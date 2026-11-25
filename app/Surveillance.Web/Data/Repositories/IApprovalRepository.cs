namespace Surveillance.Web.Data.Repositories;

/// <summary>
/// Analyst decisions, claims, expiry and the audit trail. Every change to who decides or holds a
/// case is a conditional update on the case row, so concurrent requests cannot both win.
/// </summary>
public interface IApprovalRepository
{
    /// <summary>
    /// Moves the case AwaitingApproval → Running and stores the decision, in one transaction, if
    /// the case still waits and is not held by someone else (claim active since <paramref name="claimCutoff"/>).
    /// </summary>
    Task<bool> TryDecideAsync(ApprovalDecisionRecord decision, CaseStatus running, DateTime claimCutoff, CancellationToken ct);

    /// <summary>Who decided the given approval, or null.</summary>
    Task<string?> WinnerOfAsync(Guid caseId, string approvalId, CancellationToken ct);

    /// <summary>Takes the case for <paramref name="displayName"/> if it is free, theirs, or its claim lapsed.</summary>
    Task<bool> TryClaimAsync(Guid caseId, string user, string displayName, DateTime claimCutoff, CancellationToken ct);

    Task<bool> TryReleaseAsync(Guid caseId, string displayName, CancellationToken ct);

    /// <summary>Hands a waiting case to another analyst, whoever holds it.</summary>
    Task<bool> TryAssignAsync(Guid caseId, string user, string displayName, CancellationToken ct);

    /// <summary>
    /// Marks the case Expired if it still waits on the same draft (<paramref name="awaitingSince"/>),
    /// and records the expiry as a decision by the system plus an audit entry.
    /// </summary>
    Task<bool> TryExpireAsync(Guid caseId, DateTime awaitingSince, CaseStatus expired, string actor, string reason, CancellationToken ct);

    Task AddAuditAsync(CaseAuditRecord record, CancellationToken ct);

    Task<List<ApprovalDecisionRecord>> ListDecisionsAsync(Guid caseId, CancellationToken ct);

    Task<List<CaseAuditRecord>> ListAuditAsync(Guid caseId, CancellationToken ct);
}

/// <summary>Application settings changed at run time, such as the active agent solution.</summary>
public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct);

    Task SetAsync(string key, string value, CancellationToken ct);
}
