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
}

public static class CaseStatusExtensions
{
    public static bool IsTerminal(this CaseStatus status) =>
        status is CaseStatus.Approved or CaseStatus.Rejected or CaseStatus.Cancelled or CaseStatus.Failed;
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
