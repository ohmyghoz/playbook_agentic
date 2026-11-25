using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Surveillance.Web.Data;

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
/// update, like <see cref="InvestigationService.DecideAsync"/>, and is written to the audit trail.
/// </summary>
public sealed class CaseClaims(IDbContextFactory<SurveillanceDbContext> dbFactory, IOptions<ApprovalOptions> options)
{
    private ApprovalOptions Options => options.Value;

    /// <summary>Takes the case, or renews the caller's own claim (also used as an activity heartbeat).</summary>
    public async Task<ClaimOutcome> ClaimAsync(Guid caseId, string user, string displayName, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cutoff = Options.ClaimCutoff(now);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var held = await db.Cases.AsNoTracking().Where(c => c.Id == caseId)
            .Select(c => new { c.ClaimedBy }).SingleOrDefaultAsync(ct);
        if (held is null)
        {
            return new(ClaimResult.CaseNotFound);
        }

        var changed = await db.Cases
            .Where(c => c.Id == caseId && c.Status == CaseStatus.AwaitingApproval
                        && (c.ClaimedBy == null || c.ClaimedBy == displayName || c.ClaimActivityAt < cutoff))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ClaimedBy, displayName)
                .SetProperty(c => c.ClaimedByUser, user)
                .SetProperty(c => c.ClaimActivityAt, now), ct);
        if (changed == 1)
        {
            if (held.ClaimedBy != displayName)
            {
                await AuditAsync(db, caseId, displayName, "claim", held.ClaimedBy is null ? null : $"Mengambil alih dari {held.ClaimedBy} yang tidak aktif.", ct);
            }

            return new(ClaimResult.Claimed, displayName);
        }

        return await RefusedAsync(db, caseId, ct);
    }

    public async Task<ClaimOutcome> ReleaseAsync(Guid caseId, string displayName, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Cases
            .Where(c => c.Id == caseId && c.ClaimedBy == displayName)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ClaimedBy, (string?)null)
                .SetProperty(c => c.ClaimedByUser, (string?)null)
                .SetProperty(c => c.ClaimActivityAt, (DateTime?)null), ct);
        if (changed == 1)
        {
            await AuditAsync(db, caseId, displayName, "release", null, ct);
            return new(ClaimResult.Claimed);
        }

        return await RefusedAsync(db, caseId, ct);
    }

    /// <summary>A supervisor hands the case to another analyst, whoever holds it now.</summary>
    public async Task<ClaimOutcome> AssignAsync(Guid caseId, string supervisor, string user, string displayName, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var before = await db.Cases.AsNoTracking().Where(c => c.Id == caseId).Select(c => new { c.ClaimedBy }).SingleOrDefaultAsync(ct);
        if (before is null)
        {
            return new(ClaimResult.CaseNotFound);
        }

        var changed = await db.Cases
            .Where(c => c.Id == caseId && c.Status == CaseStatus.AwaitingApproval)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ClaimedBy, displayName)
                .SetProperty(c => c.ClaimedByUser, user)
                .SetProperty(c => c.ClaimActivityAt, DateTime.UtcNow), ct);
        if (changed == 0)
        {
            return new(ClaimResult.NotAwaitingApproval);
        }

        await AuditAsync(db, caseId, supervisor, "assign",
            before.ClaimedBy is null ? $"Ditugaskan ke {displayName}." : $"Dialihkan dari {before.ClaimedBy} ke {displayName}.", ct);
        return new(ClaimResult.Claimed, displayName);
    }

    public async Task<ClaimState?> GetAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var c = await db.Cases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId, ct);
        return c is null ? null : StateOf(c, DateTime.UtcNow);
    }

    public ClaimState StateOf(InvestigationCase c, DateTime now) => Options.ClaimIsActive(c, now)
        ? new ClaimState(c.ClaimedBy, c.ClaimedByUser, c.ClaimActivityAt, c.ClaimActivityAt!.Value.AddMinutes(Options.ClaimTimeoutMinutes))
        : new ClaimState(null, null, null, null);

    private async Task<ClaimOutcome> RefusedAsync(SurveillanceDbContext db, Guid caseId, CancellationToken ct)
    {
        var c = await db.Cases.AsNoTracking().SingleAsync(x => x.Id == caseId, ct);
        if (c.Status != CaseStatus.AwaitingApproval)
        {
            return new(ClaimResult.NotAwaitingApproval);
        }

        return new(ClaimResult.HeldByOther, StateOf(c, DateTime.UtcNow).Holder);
    }

    internal static async Task AuditAsync(SurveillanceDbContext db, Guid caseId, string actor, string action, string? detail, CancellationToken ct)
    {
        db.Audit.Add(new CaseAuditRecord { CaseId = caseId, At = DateTime.UtcNow, Actor = actor, Action = action, Detail = detail });
        await db.SaveChangesAsync(ct);
    }
}
