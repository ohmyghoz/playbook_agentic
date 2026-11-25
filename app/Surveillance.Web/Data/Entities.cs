namespace Surveillance.Web.Data;

public enum CaseStatus
{
    Starting,
    Running,
    AwaitingApproval,
    Approved,
    Rejected,
    Cancelled,
    Failed,

    /// <summary>No analyst decided before the approval deadline; the application stopped the run.</summary>
    Expired,
}

public static class CaseStatusExtensions
{
    public static bool IsTerminal(this CaseStatus status) =>
        status is CaseStatus.Approved or CaseStatus.Rejected or CaseStatus.Cancelled or CaseStatus.Failed or CaseStatus.Expired;
}

/// <summary>One investigation of one stock on one trading day. The application's source of truth.</summary>
public class InvestigationCase
{
    public Guid Id { get; set; }
    public required string Symbol { get; set; }
    public DateOnly AnomalyDate { get; set; }

    /// <summary>The <c>AnomalyTrigger</c> as JSON, kept verbatim for audit.</summary>
    public required string TriggerJson { get; set; }

    public string? Notes { get; set; }
    public required string Provider { get; set; }
    public string? RunId { get; set; }
    public CaseStatus Status { get; set; }
    public long LastSequence { get; set; }

    /// <summary>When the current draft started waiting for a decision; null while not awaiting approval.</summary>
    public DateTime? AwaitingSince { get; set; }

    /// <summary>Approval deadline for this case in minutes; null uses Approval:ExpiryHours. Lab demos set 2.</summary>
    public int? ApprovalTimeoutMinutes { get; set; }

    /// <summary>The analyst holding the case (display name, as in DecidedBy) and the account behind it.</summary>
    public string? ClaimedBy { get; set; }
    public string? ClaimedByUser { get; set; }

    /// <summary>Last activity of the holder; the claim lapses after Approval:ClaimTimeoutMinutes without activity.</summary>
    public DateTime? ClaimActivityAt { get; set; }

    /// <summary>Set by RetentionService once consecutive token events were merged (docs/RETENTION.md).</summary>
    public DateTime? TokensCompactedAt { get; set; }

    /// <summary>Set by RetentionService once the events and previews were deleted after the retention period.</summary>
    public DateTime? EventsPurgedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<AgentEventRecord> Events { get; set; } = [];
    public List<ApprovalDecisionRecord> Decisions { get; set; } = [];
}

/// <summary>An agent event exactly as received, unique per (RunId, Sequence).</summary>
public class AgentEventRecord
{
    public long Id { get; set; }
    public Guid CaseId { get; set; }
    public required string RunId { get; set; }
    public long Sequence { get; set; }
    public required string Type { get; set; }
    public DateTime Timestamp { get; set; }
    public required string PayloadJson { get; set; }
    public DateTime ReceivedAt { get; set; }
}

/// <summary>
/// The raw output of one tool call (<c>resultPreview</c>, at most 8 KB), kept apart from the event
/// so SSE and replays stay small. Read only when an analyst opens the evidence.
/// </summary>
public class ToolResultPreview
{
    public long Id { get; set; }
    public Guid CaseId { get; set; }
    public required string RunId { get; set; }
    public required string ToolCallId { get; set; }
    public required string ToolName { get; set; }
    public required string Preview { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Something a person or the application did to a case outside the agent's events: claim,
/// release, assign, decide, expire. Together with the events it forms the audit timeline.
/// </summary>
public class CaseAuditRecord
{
    public long Id { get; set; }
    public Guid CaseId { get; set; }
    public DateTime At { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public string? Detail { get; set; }
}

/// <summary>An application setting changed from the settings page, such as the active agent solution.</summary>
public class AppSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>An analyst decision on an approval_required event.</summary>
public class ApprovalDecisionRecord
{
    public long Id { get; set; }
    public Guid CaseId { get; set; }
    public required string ApprovalId { get; set; }
    public required string Decision { get; set; }
    public string? Comment { get; set; }
    public required string DecidedBy { get; set; }
    public DateTime DecidedAt { get; set; }
}
