using Microsoft.Extensions.Options;
using Surveillance.Web.Data;
using Surveillance.Web.Data.Repositories;

namespace Surveillance.Web.Investigations;

public enum ClaimResult
{
    Claimed,
    HeldByOther,
    NotAwaitingApproval,
    CaseNotFound,
}

/// <param name="Holder">The analyst who holds the case after the call (display name), if anyone.</param>
public sealed record ClaimOutcome(ClaimResult Result, string? Holder = null);

/// <summary>Who holds a case now, for the page and the queue.</summary>
public sealed record ClaimState(string? Holder, string? HolderUser, DateTime? ActivityAt, DateTime? ExpiresAt);

/// <summary>
/// An analyst claims a case that waits for approval before deciding it, so two analysts do not
/// review the same draft. A claim lapses by itself after Approval:ClaimTimeoutMinutes without
/// activity, and a supervisor may assign the case to someone else. Every change is a conditional
/// update (<see cref="IApprovalRepository"/>), like <see cref="InvestigationService.DecideAsync"/>,
/// and is written to the audit trail.
/// </summary>
public sealed class CaseClaims(ICaseRepository cases, IApprovalRepository approvals, IOptions<ApprovalOptions> options)
{
    private ApprovalOptions Options => options.Value;

    /// <summary>Takes the case, or renews the caller's own claim (also used as an activity heartbeat).</summary>
    public async Task<ClaimOutcome> ClaimAsync(Guid caseId, string user, string displayName, CancellationToken ct)
    {
        var before = await cases.GetAsync(caseId, ct);
        if (before is null)
        {
            return new(ClaimResult.CaseNotFound);
        }

        var now = DateTime.UtcNow;
        if (await approvals.TryClaimAsync(caseId, user, displayName, Options.ClaimCutoff(now), ct))
        {
            var previous = StateOf(before, now).Holder;
            if (previous != displayName)
            {
                await AuditAsync(caseId, displayName, "claim",
                    before.ClaimedBy is { } lapsed && lapsed != displayName ? $"Mengambil alih dari {lapsed} yang tidak aktif." : null, ct);
            }

            return new(ClaimResult.Claimed, displayName);
        }

        return await RefusedAsync(caseId, ct);
    }

    public async Task<ClaimOutcome> ReleaseAsync(Guid caseId, string displayName, CancellationToken ct)
    {
        if (await approvals.TryReleaseAsync(caseId, displayName, ct))
        {
            await AuditAsync(caseId, displayName, "release", null, ct);
            return new(ClaimResult.Claimed);
        }

        return await RefusedAsync(caseId, ct);
    }

    /// <summary>A supervisor hands the case to another analyst, whoever holds it now.</summary>
    public async Task<ClaimOutcome> AssignAsync(Guid caseId, string supervisor, string user, string displayName, CancellationToken ct)
    {
        var before = await cases.GetAsync(caseId, ct);
        if (before is null)
        {
            return new(ClaimResult.CaseNotFound);
        }

        if (!await approvals.TryAssignAsync(caseId, user, displayName, ct))
        {
            return new(ClaimResult.NotAwaitingApproval);
        }

        var previous = StateOf(before, DateTime.UtcNow).Holder;
        await AuditAsync(caseId, supervisor, "assign",
            previous is null ? $"Ditugaskan ke {displayName}." : $"Dialihkan dari {previous} ke {displayName}.", ct);
        return new(ClaimResult.Claimed, displayName);
    }

    public async Task<ClaimState?> GetAsync(Guid caseId, CancellationToken ct)
    {
        var c = await cases.GetAsync(caseId, ct);
        return c is null ? null : StateOf(c, DateTime.UtcNow);
    }

    public ClaimState StateOf(InvestigationCase c, DateTime now) => Options.ClaimIsActive(c, now)
        ? new ClaimState(c.ClaimedBy, c.ClaimedByUser, c.ClaimActivityAt, c.ClaimActivityAt!.Value.AddMinutes(Options.ClaimTimeoutMinutes))
        : new ClaimState(null, null, null, null);

    private async Task<ClaimOutcome> RefusedAsync(Guid caseId, CancellationToken ct)
    {
        var c = (await cases.GetAsync(caseId, ct))!;
        return c.Status != CaseStatus.AwaitingApproval
            ? new(ClaimResult.NotAwaitingApproval)
            : new(ClaimResult.HeldByOther, StateOf(c, DateTime.UtcNow).Holder);
    }

    private Task AuditAsync(Guid caseId, string actor, string action, string? detail, CancellationToken ct) =>
        approvals.AddAuditAsync(new CaseAuditRecord { CaseId = caseId, At = DateTime.UtcNow, Actor = actor, Action = action, Detail = detail }, ct);
}
