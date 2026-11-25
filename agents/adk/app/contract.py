"""Wire types of contracts/events.schema.json and helpers to build catalog events."""

from datetime import date, datetime, timezone
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

DISCLAIMER = "Indikasi untuk ditelaah analis, bukan kesimpulan pelanggaran."
TERMINAL_TYPES = {"done", "error"}
PAUSE_TYPES = {"approval_required"}


class Wire(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="forbid")

    def dump(self) -> dict[str, Any]:
        return self.model_dump(by_alias=True, exclude_none=True, mode="json")


Symbol = Field(pattern=r"^[A-Z]{4}$")


class AnomalyTrigger(Wire):
    kind: Literal["price", "volume", "both"]
    rule: str = Field(min_length=1)
    price_change_pct: float | None = None
    volume: int | None = Field(default=None, ge=0)
    avg_volume: float | None = Field(default=None, ge=0)
    volume_ratio: float | None = Field(default=None, ge=0)


class PatternCandidate(Wire):
    symbol: str = Symbol
    price_change_pct: float
    reason: str | None = None


class PatternLimits(Wire):
    max_depth: int = Field(ge=0)
    max_stocks: int = Field(ge=1)
    brokers_per_stock: int = Field(ge=1)
    max_steps_per_worker: int = Field(ge=1)
    max_tool_calls: int = Field(ge=1)
    max_minutes: int = Field(ge=1)
    min_top_buyer_share_pct: float
    max_broker_spread_pct: float


class PatternJob(Wire):
    """Input of pattern detection (`$defs/PatternJob`, CHANGE-002)."""

    window_start: date
    window_end: date
    candidates: list[PatternCandidate] = Field(min_length=1)
    limits: PatternLimits


class InvestigationRequest(Wire):
    case_id: str = Field(min_length=1)
    symbol: str = Symbol
    anomaly_date: date
    trigger: AnomalyTrigger
    notes: str | None = None
    use_case: Literal["uma_investigation", "pattern_detection"] | None = None
    job: PatternJob | None = None

    @property
    def is_pattern_detection(self) -> bool:
        return self.use_case == "pattern_detection"


class ApprovalDecision(Wire):
    approval_id: str = Field(min_length=1)
    decision: Literal["approve", "revise", "reject"]
    decided_by: str = Field(min_length=1)
    comment: str | None = None


class AnomalyFacts(Wire):
    description: str = Field(min_length=1)
    price_change_pct: float | None = None
    volume: int | None = Field(default=None, ge=0)
    avg_volume: float | None = Field(default=None, ge=0)
    volume_ratio: float | None = Field(default=None, ge=0)


class Evidence(Wire):
    tool_call_id: str = Field(min_length=1)
    tool_name: str = Field(min_length=1)
    detail: str = Field(min_length=1)


class Finding(Wire):
    category: Literal["price_volume", "news", "corporate_action", "broker", "bandar", "foreign", "insider"]
    statement: str = Field(min_length=1)
    evidence: list[Evidence] = Field(min_length=1)


class UnavailableData(Wire):
    tool_name: str = Field(min_length=1)
    reason: str = Field(min_length=1)


class Memo(Wire):
    """The memo submitted for the analyst (`$defs/Memo`)."""

    # The model may omit or paraphrase the fixed disclaimer; the service sets it.
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")

    symbol: str = Symbol
    anomaly_date: date
    summary: str = Field(min_length=1)
    anomaly: AnomalyFacts
    findings: list[Finding]
    unavailable_data: list[UnavailableData]
    evidence_level: Literal["strong", "moderate", "weak", "insufficient"]
    evidence_rationale: str = Field(min_length=1)
    recommendation: Literal["escalate", "monitor", "close"]
    recommendation_rationale: str = Field(min_length=1)
    disclaimer: str = DISCLAIMER


def utc_timestamp() -> str:
    """RFC 3339 in UTC with a trailing Z, as the schema's pattern requires."""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f") + "Z"


def event(run_id: str, sequence: int, type_: str, payload: dict[str, Any], agent_name: str | None = None) -> dict[str, Any]:
    e = {"runId": run_id, "sequence": sequence, "timestamp": utc_timestamp(), "type": type_, "payload": payload}
    if agent_name:
        e["agentName"] = agent_name  # multi-agent runs only (CHANGE-002)
    return e


def tool_finished(call_id: str, name: str, ok: bool, summary: str, code: str | None = None,
                  message: str | None = None) -> dict[str, Any]:
    payload: dict[str, Any] = {"toolCallId": call_id, "toolName": name, "status": "ok" if ok else "error",
                               "summary": summary[:500]}
    if not ok:
        payload["error"] = {"code": code or "error", "message": (message or summary)[:500]}
    return payload


# Evidence fields of tool_call_finished (CHANGE-001). Mirrors Surveillance.Agents.Abstractions/ToolEvidence.cs.
SOURCE_SERVER = "idx"
PREVIEW_MAX_BYTES = 8192
TRUNCATION_MARKER = "\n…[dipotong]"


def result_preview(raw: str) -> str:
    """Cuts raw tool output at a character boundary so it fits in 8192 UTF-8 bytes, marker included."""
    if len(raw.encode("utf-8")) <= PREVIEW_MAX_BYTES:
        return raw
    budget = PREVIEW_MAX_BYTES - len(TRUNCATION_MARKER.encode("utf-8"))
    cut = raw.encode("utf-8")[:budget].decode("utf-8", errors="ignore")
    return cut + TRUNCATION_MARKER


def tool_source(envelope: dict[str, Any] | None) -> dict[str, Any]:
    """provider, fetched_at and stale from an idx envelope; any of them may be missing."""
    source: dict[str, Any] = {"server": SOURCE_SERVER}
    if not isinstance(envelope, dict):
        return source
    if isinstance(envelope.get("provider"), str):
        source["provider"] = envelope["provider"]
    if isinstance(envelope.get("fetched_at"), str):
        try:
            at = datetime.fromisoformat(envelope["fetched_at"].replace("Z", "+00:00"))
            if at.tzinfo is None:
                at = at.replace(tzinfo=timezone.utc)
            source["fetchedAt"] = at.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S") + "Z"
        except ValueError:
            pass
    if isinstance(envelope.get("stale"), bool):
        source["stale"] = envelope["stale"]
    return source


# ---- CHANGE-002: pattern detection ----

class Lead(Wire):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")

    kind: Literal["stock", "broker"]
    target: str = Field(min_length=1)
    reason: str = Field(min_length=1)


class Statement(Wire):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")

    statement: str = Field(min_length=1)
    evidence: list[Evidence] = Field(default_factory=list)


class BrokerShare(Wire):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")

    broker: str = Field(min_length=1)
    net_buy_share_pct: float


class WorkerReport(Wire):
    """The arguments of finish_worker (asd-pattern-detection.md, Protokol worker)."""

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")

    summary: str = Field(min_length=1)
    dominant_brokers: list[BrokerShare] = Field(default_factory=list)
    leads: list[Lead] = Field(default_factory=list)
    findings: list[Statement] = Field(default_factory=list)


class ReportCounts(Wire):
    clusters: int = Field(ge=0)
    stocks: int = Field(ge=0)
    brokers: int = Field(ge=0)


class PatternCluster(Wire):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")

    name: str = Field(min_length=1)
    stocks: list[str] = Field(min_length=1)
    dominant_brokers: list[str] = Field(default_factory=list)
    chronology: str = Field(min_length=1)
    ownership_links: str | None = None
    findings: list[Statement] = Field(min_length=1)


class PatternReport(Wire):
    """The report submitted for the analyst (`$defs/PatternReport`). Lists the model leaves out default to empty."""

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")

    summary: str = Field(min_length=1)
    period_start: date | None = None
    period_end: date | None = None
    counts: ReportCounts | None = None
    clusters: list[PatternCluster] = Field(default_factory=list)
    alternative_explanations: list[Statement] = Field(default_factory=list)
    leads_not_followed: list[Lead] = Field(default_factory=list)
    limits_reached: list[str] = Field(default_factory=list)
    data_limitations: list[str] = Field(default_factory=list)
    follow_ups: list[str] = Field(default_factory=list)
    disclaimer: str = DISCLAIMER
