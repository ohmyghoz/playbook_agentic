namespace Surveillance.Web.Data.Repositories;

/// <summary>
/// The case fields that move with an event: status, last sequence and, when a draft starts or
/// stops waiting, AwaitingSince (null in <see cref="AwaitingSince"/> clears it).
/// </summary>
public sealed record CaseProgress(CaseStatus Status, long LastSequence, bool AwaitingSinceChanged, DateTime? AwaitingSince);

/// <summary>Agent events as received, their raw tool output, and the retention work on both.</summary>
public interface IEventRepository
{
    /// <summary>
    /// Stores one event (and its preview, if any) and moves the case in one round trip.
    /// Returns false when the event was already stored (unique RunId and Sequence).
    /// </summary>
    Task<bool> AppendAsync(AgentEventRecord record, ToolResultPreview? preview, CaseProgress progress, CancellationToken ct);

    Task<List<AgentEventRecord>> GetAfterAsync(Guid caseId, long after, CancellationToken ct);

    /// <summary>The payload of the case's latest approval_required event, or null.</summary>
    Task<string?> LatestApprovalPayloadAsync(Guid caseId, CancellationToken ct);

    Task<ToolResultPreview?> GetPreviewAsync(Guid caseId, string toolCallId, CancellationToken ct);

    // ---- retention (RetentionService) ----

    Task<int> DeletePreviewsOlderThanAsync(DateTime cutoff, CancellationToken ct);

    /// <summary>Merges each run of consecutive token events of finished cases into its first event.</summary>
    Task<(int Cases, int EventsRemoved)> CompactTokensAsync(DateTime finishedBefore, CancellationToken ct);

    /// <summary>Deletes the events and previews of finished cases; the case, decisions and audit stay.</summary>
    Task<(int Cases, int EventsRemoved)> PurgeEventsAsync(DateTime finishedBefore, CancellationToken ct);
}
