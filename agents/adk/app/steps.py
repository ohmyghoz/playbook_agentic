"""The five workflow stages of asd-investigator-uma.md and when step_started is emitted.

Mirrors app/Surveillance.Agents.Abstractions/InvestigationSteps.cs; agents/langgraph and agents/adk keep
identical copies. The adapter decides the stage from the tool name, so it does not depend on the model.
"""

from typing import Any

ORDER = ["measure", "explain", "deep_dive", "assess", "memo"]

LABELS = {
    "measure": "Mengukur penyimpangan",
    "explain": "Mencari penjelasan wajar",
    "deep_dive": "Menggali broker dan arus asing",
    "assess": "Menilai bukti",
    "memo": "Draf memo menunggu keputusan analis",
}

_TOOL_STEPS = {
    "idx_stockbit_status": "measure", "idx_quote": "measure", "idx_history": "measure",
    "idx_news": "explain", "idx_stockbit_actions": "explain", "idx_corporate_actions": "explain",
    "idx_broker": "deep_dive", "idx_bandar": "deep_dive", "idx_foreign": "deep_dive", "idx_stockbit_movements": "deep_dive",
    "submit_memo": "assess",
}


def next_step(current: str | None, type_: str, payload: dict[str, Any]) -> str | None:
    """The stage to announce for an event, or None when the stage does not change. Stages only move forward.

    run_started announces measure after itself; the others are announced before the event.
    """
    if type_ == "run_started":
        wanted = "measure"
    elif type_ == "tool_call_started":
        wanted = _TOOL_STEPS.get(payload.get("toolName", ""))
    elif type_ == "approval_required":
        wanted = "memo"
    else:
        wanted = None
    if wanted is None or (current is not None and ORDER.index(wanted) <= ORDER.index(current)):
        return None
    return wanted


def step_payload(step: str) -> dict[str, Any]:
    return {"step": step, "label": LABELS[step]}
