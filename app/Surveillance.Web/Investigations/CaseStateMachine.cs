using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;

namespace Surveillance.Web.Investigations;

/// <summary>
/// The lifecycle of an investigation case in one place: which status changes are allowed,
/// and which status an agent event moves a case to. Every status change in the application
/// goes through <see cref="Move"/>; anything not listed here is rejected.
/// </summary>
/// <remarks>
/// Starting ─► Running ◄──────────────┐
///    │          │                    │ analyst decides
///    │          ├─► AwaitingApproval ┘
///    │          │          ├─► Expired   (final; the application stops the run, see ApprovalExpiryService)
///    └──────────┴──────────┴─► Approved | Rejected | Cancelled | Failed   (final)
/// The same diagram in Mermaid: docs/APPROVAL-STATES.md.
/// </remarks>
public static class CaseStateMachine
{
    private static readonly Dictionary<CaseStatus, CaseStatus[]> Allowed = new()
    {
        [CaseStatus.Starting] = [CaseStatus.Running, CaseStatus.Failed],
        [CaseStatus.Running] = [CaseStatus.AwaitingApproval, CaseStatus.Approved, CaseStatus.Rejected,
                                CaseStatus.Cancelled, CaseStatus.Failed],
        [CaseStatus.AwaitingApproval] = [CaseStatus.Running, CaseStatus.Cancelled, CaseStatus.Failed, CaseStatus.Expired],
        [CaseStatus.Approved] = [],
        [CaseStatus.Rejected] = [],
        [CaseStatus.Cancelled] = [],
        [CaseStatus.Failed] = [],
        [CaseStatus.Expired] = [],
    };

    /// <summary>
    /// True when <paramref name="from"/> may become <paramref name="to"/>. Staying in the same
    /// non-final status is allowed: a running case receives many events.
    /// </summary>
    public static bool CanMove(CaseStatus from, CaseStatus to) =>
        from == to ? !from.IsTerminal() : Allowed[from].Contains(to);

    /// <exception cref="InvalidCaseTransitionException">The change is not in the table.</exception>
    public static CaseStatus Move(CaseStatus from, CaseStatus to) =>
        CanMove(from, to) ? to : throw new InvalidCaseTransitionException(from, to);

    /// <summary>The status an agent event asks for; <see cref="Move"/> decides whether it is allowed.</summary>
    public static CaseStatus StatusAfter(AgentEvent e) => e.Type switch
    {
        AgentEventType.ApprovalRequired => CaseStatus.AwaitingApproval,
        AgentEventType.Error => CaseStatus.Failed,
        AgentEventType.Done => e.PayloadAs<DonePayload>().Outcome switch
        {
            RunOutcome.Approved => CaseStatus.Approved,
            RunOutcome.Rejected => CaseStatus.Rejected,
            _ => CaseStatus.Cancelled,
        },
        _ => CaseStatus.Running,
    };
}

public sealed class InvalidCaseTransitionException(CaseStatus from, CaseStatus to)
    : InvalidOperationException($"Case status cannot change from {from} to {to}.")
{
    public CaseStatus From { get; } = from;
    public CaseStatus To { get; } = to;
}
