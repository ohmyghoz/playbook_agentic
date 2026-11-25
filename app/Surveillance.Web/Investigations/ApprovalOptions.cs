using Surveillance.Web.Data;

namespace Surveillance.Web.Investigations;

/// <summary>Approval deadlines and claims (configuration section "Approval").</summary>
public sealed class ApprovalOptions
{
    /// <summary>A draft not decided within this time expires; the application stops the run.</summary>
    public double ExpiryHours { get; set; } = 24;

    /// <summary>The queue marks a case "hampir kedaluwarsa" this long before its deadline.</summary>
    public double WarningHours { get; set; } = 4;

    /// <summary>A claim lapses after this long without activity from its holder.</summary>
    public int ClaimTimeoutMinutes { get; set; } = 15;

    /// <summary>How often <see cref="ApprovalExpiryService"/> looks for expired approvals.</summary>
    public int CheckSeconds { get; set; } = 30;

    public TimeSpan TimeoutFor(InvestigationCase c) =>
        c.ApprovalTimeoutMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : TimeSpan.FromHours(ExpiryHours);

    /// <summary>Warning window; never more than half the deadline, so short Lab deadlines still warn first.</summary>
    public TimeSpan WarningFor(InvestigationCase c) =>
        TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(WarningHours).Ticks, TimeoutFor(c).Ticks / 2));

    public DateTime? DeadlineFor(InvestigationCase c) =>
        c.Status == CaseStatus.AwaitingApproval && c.AwaitingSince is { } since ? since + TimeoutFor(c) : null;

    /// <summary>The holder of a claim whose last activity is older than this has lost it.</summary>
    public DateTime ClaimCutoff(DateTime now) => now - TimeSpan.FromMinutes(ClaimTimeoutMinutes);

    public bool ClaimIsActive(InvestigationCase c, DateTime now) =>
        c.ClaimedBy is not null && c.ClaimActivityAt is { } at && at >= ClaimCutoff(now);
}
