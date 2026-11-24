using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Agents;
using Surveillance.Web.Data;

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
    string? Notes);

public enum DecisionResult
{
    Accepted,
    CaseNotFound,
    NotAwaitingApproval,
    StaleApproval,
    CommentRequired,
    AlreadyDecided,
}

/// <param name="DecidedBy">Who made the decision that won, when the result is <see cref="DecisionResult.AlreadyDecided"/>.</param>
public sealed record DecisionOutcome(DecisionResult Result, string? DecidedBy = null);

/// <summary>
/// Everything controllers need for UMA investigations. Knows only
/// <see cref="IAgentClient"/>, never a concrete agent framework.
/// </summary>
public sealed class InvestigationService(
    AgentClientRegistry agents,
    AgentSelection selection,
    IDbContextFactory<SurveillanceDbContext> dbFactory,
    RunPump pump,
    EventBroadcaster broadcaster,
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
            Status = CaseStatus.Starting,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Cases.Add(@case);
            await db.SaveChangesAsync(ct);
        }

        var request = new InvestigationRequest(@case.Id.ToString(), @case.Symbol, @case.AnomalyDate, trigger, @case.Notes);
        string runId;
        try
        {
            runId = await agents.Get(@case.Provider).StartAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent {Provider} failed to start case {CaseId}", @case.Provider, @case.Id);
            await SetRunIdAsync(@case.Id, $"failed-{@case.Id:N}");
            // InvalidOperationException: the chosen solution cannot be created (missing API key or URL).
            var (code, message) = ex is InvalidOperationException
                ? (AgentErrorCode.Internal, "Solusi agent yang dipilih belum siap. Periksa halaman Setting, lalu coba lagi.")
                : (AgentErrorCode.McpUnavailable, "Agent tidak bisa dimulai. Periksa koneksi ke service agent dan server data pasar, lalu coba lagi.");
            await pump.RecordFailureAsync(@case.Id, $"failed-{@case.Id:N}", code, message);
            return @case.Id;
        }

        await SetRunIdAsync(@case.Id, runId);
        _ = pump.Start(@case.Id, runId, @case.Provider);
        return @case.Id;
    }

    /// <summary>
    /// Records an analyst's decision and resumes the agent. When two analysts decide the same
    /// approval at once, exactly one wins: the case is claimed with a conditional update
    /// (AwaitingApproval → Running) and only the request that changed the row goes on.
    /// The other gets <see cref="DecisionResult.AlreadyDecided"/> and never reaches the agent.
    /// </summary>
    public async Task<DecisionOutcome> DecideAsync(Guid caseId, string approvalId, Decision decision,
        string? comment, string decidedBy, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var @case = await db.Cases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == caseId, ct);
        if (@case is null)
        {
            return new(DecisionResult.CaseNotFound);
        }

        if (@case.Status != CaseStatus.AwaitingApproval || @case.RunId is null)
        {
            return await WinnerOfAsync(db, caseId, approvalId, ct) is { } winner
                ? new(DecisionResult.AlreadyDecided, winner)
                : new(DecisionResult.NotAwaitingApproval);
        }

        if (decision != Decision.Approve && string.IsNullOrWhiteSpace(comment))
        {
            return new(DecisionResult.CommentRequired);
        }

        var latest = await db.Events
            .Where(e => e.CaseId == caseId && e.Type == "approval_required")
            .OrderByDescending(e => e.Sequence)
            .Select(e => e.PayloadJson)
            .FirstAsync(ct);
        var pending = JsonSerializer.Deserialize<ApprovalRequiredPayload>(latest, AgentJson.Options)!;
        if (pending.ApprovalId != approvalId)
        {
            return new(DecisionResult.StaleApproval);
        }

        var normalizedComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        var running = CaseStateMachine.Move(CaseStatus.AwaitingApproval, CaseStatus.Running);

        // A user transaction must run inside the execution strategy when retry on failure is on.
        var claimed = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.Cases
                .Where(c => c.Id == caseId && c.Status == CaseStatus.AwaitingApproval)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, running)
                    .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), ct);
            if (changed == 0)
            {
                return false; // someone else claimed it between our read and this update
            }

            db.Decisions.Add(new ApprovalDecisionRecord
            {
                CaseId = caseId,
                ApprovalId = approvalId,
                Decision = EventMapping.EnumName(decision),
                Comment = normalizedComment,
                DecidedBy = decidedBy.Trim(),
                DecidedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });

        if (!claimed)
        {
            logger.LogInformation("Decision on case {CaseId} by {DecidedBy} lost the race", caseId, decidedBy);
            return new(DecisionResult.AlreadyDecided, await WinnerOfAsync(db, caseId, approvalId, ct));
        }

        try
        {
            // The case keeps the solution that started it, whatever the settings page says now.
            await agents.Get(@case.Provider).ResumeAsync(@case.RunId,
                new ApprovalDecision(approvalId, decision, decidedBy.Trim(), normalizedComment), ct);
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

    private static Task<string?> WinnerOfAsync(SurveillanceDbContext db, Guid caseId, string approvalId, CancellationToken ct) =>
        db.Decisions.AsNoTracking()
            .Where(d => d.CaseId == caseId && d.ApprovalId == approvalId)
            .Select(d => d.DecidedBy)
            .FirstOrDefaultAsync(ct);

    public async Task StopAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var @case = await db.Cases.SingleOrDefaultAsync(c => c.Id == caseId, ct);
        if (@case is { RunId: not null } && !@case.Status.IsTerminal())
        {
            _ = pump.Stop(caseId, @case.RunId, @case.Provider);
        }
    }

    public async Task<InvestigationCase?> GetCaseAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Cases.AsNoTracking()
            .Include(c => c.Decisions.OrderBy(d => d.DecidedAt))
            .SingleOrDefaultAsync(c => c.Id == caseId, ct);
    }

    public async Task<List<InvestigationCase>> ListCasesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Cases.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(100).ToListAsync(ct);
    }

    public async Task<List<AgentEventRecord>> GetEventsAsync(Guid caseId, long after, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Events.AsNoTracking()
            .Where(e => e.CaseId == caseId && e.Sequence > after)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
    }

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

    /// <summary>Re-attaches pumps to runs that were streaming when the application stopped.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var active = await db.Cases
            .Where(c => c.RunId != null && (c.Status == CaseStatus.Running || c.Status == CaseStatus.Starting))
            .Select(c => new { c.Id, c.RunId, c.Provider })
            .ToListAsync(ct);
        foreach (var c in active)
        {
            _ = pump.Start(c.Id, c.RunId!, c.Provider);
        }
    }

    private async Task SetRunIdAsync(Guid caseId, string runId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var @case = await db.Cases.SingleAsync(c => c.Id == caseId);
        @case.RunId = runId;
        @case.Status = CaseStateMachine.Move(@case.Status, CaseStatus.Running);
        @case.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
