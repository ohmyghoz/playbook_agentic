"""The UMA investigator as an explicit LangGraph StateGraph.

    START -> agent -> tools  -> agent | review
                   -> review -> agent | END   (interrupt for the analyst)
                   -> nudge  -> agent    (a text-only turn is narration; ask it to go on)
                   -> no_memo -> END     (too many text-only turns in a row)

Tool progress is written to the "custom" stream; the service turns it into
catalog events (see EVENT-MAPPING.md).
"""

import asyncio
from collections.abc import Sequence
from typing import Annotated, Any, TypedDict

from langchain_core.language_models import BaseChatModel
from langchain_core.messages import AIMessage, AnyMessage, HumanMessage, SystemMessage, ToolMessage
from langchain_core.tools import BaseTool, tool
from langgraph.config import get_stream_writer
from langgraph.graph import END, START, StateGraph
from langgraph.graph.message import add_messages
from langgraph.types import interrupt
from pydantic import BaseModel, Field, ValidationError

from . import instructions
from .config import Settings
from .contract import DISCLAIMER, InvestigationRequest, Memo, tool_finished
from .instructions import SUBMIT_MEMO
from .summarizer import LIMIT_MARKER, result_text, summarize


class State(TypedDict, total=False):
    request: dict[str, Any]
    messages: Annotated[list[AnyMessage], add_messages]
    tool_calls_used: int
    agent_turns: int
    limit_warned_turn: int | None
    revision: int
    invalid_memos: int
    text_only_turns: int
    memo: dict[str, Any] | None
    outcome: str | None
    error: dict[str, Any] | None


class SubmitMemoArgs(BaseModel):
    memo: Memo = Field(description="Draf memo investigasi sesuai skema.")


@tool(SUBMIT_MEMO, args_schema=SubmitMemoArgs)
def submit_memo_tool(memo: Memo) -> str:
    """Kirim draf memo investigasi untuk ditelaah analis. Analis bisa menyetujui, meminta revisi, atau menolak."""
    return "Memo disetujui analis."  # never executed: the review node handles this call


def _error(code: str, message: str, retryable: bool) -> dict[str, Any]:
    return {"code": code, "message": message, "retryable": retryable}


def _last_ai(messages: Sequence[AnyMessage]) -> AIMessage:
    return next(m for m in reversed(messages) if isinstance(m, AIMessage))


def validate_memo(args: dict[str, Any], request: InvestigationRequest,
                  known_call_ids: set[str]) -> tuple[dict[str, Any] | None, str | None]:
    """Returns the memo as wire JSON, or a reason the model can act on. Mirrors agents/maf/MemoValidator.cs."""
    raw = args.get("memo")
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


def build_graph(model: BaseChatModel, tools: Sequence[BaseTool], settings: Settings) -> StateGraph:
    tools_by_name = {t.name: t for t in tools}
    bound = model.bind_tools([*tools, submit_memo_tool])

    async def agent(state: State) -> dict[str, Any]:
        request = InvestigationRequest.model_validate(state["request"])
        system = SystemMessage(instructions.build(request, settings.max_tool_calls))
        reply = await bound.ainvoke([system, *state["messages"]])
        # Some models narrate in one turn and call the tool in the next; count consecutive text-only turns.
        text_only = 0 if reply.tool_calls else state.get("text_only_turns", 0) + 1
        return {"messages": [reply], "agent_turns": state.get("agent_turns", 0) + 1, "text_only_turns": text_only}

    def after_agent(state: State) -> str:
        calls = _last_ai(state["messages"]).tool_calls
        if not calls:
            return "no_memo" if state.get("text_only_turns", 0) >= settings.max_text_only_turns else "nudge"
        if any(c["name"] != SUBMIT_MEMO for c in calls):
            return "tools"
        return "review"

    async def run_tool(call: dict[str, Any], kind: str) -> tuple[str, dict[str, Any]]:
        call_id, name = call["id"], call["name"]
        if kind == "limit":
            text = f"{LIMIT_MARKER} Batas {settings.max_tool_calls} panggilan tool tercapai. Susun memo dari data yang sudah ada."
            return text, summarize(call_id, name, text)
        if kind == "unknown":
            text = f"Tool {name} tidak tersedia untuk agent ini."
            return text, tool_finished(call_id, name, False, text, "unknown_tool")
        try:
            result = await tools_by_name[name].ainvoke({**call, "type": "tool_call"})
            text = result_text(result.content)
            is_error = getattr(result, "status", "success") == "error"
        except Exception as ex:  # transport failures raise; the run continues without this data
            text, is_error = f"Tool gagal: {ex}", True
        return f"[toolCallId: {call_id}]\n{text}", summarize(call_id, name, text, is_error)

    async def tools_node(state: State) -> dict[str, Any]:
        writer = get_stream_writer()
        used = state.get("tool_calls_used", 0)
        warned = state.get("limit_warned_turn")
        turn = state.get("agent_turns", 0)
        exceeded = False

        plan = []
        for call in _last_ai(state["messages"]).tool_calls:
            if call["name"] == SUBMIT_MEMO:
                continue
            writer({"kind": "tool_started", "id": call["id"], "name": call["name"], "args": call["args"]})
            if call["name"] not in tools_by_name:
                plan.append((call, "unknown"))
            elif used >= settings.max_tool_calls:
                # Calls in the turn that got the warning were requested before the model could read it.
                if warned is not None and warned != turn:
                    exceeded = True
                warned = turn if warned is None else warned
                plan.append((call, "limit"))
            else:
                used += 1
                plan.append((call, "run"))

        results = await asyncio.gather(*(run_tool(call, kind) for call, kind in plan))
        messages = []
        for (call, _), (text, payload) in zip(plan, results):
            writer({"kind": "tool_finished", "payload": payload})
            messages.append(ToolMessage(content=text, tool_call_id=call["id"], name=call["name"],
                                        status="success" if payload["status"] == "ok" else "error"))

        update: dict[str, Any] = {"messages": messages, "tool_calls_used": used, "limit_warned_turn": warned}
        if exceeded:
            update["error"] = _error("step_limit_exceeded",
                                     f"Agent mencapai batas {settings.max_tool_calls} panggilan data tanpa menyusun memo.", False)
        return update

    def after_tools(state: State) -> str:
        if state.get("error"):
            return END
        calls = _last_ai(state["messages"]).tool_calls
        return "review" if any(c["name"] == SUBMIT_MEMO for c in calls) else "agent"

    async def review(state: State) -> dict[str, Any]:
        # Re-runs from the top when resumed: everything before interrupt() must be repeatable.
        writer = get_stream_writer()
        request = InvestigationRequest.model_validate(state["request"])
        submits = [c for c in _last_ai(state["messages"]).tool_calls if c["name"] == SUBMIT_MEMO]
        call = submits[0]
        extra = [ToolMessage("Hanya satu memo yang diproses per giliran.", tool_call_id=c["id"]) for c in submits[1:]]
        writer({"kind": "tool_started", "id": call["id"], "name": SUBMIT_MEMO, "args": {"memo": "(lihat approval_required)"}})

        known = {c["id"] for m in state["messages"] if isinstance(m, AIMessage)
                 for c in m.tool_calls if c["name"] != SUBMIT_MEMO}
        memo, problem = validate_memo(call["args"], request, known)
        if memo is None:
            attempts = state.get("invalid_memos", 0) + 1
            update: dict[str, Any] = {
                "messages": [ToolMessage(problem, tool_call_id=call["id"], status="error"), *extra],
                "invalid_memos": attempts,
            }
            if attempts > settings.max_invalid_memos:
                writer({"kind": "tool_finished", "payload": tool_finished(
                    call["id"], SUBMIT_MEMO, False, "Memo tidak sesuai format.", "invalid_memo", problem)})
                update["error"] = _error("model_error", "Agent tidak berhasil menyusun memo yang sesuai format. Coba lagi.", True)
            else:
                writer({"kind": "tool_finished", "payload": tool_finished(
                    call["id"], SUBMIT_MEMO, False, "Memo belum sesuai format; agent memperbaikinya.", "invalid_memo", problem)})
            return update

        revision = state.get("revision", -1) + 1
        decision = interrupt({"approvalId": f"approval_{revision + 1}", "revision": revision, "memo": memo,
                              "toolCallId": call["id"]})

        def finish(summary: str, reply: str, **changes: Any) -> dict[str, Any]:
            writer({"kind": "tool_finished", "payload": tool_finished(call["id"], SUBMIT_MEMO, True, summary)})
            return {"messages": [ToolMessage(reply, tool_call_id=call["id"]), *extra],
                    "revision": revision, "memo": memo, **changes}

        match decision["decision"]:
            case "approve":
                return finish("Memo disetujui analis.", "Memo disetujui analis.", outcome="approved")
            case "reject":
                return finish("Memo ditolak analis.", "Memo ditolak analis.", outcome="rejected")
            case _ if revision >= settings.max_revisions:
                return finish("Revisi diminta, tetapi batas revisi sudah tercapai.", "Batas revisi tercapai.",
                              error=_error("revision_limit_exceeded",
                                           f"Memo sudah direvisi {settings.max_revisions} kali. Setujui atau tolak draf terakhir.",
                                           False))
            case _:
                return finish("Analis meminta revisi.", instructions.REVISE_PREFIX + (decision.get("comment") or ""))

    def after_review(state: State) -> str:
        return END if state.get("outcome") or state.get("error") else "agent"

    def nudge(state: State) -> dict[str, Any]:
        return {"messages": [HumanMessage(instructions.CONTINUE)]}

    def no_memo(state: State) -> dict[str, Any]:
        return {"error": _error("model_error", "Agent berhenti tanpa menyusun memo. Coba lagi.", True)}

    builder = StateGraph(State)
    builder.add_node("agent", agent)
    builder.add_node("tools", tools_node)
    builder.add_node("review", review)
    builder.add_node("nudge", nudge)
    builder.add_node("no_memo", no_memo)
    builder.add_edge(START, "agent")
    builder.add_conditional_edges("agent", after_agent, ["tools", "review", "nudge", "no_memo"])
    builder.add_conditional_edges("tools", after_tools, ["agent", "review", END])
    builder.add_conditional_edges("review", after_review, ["agent", END])
    builder.add_edge("nudge", "agent")
    builder.add_edge("no_memo", END)
    return builder
