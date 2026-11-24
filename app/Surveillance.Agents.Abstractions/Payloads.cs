using System.Text.Json;

namespace Surveillance.Agents.Abstractions;

// Payload shapes of events.schema.json. Property names serialize to camelCase
// and enum values to snake_case through AgentJson.Options.

public sealed record RunStartedPayload(string CaseId, string Symbol, int MaxToolCalls);

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

public sealed record ApprovalRequiredPayload(string ApprovalId, int Revision, Memo Memo);

public enum RunOutcome
{
    Approved,
    Rejected,
    Cancelled,
}

public sealed record DonePayload(RunOutcome Outcome, int ToolCallsUsed, Memo? Memo = null);

public enum AgentErrorCode
{
    StepLimitExceeded,
    RevisionLimitExceeded,
    ModelError,
    McpUnavailable,
    InvalidInput,
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
