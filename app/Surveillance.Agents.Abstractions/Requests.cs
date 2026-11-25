namespace Surveillance.Agents.Abstractions;

public enum TriggerKind
{
    Price,
    Volume,
    Both,
}

/// <summary>The threshold rule that flagged the stock; produced by detection code, not by an agent.</summary>
public sealed record AnomalyTrigger(
    TriggerKind Kind,
    string Rule,
    double? PriceChangePct = null,
    long? Volume = null,
    double? AvgVolume = null,
    double? VolumeRatio = null);

/// <summary>
/// Input of a run (<c>$defs/InvestigationRequest</c>). For the UMA investigator (UseCase null)
/// Symbol, AnomalyDate and Trigger describe the anomaly. For pattern detection (CHANGE-002) the
/// application still fills them, with the first candidate, the window's last day and the
/// screening rule, so adapters written for UMA keep working; the agent reads <see cref="Job"/>.
/// </summary>
public sealed record InvestigationRequest(
    string CaseId,
    string Symbol,
    DateOnly AnomalyDate,
    AnomalyTrigger Trigger,
    string? Notes = null,
    UseCase? UseCase = null,
    PatternJob? Job = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPatternDetection => UseCase == Abstractions.UseCase.PatternDetection;
}

public enum UseCase
{
    UmaInvestigation,
    PatternDetection,
}

public sealed record PatternCandidate(string Symbol, double PriceChangePct, string? Reason = null);

/// <summary>Limits the pattern-detection agent enforces in code (<c>$defs/PatternJob.limits</c>).</summary>
public sealed record PatternLimits(
    int MaxDepth,
    int MaxStocks,
    int BrokersPerStock,
    int MaxStepsPerWorker,
    int MaxToolCalls,
    int MaxMinutes,
    double MinTopBuyerSharePct,
    double MaxBrokerSpreadPct);

/// <summary>Input of pattern detection: screened candidates and limits (<c>$defs/PatternJob</c>).</summary>
public sealed record PatternJob(
    DateOnly WindowStart,
    DateOnly WindowEnd,
    IReadOnlyList<PatternCandidate> Candidates,
    PatternLimits Limits);

public enum Decision
{
    Approve,
    Revise,
    Reject,
}

/// <summary>The analyst's answer to an <c>approval_required</c> event (<c>$defs/ApprovalDecision</c>).</summary>
public sealed record ApprovalDecision(
    string ApprovalId,
    Decision Decision,
    string DecidedBy,
    string? Comment = null);
