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


class InvestigationRequest(Wire):
    case_id: str = Field(min_length=1)
    symbol: str = Symbol
    anomaly_date: date
    trigger: AnomalyTrigger
    notes: str | None = None


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


def event(run_id: str, sequence: int, type_: str, payload: dict[str, Any]) -> dict[str, Any]:
    return {"runId": run_id, "sequence": sequence, "timestamp": utc_timestamp(), "type": type_, "payload": payload}


def tool_finished(call_id: str, name: str, ok: bool, summary: str, code: str | None = None,
                  message: str | None = None) -> dict[str, Any]:
    payload: dict[str, Any] = {"toolCallId": call_id, "toolName": name, "status": "ok" if ok else "error",
                               "summary": summary[:500]}
    if not ok:
        payload["error"] = {"code": code or "error", "message": (message or summary)[:500]}
    return payload
