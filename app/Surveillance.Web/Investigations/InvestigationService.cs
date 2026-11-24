using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
}

/// <summary>
/// Everything controllers need for UMA investigations. Knows only
/// <see cref="IAgentClient"/>, never a concrete agent framework.
/// </summary>
public sealed class InvestigationService(
    IAgentClient agent,
    IDbContextFactory<SurveillanceDbContext> dbFactory,
    RunPump pump,
    EventBroadcaster broadcaster,
    IOptions<AgentOptions> agentOptions,
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
            Provider = agentOptions.Value.Provider,
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
            runId = await agent.StartAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent failed to start case {CaseId}", @case.Id);
            await SetRunIdAsync(@case.Id, $"failed-{@case.Id:N}");
            await pump.RecordFailureAsync(@case.Id, $"failed-{@case.Id:N}", AgentErrorCode.McpUnavailable,
                "Agent tidak bisa dimulai. Periksa koneksi ke server data pasar, lalu coba lagi.");
            return @case.Id;
        }

        await SetRunIdAsync(@case.Id, runId);
        _ = pump.Start(@case.Id, runId);
        return @case.Id;
    }

    public async Task<DecisionResult> DecideAsync(Guid caseId, string approvalId, Decision decision,
        string? comment, string decidedBy, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var @case = await db.Cases.SingleOrDefaultAsync(c => c.Id == caseId, ct);
        if (@case is null)
        {
            return DecisionResult.CaseNotFound;
        }

        if (@case.Status != CaseStatus.AwaitingApproval || @case.RunId is null)
        {
            return DecisionResult.NotAwaitingApproval;
        }

        if (decision != Decision.Approve && string.IsNullOrWhiteSpace(comment))
        {
            return DecisionResult.CommentRequired;
        }

        var latest = await db.Events
            .Where(e => e.CaseId == caseId && e.Type == "approval_required")
            .OrderByDescending(e => e.Sequence)
            .Select(e => e.PayloadJson)
            .FirstAsync(ct);
        var pending = JsonSerializer.Deserialize<ApprovalRequiredPayload>(latest, AgentJson.Options)!;
        if (pending.ApprovalId != approvalId)
        {
            return DecisionResult.StaleApproval;
        }

        var normalizedComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        db.Decisions.Add(new ApprovalDecisionRecord
        {
            CaseId = caseId,
            ApprovalId = approvalId,
            Decision = EventMapping.EnumName(decision),
            Comment = normalizedComment,
            DecidedBy = decidedBy.Trim(),
            DecidedAt = DateTime.UtcNow,
        });
        @case.Status = CaseStatus.Running;
        @case.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            await agent.ResumeAsync(@case.RunId, new ApprovalDecision(approvalId, decision, decidedBy.Trim(), normalizedComment), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent failed to resume case {CaseId}", caseId);
            await pump.RecordFailureAsync(caseId, @case.RunId, AgentErrorCode.Internal,
                "Agent tidak bisa melanjutkan investigasi ini. Keputusan Anda sudah tersimpan. Mulai investigasi baru untuk mencoba lagi.");
            return DecisionResult.Accepted;
        }

        _ = pump.Start(caseId, @case.RunId);
        return DecisionResult.Accepted;
    }

    public async Task StopAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var @case = await db.Cases.SingleOrDefaultAsync(c => c.Id == caseId, ct);
        if (@case is { RunId: not null } && !@case.Status.IsTerminal())
        {
            _ = pump.Stop(caseId, @case.RunId);
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
            .Select(c => new { c.Id, c.RunId })
            .ToListAsync(ct);
        foreach (var c in active)
        {
            _ = pump.Start(c.Id, c.RunId!);
        }
    }

    private async Task SetRunIdAsync(Guid caseId, string runId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var @case = await db.Cases.SingleAsync(c => c.Id == caseId);
        @case.RunId = runId;
        @case.Status = CaseStatus.Running;
        @case.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
