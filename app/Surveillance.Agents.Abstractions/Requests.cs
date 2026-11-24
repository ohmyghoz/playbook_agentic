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

/// <summary>Input of the UMA investigator (<c>$defs/InvestigationRequest</c>).</summary>
public sealed record InvestigationRequest(
    string CaseId,
    string Symbol,
    DateOnly AnomalyDate,
    AnomalyTrigger Trigger,
    string? Notes = null);

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
