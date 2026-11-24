using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Agents;
using Surveillance.Web.Data;

namespace Surveillance.Web.Investigations;

/// <summary>
/// Reads a run's events from the agent and persists them, independent of any
/// browser connection. One pump at a time per case; later requests queue
/// behind the current one. Each case is read through the solution that started it.
/// </summary>
public sealed class RunPump(
    AgentClientRegistry agents,
    IDbContextFactory<SurveillanceDbContext> dbFactory,
    EventBroadcaster broadcaster,
    ILogger<RunPump> logger)
{
    private readonly ConcurrentDictionary<Guid, CaseRun> _runs = new();

    /// <summary>Streams the run until it pauses or ends.</summary>
    public Task Start(Guid caseId, string runId, string provider)
    {
        var run = _runs.GetOrAdd(caseId, _ => new CaseRun());
        lock (run)
        {
            run.Tail = run.Tail.ContinueWith(
                _ => PumpAsync(caseId, runId, provider, run.Stop.Token), TaskScheduler.Default).Unwrap();
            return run.Tail;
        }
    }

    /// <summary>
    /// Stops the run: cancels the active stream, or, when the run is paused,
    /// opens a stream with a cancelled token so the agent ends it.
    /// </summary>
    public Task Stop(Guid caseId, string runId, string provider)
    {
        var run = _runs.GetOrAdd(caseId, _ => new CaseRun());
        run.Stop.Cancel();
        return Start(caseId, runId, provider);
    }

    /// <summary>Completes when every queued pump for the case has finished.</summary>
    public Task WhenIdle(Guid caseId) =>
        _runs.TryGetValue(caseId, out var run) ? run.Tail : Task.CompletedTask;

    private async Task PumpAsync(Guid caseId, string runId, string provider, CancellationToken stop)
    {
        try
        {
            await foreach (var e in agents.Get(provider).StreamAsync(runId, stop))
            {
                await PersistAsync(caseId, e);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent stream failed for case {CaseId}, run {RunId}", caseId, runId);
            await RecordFailureAsync(caseId, runId, AgentErrorCode.Internal,
                "Terjadi kesalahan pada agent dan investigasi tidak bisa dilanjutkan. Mulai investigasi baru untuk mencoba lagi.");
        }
    }

    public async Task PersistAsync(Guid caseId, AgentEvent e)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var @case = await db.Cases.SingleAsync(c => c.Id == caseId);

        if (await db.Events.AnyAsync(x => x.RunId == e.RunId && x.Sequence == e.Sequence))
        {
            return;
        }

        if (e.Sequence != @case.LastSequence + 1)
        {
            logger.LogWarning("Sequence gap in run {RunId}: expected {Expected}, got {Actual}",
                e.RunId, @case.LastSequence + 1, e.Sequence);
        }

        var record = EventMapping.ToRecord(caseId, e);
        db.Events.Add(record);
        @case.LastSequence = Math.Max(@case.LastSequence, e.Sequence);
        @case.Status = NextStatus(@case.Status, e);
        @case.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        broadcaster.Publish(record);
    }

    /// <summary>
    /// Closes a run the agent can no longer report on (it threw, or never
    /// started). The application appends the error event itself so the
    /// stream still ends with a catalog event.
    /// </summary>
    public async Task RecordFailureAsync(Guid caseId, string runId, AgentErrorCode code, string message)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var @case = await db.Cases.SingleAsync(c => c.Id == caseId);
        if (@case.Status.IsTerminal())
        {
            return;
        }

        var error = AgentEvent.Create(runId, @case.LastSequence + 1, AgentEventType.Error,
            new ErrorPayload(code, message, Retryable: code != AgentErrorCode.InvalidInput));
        await PersistAsync(caseId, error);
    }

    private static CaseStatus NextStatus(CaseStatus current, AgentEvent e) => e.Type switch
    {
        AgentEventType.ApprovalRequired => CaseStatus.AwaitingApproval,
        AgentEventType.Error => CaseStatus.Failed,
        AgentEventType.Done => e.PayloadAs<DonePayload>().Outcome switch
        {
            RunOutcome.Approved => CaseStatus.Approved,
            RunOutcome.Rejected => CaseStatus.Rejected,
            _ => CaseStatus.Cancelled,
        },
        _ when current.IsTerminal() => current,
        _ => CaseStatus.Running,
    };

    private sealed class CaseRun
    {
        public CancellationTokenSource Stop { get; } = new();
        public Task Tail { get; set; } = Task.CompletedTask;
    }
}
