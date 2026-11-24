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
    private readonly ConcurrentDictionary<Guid, CaseCursor> _cursors = new();

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
        // A decision or a stop may have changed the case since the last stream; read it again.
        _cursors.TryRemove(caseId, out _);
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

    /// <summary>
    /// Stores one event and moves the case along. One round trip per event: the case's status
    /// and last sequence are kept in memory while a pump streams it (only that pump writes it),
    /// and duplicates are caught by the unique (RunId, Sequence) index instead of a lookup.
    /// See docs/NOTES-PERSIST.md for the measurements.
    /// </summary>
    public async Task PersistAsync(Guid caseId, AgentEvent e)
    {
        var cursor = await CursorAsync(caseId);
        if (e.Sequence <= cursor.LastSequence)
        {
            return; // already stored, e.g. replayed after a reconnect
        }

        if (e.Sequence != cursor.LastSequence + 1)
        {
            logger.LogWarning("Sequence gap in run {RunId}: expected {Expected}, got {Actual}",
                e.RunId, cursor.LastSequence + 1, e.Sequence);
        }

        var wanted = CaseStateMachine.StatusAfter(e);
        var status = cursor.Status;
        if (CaseStateMachine.CanMove(status, wanted))
        {
            status = wanted;
        }
        else
        {
            logger.LogWarning("Event {Type} {Sequence} would move case {CaseId} from {From} to {To}; status kept",
                e.Type, e.Sequence, caseId, cursor.Status, wanted);
        }

        var (stored, preview) = ResultPreviewSplitter.Split(caseId, e);
        var record = EventMapping.ToRecord(caseId, stored);
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Events.Add(record);
        if (preview is not null)
        {
            db.Previews.Add(preview);
        }

        // Update only the columns that change, without reading the row first.
        var stub = new InvestigationCase { Id = caseId, Symbol = "", TriggerJson = "", Provider = "" };
        db.Cases.Attach(stub);
        stub.Status = status;
        stub.LastSequence = e.Sequence;
        stub.UpdatedAt = DateTime.UtcNow;
        var entry = db.Entry(stub);
        entry.Property(x => x.Status).IsModified = true;
        entry.Property(x => x.LastSequence).IsModified = true;
        entry.Property(x => x.UpdatedAt).IsModified = true;

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _cursors.TryRemove(caseId, out _);
            await using var check = await dbFactory.CreateDbContextAsync();
            if (await check.Events.AnyAsync(x => x.RunId == e.RunId && x.Sequence == e.Sequence))
            {
                return; // another writer stored this sequence first (unique index)
            }

            throw;
        }

        _cursors[caseId] = new CaseCursor(status, e.Sequence);
        broadcaster.Publish(record);
    }

    private async Task<CaseCursor> CursorAsync(Guid caseId)
    {
        if (_cursors.TryGetValue(caseId, out var cursor))
        {
            return cursor;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.Cases.AsNoTracking().Where(c => c.Id == caseId)
            .Select(c => new CaseCursor(c.Status, c.LastSequence)).SingleAsync();
        return _cursors[caseId] = row;
    }

    private sealed record CaseCursor(CaseStatus Status, long LastSequence);

    /// <summary>
    /// Closes a run the agent can no longer report on (it threw, or never
    /// started). The application appends the error event itself so the
    /// stream still ends with a catalog event.
    /// </summary>
    public async Task RecordFailureAsync(Guid caseId, string runId, AgentErrorCode code, string message)
    {
        _cursors.TryRemove(caseId, out _); // the caller may have changed the case outside the pump
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

    private sealed class CaseRun
    {
        public CancellationTokenSource Stop { get; } = new();
        public Task Tail { get; set; } = Task.CompletedTask;
    }
}
