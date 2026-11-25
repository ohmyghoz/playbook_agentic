using System.Collections.Concurrent;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Agents;
using Surveillance.Web.Data;
using Surveillance.Web.Data.Repositories;

namespace Surveillance.Web.Investigations;

/// <summary>
/// Reads a run's events from the agent and persists them, independent of any
/// browser connection. One pump at a time per case; later requests queue
/// behind the current one. Each case is read through the solution that started it.
/// </summary>
public sealed class RunPump(
    AgentClientRegistry agents,
    ICaseRepository cases,
    IEventRepository events,
    EventBroadcaster broadcaster,
    ILogger<RunPump> logger)
{
    public const string InterruptedMessage =
        "Investigasi terputus karena aplikasi atau service agent dinyalakan ulang saat agent sedang bekerja. " +
        "Data yang sudah masuk tetap tersimpan. Pilih Mulai ulang dengan data yang sama untuk menjalankannya lagi.";

    private readonly ConcurrentDictionary<Guid, CaseRun> _runs = new();
    private readonly ConcurrentDictionary<Guid, CaseCursor> _cursors = new();

    /// <summary>Streams the run until it pauses or ends.</summary>
    /// <param name="afterRestart">The application restarted while this run streamed; a failure means the run was lost.</param>
    public Task Start(Guid caseId, string runId, string provider, bool afterRestart = false)
    {
        var run = _runs.GetOrAdd(caseId, _ => new CaseRun());
        lock (run)
        {
            run.Tail = run.Tail.ContinueWith(
                _ => PumpAsync(caseId, runId, provider, afterRestart, run.Stop.Token), TaskScheduler.Default).Unwrap();
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

    private async Task PumpAsync(Guid caseId, string runId, string provider, bool afterRestart, CancellationToken stop)
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
            // After a restart the usual cause is that the agent no longer knows the run (in-memory fake,
            // or a service that restarted too): say so, and let the analyst start it again.
            if (afterRestart)
            {
                await RecordFailureAsync(caseId, runId, AgentErrorCode.RunInterrupted, InterruptedMessage);
            }
            else
            {
                await RecordFailureAsync(caseId, runId, AgentErrorCode.Internal,
                    "Terjadi kesalahan pada agent dan investigasi tidak bisa dilanjutkan. Mulai investigasi baru untuk mencoba lagi.");
            }
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

        // The approval deadline counts from the moment each draft starts waiting.
        var awaitingChanged = status != cursor.Status
                              && (status == CaseStatus.AwaitingApproval || cursor.Status == CaseStatus.AwaitingApproval);
        var progress = new CaseProgress(status, e.Sequence, awaitingChanged,
            status == CaseStatus.AwaitingApproval ? e.Timestamp : null);

        var (stored, preview) = ResultPreviewSplitter.Split(caseId, e);
        var record = EventMapping.ToRecord(caseId, stored);
        bool appended;
        try
        {
            appended = await events.AppendAsync(record, preview, progress, CancellationToken.None);
        }
        catch
        {
            _cursors.TryRemove(caseId, out _);
            throw;
        }

        if (!appended)
        {
            _cursors.TryRemove(caseId, out _); // another writer stored this sequence first
            return;
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

        return _cursors[caseId] = await cases.GetCursorAsync(caseId, CancellationToken.None);
    }

    /// <summary>
    /// Closes a run the agent can no longer report on (it threw, or never
    /// started). The application appends the error event itself so the
    /// stream still ends with a catalog event.
    /// </summary>
    public async Task RecordFailureAsync(Guid caseId, string runId, AgentErrorCode code, string message)
    {
        _cursors.TryRemove(caseId, out _); // the caller may have changed the case outside the pump
        var cursor = await cases.GetCursorAsync(caseId, CancellationToken.None);
        if (cursor.Status.IsTerminal())
        {
            return;
        }

        var error = AgentEvent.Create(runId, cursor.LastSequence + 1, AgentEventType.Error,
            new ErrorPayload(code, message, Retryable: code != AgentErrorCode.InvalidInput));
        await PersistAsync(caseId, error);
    }

    private sealed class CaseRun
    {
        public CancellationTokenSource Stop { get; } = new();
        public Task Tail { get; set; } = Task.CompletedTask;
    }
}
