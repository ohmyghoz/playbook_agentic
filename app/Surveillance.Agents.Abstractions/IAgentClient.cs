namespace Surveillance.Agents.Abstractions;

/// <summary>
/// The single contract the application uses to talk to any agent solution.
/// Implementations map their framework's native events to the catalog in
/// contracts/events.schema.json; callers never see framework types.
/// </summary>
public interface IAgentClient
{
    /// <summary>
    /// Creates a run and returns its id. No events are produced until
    /// <see cref="StreamAsync"/> is called.
    /// </summary>
    Task<string> StartAsync(InvestigationRequest request, CancellationToken ct);

    /// <summary>
    /// Yields the run's events that have not been yielded before, in sequence
    /// order, until the run pauses at <c>approval_required</c> or terminates
    /// with <c>done</c> or <c>error</c>. Call again after
    /// <see cref="ResumeAsync"/> to receive the events that follow.
    /// </summary>
    /// <remarks>
    /// Cancelling <paramref name="ct"/> means the user stopped the run: the
    /// implementation closes any in-flight tool call with a failed
    /// <c>tool_call_finished</c>, yields <c>done</c> with outcome
    /// <c>cancelled</c>, and completes. Calling it with an already cancelled
    /// token on a paused run cancels that run the same way.
    /// </remarks>
    IAsyncEnumerable<AgentEvent> StreamAsync(string runId, CancellationToken ct);

    /// <summary>
    /// Delivers the analyst's decision for the pending approval. Throws
    /// <see cref="InvalidOperationException"/> when the run is not waiting
    /// for that approval.
    /// </summary>
    Task ResumeAsync(string runId, ApprovalDecision decision, CancellationToken ct);
}
