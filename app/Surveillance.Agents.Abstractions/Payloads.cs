using System.Text.Json;

namespace Surveillance.Agents.Abstractions;

// Payload shapes of events.schema.json. Property names serialize to camelCase
// and enum values to snake_case through AgentJson.Options.

/// <param name="Symbol">Null for pattern detection, which covers many stocks.</param>
public sealed record RunStartedPayload(string CaseId, string? Symbol, int MaxToolCalls, UseCase? UseCase = null);

public enum InvestigationStep
{
    Measure,
    Explain,
    DeepDive,
    Assess,
    Memo,
}

public sealed record StepStartedPayload(InvestigationStep Step, string Label);

public sealed record TokenPayload(string Text);

public sealed record ToolCallStartedPayload(string ToolCallId, string ToolName, JsonElement Arguments);

public enum ToolCallStatus
{
    Ok,
    Error,
}

public sealed record ToolError(string Code, string Message);

/// <summary>Where an MCP tool's data came from (<c>$defs/ToolSource</c>).</summary>
public sealed record ToolSource(string Server, string? Provider = null, DateTime? FetchedAt = null, bool? Stale = null);

/// <param name="Source">Set for MCP tools that returned a result; see <see cref="ToolEvidence"/>.</param>
/// <param name="ResultPreview">Raw tool output, at most 8192 UTF-8 bytes. The application stores it apart from the event.</param>
public sealed record ToolCallFinishedPayload(
    string ToolCallId,
    string ToolName,
    ToolCallStatus Status,
    string Summary,
    ToolError? Error = null,
    ToolSource? Source = null,
    string? ResultPreview = null);

/// <param name="Memo">The UMA memo; null when the draft is a pattern-detection report.</param>
public sealed record ApprovalRequiredPayload(string ApprovalId, int Revision, Memo? Memo, PatternReport? Report = null);

public enum RunOutcome
{
    Approved,
    Rejected,
    Cancelled,
}

public sealed record DonePayload(RunOutcome Outcome, int ToolCallsUsed, Memo? Memo = null, PatternReport? Report = null);

public enum AgentErrorCode
{
    StepLimitExceeded,
    RevisionLimitExceeded,
    ModelError,
    McpUnavailable,
    InvalidInput,

    /// <summary>The run was lost because the application or agent service restarted mid-run (CHANGE-003).</summary>
    RunInterrupted,
    Internal,
}

public sealed record ErrorPayload(AgentErrorCode Code, string Message, bool Retryable);

public enum EvidenceLevel
{
    Strong,
    Moderate,
    Weak,
    Insufficient,
}

public enum Recommendation
{
    Escalate,
    Monitor,
    Close,
}

public enum FindingCategory
{
    PriceVolume,
    News,
    CorporateAction,
    Broker,
    Bandar,
    Foreign,
    Insider,
}

public sealed record AnomalyFacts(
    string Description,
    double? PriceChangePct = null,
    long? Volume = null,
    double? AvgVolume = null,
    double? VolumeRatio = null);

public sealed record Evidence(string ToolCallId, string ToolName, string Detail);

public sealed record Finding(FindingCategory Category, string Statement, IReadOnlyList<Evidence> Evidence);

public sealed record UnavailableData(string ToolName, string Reason);

public sealed record Memo(
    string Symbol,
    DateOnly AnomalyDate,
    string Summary,
    AnomalyFacts Anomaly,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<UnavailableData> UnavailableData,
    EvidenceLevel EvidenceLevel,
    string EvidenceRationale,
    Recommendation Recommendation,
    string RecommendationRationale,
    string Disclaimer = Memo.StandardDisclaimer)
{
    public const string StandardDisclaimer = "Indikasi untuk ditelaah analis, bukan kesimpulan pelanggaran.";
}

// ---- CHANGE-002: multi-agent pattern detection ----

public enum WorkerRole
{
    Stock,
    Broker,
    Ownership,
}

public enum LeadKind
{
    Stock,
    Broker,
}

public sealed record Lead(LeadKind Kind, string Target, string Reason);

public sealed record WorkerStartedPayload(string WorkerId, WorkerRole Role, string Target, int Depth, string? Reason = null);

public enum WorkerStatus
{
    Ok,
    Error,
    Cancelled,
    LimitReached,
}

public sealed record WorkerFinishedPayload(
    string WorkerId,
    WorkerRole Role,
    WorkerStatus Status,
    string Summary,
    IReadOnlyList<Lead>? Leads = null);

public sealed record Statement(string Text, IReadOnlyList<Evidence> Evidence)
{
    // The schema calls the text "statement"; the record keeps a short C# name.
    [System.Text.Json.Serialization.JsonPropertyName("statement")]
    public string Text { get; init; } = Text;
}

public sealed record ReportCounts(int Clusters, int Stocks, int Brokers);

public sealed record PatternCluster(
    string Name,
    IReadOnlyList<string> Stocks,
    IReadOnlyList<string> DominantBrokers,
    string Chronology,
    IReadOnlyList<Statement> Findings,
    string? OwnershipLinks = null);

/// <summary>The pattern-detection report submitted for the analyst (<c>$defs/PatternReport</c>).</summary>
public sealed record PatternReport(
    string Summary,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    ReportCounts Counts,
    IReadOnlyList<PatternCluster> Clusters,
    IReadOnlyList<Statement> AlternativeExplanations,
    IReadOnlyList<Lead> LeadsNotFollowed,
    IReadOnlyList<string> LimitsReached,
    IReadOnlyList<string> DataLimitations,
    IReadOnlyList<string> FollowUps,
    string Disclaimer = Memo.StandardDisclaimer);
