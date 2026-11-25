using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Agents;
using Surveillance.Web.Data;
using Surveillance.Web.Data.Repositories;

namespace Surveillance.Web.Investigations;

public sealed record StartInvestigation(
    string Symbol,
    DateOnly AnomalyDate,
    TriggerKind TriggerKind,
    string Rule,
    double? PriceChangePct,
    long? Volume,
    double? AvgVolume,
    double? VolumeRatio,
    string? Notes,
    int? ApprovalTimeoutMinutes = null);

public enum DecisionResult
{
    Accepted,
    CaseNotFound,
    NotAwaitingApproval,
    StaleApproval,
    CommentRequired,
    AlreadyDecided,

    /// <summary>Another analyst holds the case (see <see cref="CaseClaims"/>).</summary>
    ClaimedByOther,
}

/// <param name="DecidedBy">Who made the decision that won (AlreadyDecided), or who holds the case (ClaimedByOther).</param>
public sealed record DecisionOutcome(DecisionResult Result, string? DecidedBy = null);

/// <summary>
/// Everything controllers need for UMA investigations. Knows only
/// <see cref="IAgentClient"/>, never a concrete agent framework, and reaches the
/// database only through the repositories in Data/Repositories.
/// </summary>
public sealed class InvestigationService(
    AgentClientRegistry agents,
    AgentSelection selection,
    ICaseRepository cases,
    IEventRepository events,
    IApprovalRepository approvals,
    RunPump pump,
    EventBroadcaster broadcaster,
    IOptions<ApprovalOptions> approval,
    ILogger<InvestigationService> logger)
{
    public async Task<Guid> StartAsync(StartInvestigation input, CancellationToken ct)
    {
        var trigger = new AnomalyTrigger(input.TriggerKind, input.Rule.Trim(), input.PriceChangePct,
            input.Volume, input.AvgVolume, input.VolumeRatio);
        var now = DateTime.UtcNow;
        var @case = new InvestigationCase
        {
            Id = Guid.NewGuid(),
            Symbol = input.Symbol.Trim().ToUpperInvariant(),
            AnomalyDate = input.AnomalyDate,
            TriggerJson = JsonSerializer.Serialize(trigger, AgentJson.Options),
            Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim(),
            Provider = await selection.GetActiveAsync(ct),
            ApprovalTimeoutMinutes = input.ApprovalTimeoutMinutes,
            Status = CaseStatus.Starting,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await cases.AddAsync(@case, ct);
        await LaunchAsync(@case, new InvestigationRequest(@case.Id.ToString(), @case.Symbol, @case.AnomalyDate, trigger, @case.Notes), ct);
        return @case.Id;
    }

    /// <summary>Creates a use case 2 job in Starting; <see cref="Patterns.PatternJobRunner"/> screens and launches it.</summary>
    public async Task<Guid> CreateJobAsync(string user, string displayName, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var trigger = new AnomalyTrigger(TriggerKind.Price, "Penyaringan pola: perubahan harga besar dalam 10 hari bursa");
        var @case = new InvestigationCase
        {
            Id = Guid.NewGuid(),
            Symbol = PatternJobs.JobSymbol,
            AnomalyDate = DateOnly.FromDateTime(now),
            TriggerJson = JsonSerializer.Serialize(trigger, AgentJson.Options),
            Provider = await selection.GetActiveAsync(ct),
            UseCase = PatternJobs.UseCaseName,
            StartedBy = displayName,
            StartedByUser = user,
            Status = CaseStatus.Starting,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await cases.AddAsync(@case, ct);
        return @case.Id;
    }

    /// <summary>
    /// Starts the agent run for a stored case and attaches the pump. A failure to start ends the
    /// case with an error event, so every case still finishes with a catalog event.
    /// </summary>
    public async Task LaunchAsync(InvestigationCase @case, InvestigationRequest request, CancellationToken ct)
    {
        string runId;
        try
        {
            runId = await agents.Get(@case.Provider).StartAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent {Provider} failed to start case {CaseId}", @case.Provider, @case.Id);
            await cases.SetRunIdAsync(@case.Id, $"failed-{@case.Id:N}", CancellationToken.None);
            // InvalidOperationException: the chosen solution cannot be created (missing API key or URL).
            var (code, message) = ex is InvalidOperationException
                ? (AgentErrorCode.Internal, "Solusi agent yang dipilih belum siap. Periksa halaman Setting, lalu coba lagi.")
                : (AgentErrorCode.McpUnavailable, "Agent tidak bisa dimulai. Periksa koneksi ke service agent dan server data pasar, lalu coba lagi.");
            await pump.RecordFailureAsync(@case.Id, $"failed-{@case.Id:N}", code, message);
            return;
        }

        await cases.SetRunIdAsync(@case.Id, runId, CancellationToken.None);
        _ = pump.Start(@case.Id, runId, @case.Provider);
    }

    /// <summary>Ends a case that never reached the agent (for example, screening found nothing).</summary>
    public async Task FailBeforeStartAsync(Guid caseId, AgentErrorCode code, string message)
    {
        var runId = $"failed-{caseId:N}";
        await cases.SetRunIdAsync(caseId, runId, CancellationToken.None);
        await pump.RecordFailureAsync(caseId, runId, code, message);
    }

    public Task<List<InvestigationCase>> ListJobsAsync(CancellationToken ct) => cases.ListJobsAsync(100, ct);

    public Task<int> CountJobsAwaitingAsync(string user, CancellationToken ct) => cases.CountJobsAwaitingAsync(user, ct);

    /// <summary>
    /// Records an analyst's decision and resumes the agent. When two analysts decide the same
    /// approval at once, exactly one wins: the case is claimed with a conditional update
    /// (AwaitingApproval → Running) and only the request that changed the row goes on.
    /// The other gets <see cref="DecisionResult.AlreadyDecided"/> and never reaches the agent.
    /// A case held by another analyst (active claim, <see cref="CaseClaims"/>) is refused with
    /// <see cref="DecisionResult.ClaimedByOther"/>; an unclaimed case is decided directly, so the
    /// claim step is a UI convention backed by the server, not a second gate that can lose decisions.
    /// </summary>
    public async Task<DecisionOutcome> DecideAsync(Guid caseId, string approvalId, Decision decision,
        string? comment, string decidedBy, CancellationToken ct)
    {
        var @case = await cases.GetAsync(caseId, ct);
        if (@case is null)
        {
            return new(DecisionResult.CaseNotFound);
        }

        if (@case.Status != CaseStatus.AwaitingApproval || @case.RunId is null)
        {
            return await approvals.WinnerOfAsync(caseId, approvalId, ct) is { } winner
                ? new(DecisionResult.AlreadyDecided, winner)
                : new(DecisionResult.NotAwaitingApproval);
        }

        if (decision != Decision.Approve && string.IsNullOrWhiteSpace(comment))
        {
            return new(DecisionResult.CommentRequired);
        }

        var latest = await events.LatestApprovalPayloadAsync(caseId, ct);
        var pending = JsonSerializer.Deserialize<ApprovalRequiredPayload>(latest!, AgentJson.Options)!;
        if (pending.ApprovalId != approvalId)
        {
            return new(DecisionResult.StaleApproval);
        }

        var normalizedComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        var who = decidedBy.Trim();
        var record = new ApprovalDecisionRecord
        {
            CaseId = caseId,
            ApprovalId = approvalId,
            Decision = EventMapping.EnumName(decision),
            Comment = normalizedComment,
            DecidedBy = who,
            DecidedAt = DateTime.UtcNow,
        };
        var running = CaseStateMachine.Move(CaseStatus.AwaitingApproval, CaseStatus.Running);
        if (!await approvals.TryDecideAsync(record, running, approval.Value.ClaimCutoff(DateTime.UtcNow), ct))
        {
            var now = await cases.GetAsync(caseId, ct);
            if (now is { Status: CaseStatus.AwaitingApproval } && approval.Value.ClaimIsActive(now, DateTime.UtcNow) && now.ClaimedBy != who)
            {
                return new(DecisionResult.ClaimedByOther, now.ClaimedBy);
            }

            logger.LogInformation("Decision on case {CaseId} by {DecidedBy} lost the race", caseId, decidedBy);
            return new(DecisionResult.AlreadyDecided, await approvals.WinnerOfAsync(caseId, approvalId, ct));
        }

        try
        {
            // The case keeps the solution that started it, whatever the settings page says now.
            await agents.Get(@case.Provider).ResumeAsync(@case.RunId,
                new ApprovalDecision(approvalId, decision, who, normalizedComment), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent failed to resume case {CaseId}", caseId);
            await pump.RecordFailureAsync(caseId, @case.RunId, AgentErrorCode.Internal,
                "Agent tidak bisa melanjutkan investigasi ini. Keputusan Anda sudah tersimpan. Mulai investigasi baru untuk mencoba lagi.");
            return new(DecisionResult.Accepted);
        }

        _ = pump.Start(caseId, @case.RunId, @case.Provider);
        return new(DecisionResult.Accepted);
    }

    public async Task StopAsync(Guid caseId, CancellationToken ct)
    {
        var @case = await cases.GetAsync(caseId, ct);
        if (@case is { RunId: not null } && !@case.Status.IsTerminal())
        {
            _ = pump.Stop(caseId, @case.RunId, @case.Provider);
        }
    }

    public Task<InvestigationCase?> GetCaseAsync(Guid caseId, CancellationToken ct) => cases.GetAsync(caseId, ct);

    /// <summary>Cases waiting for an analyst, the longest waiting first.</summary>
    public Task<List<InvestigationCase>> ListAwaitingApprovalAsync(CancellationToken ct) => cases.ListAwaitingApprovalAsync(ct);

    /// <summary>UMA investigations only; jobs are listed on /jobs.</summary>
    public Task<List<InvestigationCase>> ListCasesAsync(CancellationToken ct) => cases.ListInvestigationsAsync(100, ct);

    /// <summary>The raw output of one tool call, for the evidence view. Null when the agent sent none.</summary>
    public Task<ToolResultPreview?> GetPreviewAsync(Guid caseId, string toolCallId, CancellationToken ct) =>
        events.GetPreviewAsync(caseId, toolCallId, ct);

    public Task<List<AgentEventRecord>> GetEventsAsync(Guid caseId, long after, CancellationToken ct) =>
        events.GetAfterAsync(caseId, after, ct);

    /// <summary>
    /// Replays stored events after <paramref name="after"/>, then follows live
    /// events until the run ends. Subscribes before reading the database so no
    /// event falls between the two.
    /// </summary>
    public async IAsyncEnumerable<AgentEventRecord> StreamEventsAsync(Guid caseId, long after,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var subscription = broadcaster.Subscribe(caseId);
        var last = after;

        foreach (var record in await GetEventsAsync(caseId, after, ct))
        {
            yield return record;
            last = record.Sequence;
            if (EventMapping.IsTerminal(record))
            {
                yield break;
            }
        }

        var @case = await GetCaseAsync(caseId, ct);
        if (@case is null || @case.Status.IsTerminal() && @case.LastSequence <= last)
        {
            yield break;
        }

        await foreach (var record in subscription.Reader.ReadAllAsync(ct))
        {
            if (record.Sequence <= last)
            {
                continue;
            }

            yield return record;
            last = record.Sequence;
            if (EventMapping.IsTerminal(record))
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Re-attaches pumps to runs that were streaming when the application stopped. A run the
    /// agent no longer knows ends with run_interrupted, which the analyst can retry (docs/RESTART.md).
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        foreach (var run in await cases.ListActiveRunsAsync(ct))
        {
            _ = pump.Start(run.CaseId, run.RunId, run.Provider, afterRestart: true);
        }
    }
}
