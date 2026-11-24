"""The UMA investigator as a Google ADK LlmAgent.

Run bookkeeping lives in session state (DatabaseSessionService), so a paused
approval survives a service restart. Keys:
    request, tool_calls_used, agent_turns, limit_warned_turn, revision,
    invalid_memos, pending (memo awaiting the analyst), memo, outcome, error
"""

import json
from collections.abc import Sequence
from typing import Any

from google.adk.agents import LlmAgent
from google.adk.agents.callback_context import CallbackContext
from google.adk.agents.readonly_context import ReadonlyContext
from google.adk.tools import BaseTool, FunctionTool
from google.adk.tools.tool_context import ToolContext
from pydantic import ValidationError

from . import instructions
from .config import Settings
from .contract import DISCLAIMER, InvestigationRequest, Memo
from .instructions import SUBMIT_MEMO
from .summarizer import LIMIT_MARKER

SUBMIT_DESCRIPTION = ("Kirim draf memo investigasi untuk ditelaah analis. "
                      "Analis bisa menyetujui, meminta revisi, atau menolak.")


def _error(code: str, message: str, retryable: bool) -> dict[str, Any]:
    return {"code": code, "message": message, "retryable": retryable}


def validate_memo(raw: Any, request: InvestigationRequest, known_call_ids: set[str]) -> tuple[dict[str, Any] | None, str | None]:
    """Returns the memo as wire JSON, or a reason the model can act on. Mirrors agents/maf/MemoValidator.cs."""
    if raw is None:
        return None, "Argumen 'memo' tidak ada."
    try:
        memo = Memo.model_validate(raw)
    except ValidationError as ex:
        details = "; ".join(f"{'.'.join(str(p) for p in e['loc'])}: {e['msg']}" for e in ex.errors()[:8])
        return None, f"Memo belum sesuai skema: {details}."

    problems = []
    if memo.symbol != request.symbol:
        problems.append(f"symbol harus {request.symbol}")
    if memo.anomaly_date != request.anomaly_date:
        problems.append(f"anomalyDate harus {request.anomaly_date.isoformat()}")
    for finding in memo.findings:
        for ev in finding.evidence:
            if ev.tool_call_id not in known_call_ids:
                problems.append(f"toolCallId {ev.tool_call_id} tidak ada di run ini")
    if problems:
        return None, "Memo belum sesuai skema: " + "; ".join(problems) + "."

    memo.disclaimer = DISCLAIMER
    return memo.dump(), None


def _declaration_only(memo: Memo) -> dict:
    """Kirim draf memo investigasi untuk ditelaah analis. Analis bisa menyetujui, meminta revisi, atau menolak."""
    raise NotImplementedError


class SubmitMemoTool(FunctionTool):
    """Declares the full Memo schema to the model but receives the raw dict, so an
    invalid memo reaches our validation instead of failing inside ADK's conversion."""

    def __init__(self, settings: Settings):
        super().__init__(self._submit)
        self.name = SUBMIT_MEMO
        self.description = SUBMIT_DESCRIPTION
        self._settings = settings

    def _get_declaration(self):
        return FunctionTool(_declaration_only)._get_declaration().model_copy(update={"name": SUBMIT_MEMO})

    def _submit(self, memo: dict, tool_context: ToolContext) -> dict:
        state = tool_context.state
        settings = self._settings
        confirmation = tool_context.tool_confirmation

        if confirmation is None:
            request = InvestigationRequest.model_validate(state["request"])
            memo_json, problem = validate_memo(memo, request, set(state.get("mcp_call_ids", [])))
            if memo_json is None:
                attempts = state.get("invalid_memos", 0) + 1
                state["invalid_memos"] = attempts
                if attempts > settings.max_invalid_memos:
                    state["error"] = _error("model_error", "Agent tidak berhasil menyusun memo yang sesuai format. Coba lagi.", True)
                    tool_context.actions.skip_summarization = True
                    return {"error": problem, "code": "invalid_memo", "summary": "Memo tidak sesuai format."}
                return {"error": problem, "code": "invalid_memo", "summary": "Memo belum sesuai format; agent memperbaikinya."}

            revision = state.get("revision", -1) + 1
            state["pending"] = {"approvalId": f"approval_{revision + 1}", "revision": revision, "memo": memo_json,
                                "toolCallId": tool_context.function_call_id}
            tool_context.request_confirmation(hint="Keputusan analis untuk draf memo.", payload=state["pending"])
            tool_context.actions.skip_summarization = True
            return {"status": "waiting"}

        # Re-entered with the analyst's decision (possibly in a new process).
        pending = state.get("pending") or {}
        decision = confirmation.payload or {}
        revision = pending.get("revision", 0)
        state["revision"] = revision
        state["memo"] = pending.get("memo")
        state["pending"] = None

        match decision.get("decision"):
            case "approve":
                state["outcome"] = "approved"
                tool_context.actions.skip_summarization = True
                return {"result": "Memo disetujui analis.", "summary": "Memo disetujui analis."}
            case "reject":
                state["outcome"] = "rejected"
                tool_context.actions.skip_summarization = True
                return {"result": "Memo ditolak analis.", "summary": "Memo ditolak analis."}
            case _ if revision >= settings.max_revisions:
                state["error"] = _error("revision_limit_exceeded",
                                        f"Memo sudah direvisi {settings.max_revisions} kali. Setujui atau tolak draf terakhir.", False)
                tool_context.actions.skip_summarization = True
                return {"result": "Batas revisi tercapai.", "summary": "Revisi diminta, tetapi batas revisi sudah tercapai."}
            case _:
                return {"result": instructions.REVISE_PREFIX + (decision.get("comment") or ""),
                        "summary": "Analis meminta revisi."}


def build_agent(model: Any, mcp_tools: Sequence[BaseTool] | Any, settings: Settings) -> LlmAgent:
    def instruction(ctx: ReadonlyContext) -> str:
        return instructions.build(InvestigationRequest.model_validate(ctx.state["request"]), settings.max_tool_calls)

    def before_model(callback_context: CallbackContext, llm_request) -> None:
        state = callback_context.state
        state["agent_turns"] = state.get("agent_turns", 0) + 1

    def before_tool(tool: BaseTool, args: dict[str, Any], tool_context: ToolContext) -> dict | None:
        if tool.name == SUBMIT_MEMO:
            return None
        state = tool_context.state
        state["mcp_call_ids"] = [*state.get("mcp_call_ids", []), tool_context.function_call_id]
        used = state.get("tool_calls_used", 0)
        if used < settings.max_tool_calls:
            state["tool_calls_used"] = used + 1
            return None

        # Calls in the turn that got the warning were requested before the model could read it.
        turn, warned = state.get("agent_turns", 0), state.get("limit_warned_turn")
        if warned is not None and warned != turn:
            state["error"] = _error("step_limit_exceeded",
                                    f"Agent mencapai batas {settings.max_tool_calls} panggilan data tanpa menyusun memo.", False)
            tool_context.actions.skip_summarization = True
        elif warned is None:
            state["limit_warned_turn"] = turn
        return {"result": f"{LIMIT_MARKER} Batas {settings.max_tool_calls} panggilan tool tercapai. Susun memo dari data yang sudah ada."}

    def after_tool(tool: BaseTool, args: dict[str, Any], tool_context: ToolContext, tool_response: Any) -> dict | None:
        if tool.name == SUBMIT_MEMO:
            return None
        text = tool_response.get("result") if isinstance(tool_response, dict) and isinstance(tool_response.get("result"), str) \
            else json.dumps(tool_response, ensure_ascii=False, default=str)
        if text.startswith(LIMIT_MARKER):
            return None
        return {"result": f"[toolCallId: {tool_context.function_call_id}]\n{text}"}

    tools = mcp_tools if isinstance(mcp_tools, list) else [mcp_tools]
    return LlmAgent(
        name="investigator_uma",
        model=model,
        instruction=instruction,
        tools=[*tools, SubmitMemoTool(settings)],
        before_model_callback=before_model,
        before_tool_callback=before_tool,
        after_tool_callback=after_tool,
    )
