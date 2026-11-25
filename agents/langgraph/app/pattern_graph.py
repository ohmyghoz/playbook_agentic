"""Pattern detection (use case 2) as a LangGraph StateGraph.

    START -> workers -> report_agent -> review -> report_agent | END   (interrupt for the analyst)
                                     -> nudge  -> report_agent
                                     -> no_report -> END

The workers node runs PatternOrchestrator (code); each worker is a small compiled StateGraph
(worker_agent <-> worker_tools, ending in finish_worker) built for that worker. Progress goes
to the "custom" stream with the worker's agentName; the service turns it into catalog events.
See EVENT-MAPPING.md and contracts/asd-pattern-detection.md.
"""

import json
from collections.abc import Callable, Sequence
from typing import Annotated, Any, TypedDict

import openai
from langchain_core.language_models import BaseChatModel
from langchain_core.messages import AIMessage, AnyMessage, HumanMessage, SystemMessage, ToolMessage
from langchain_core.tools import BaseTool, tool
from langgraph.config import get_stream_writer
from langgraph.graph import END, START, StateGraph
from langgraph.graph.message import add_messages
from langgraph.types import interrupt
from pydantic import BaseModel, Field

from . import instructions
from .config import Settings
from .contract import InvestigationRequest, PatternJob, PatternReport, WorkerReport, tool_finished
from .pattern_orchestrator import (FINISH_WORKER, MAX_INVALID_REPORTS, REPORT_CONTINUE, REPORT_TEMPLATE, ROLE_TOOLS,
                                   SUBMIT_REPORT, WORKER_CONTINUE, JobBudget, PatternOrchestrator, WorkerOutcome,
                                   clean_worker_report, validate_report, worker_instructions)
from .summarizer import LIMIT_MARKER, result_text, summarize


class PatternState(TypedDict, total=False):
    request: dict[str, Any]
    messages: Annotated[list[AnyMessage], add_messages]  # the report writer's conversation
    facts: dict[str, Any] | None
    known_call_ids: list[str]
    tool_calls_used: int
    revision: int
    invalid_reports: int
    text_only_turns: int
    report: dict[str, Any] | None
    outcome: str | None
    error: dict[str, Any] | None


class SubmitReportArgs(BaseModel):
    report: PatternReport = Field(description="Laporan deteksi pola sesuai skema PatternReport.")


@tool(SUBMIT_REPORT, args_schema=SubmitReportArgs)
def submit_report_tool(report: PatternReport) -> str:
    """Ajukan laporan deteksi pola untuk ditelaah analis. Analis bisa menyetujui, meminta revisi, atau menolak."""
    return "Laporan disetujui analis."  # never executed: the review node handles this call


@tool(FINISH_WORKER, args_schema=WorkerReport)
def finish_worker_tool(**kwargs: Any) -> str:
    """Laporkan temuan worker ke orchestrator. Panggil tepat satu kali di akhir tugas."""
    return "Temuan diterima."  # never executed: the worker graph reads the arguments


def _error(code: str, message: str, retryable: bool) -> dict[str, Any]:
    return {"code": code, "message": message, "retryable": retryable}


def _last_ai(messages: Sequence[AnyMessage]) -> AIMessage:
    return next(m for m in reversed(messages) if isinstance(m, AIMessage))


class WorkerState(TypedDict, total=False):
    messages: Annotated[list[AnyMessage], add_messages]
    steps: int
    warned: bool
    exceeded: bool
    nudges: int
    report: dict[str, Any] | None


def build_worker_graph(model: BaseChatModel, tools_by_name: dict[str, BaseTool], worker: dict[str, Any], job: PatternJob,
                       budget: JobBudget, emit: Callable[[dict[str, Any]], None], known: list[str]):
    """One worker: a model with its role's MCP tools and finish_worker. Tool events go to `emit`."""
    agent_name = worker["workerId"]
    role_tools = [tools_by_name[n] for n in sorted(ROLE_TOOLS[worker["role"]]) if n in tools_by_name]
    bound = model.bind_tools([*role_tools, finish_worker_tool])
    system = SystemMessage(worker_instructions(worker["role"], worker["target"], job))
    max_steps = job.limits.max_steps_per_worker

    async def worker_agent(state: WorkerState) -> dict[str, Any]:
        return {"messages": [await bound.ainvoke([system, *state["messages"]])]}

    def after_agent(state: WorkerState) -> str:
        calls = _last_ai(state["messages"]).tool_calls
        if any(c["name"] == FINISH_WORKER for c in calls):
            return "finish"
        if calls:
            return "worker_tools"
        return END if state.get("nudges", 0) >= 2 else "nudge"

    async def worker_tools(state: WorkerState) -> dict[str, Any]:
        steps, warned, exceeded = state.get("steps", 0), state.get("warned", False), False
        messages = []
        for call in _last_ai(state["messages"]).tool_calls:
            call_id, name = call["id"], call["name"]
            emit({"kind": "tool_started", "id": call_id, "name": name, "args": call["args"], "agent": agent_name})
            if name not in tools_by_name or name not in ROLE_TOOLS[worker["role"]]:
                # Counts as a step: a model that keeps asking for a missing tool must still reach its limit.
                steps += 1
                available = ", ".join([t.name for t in role_tools] + [FINISH_WORKER])
                text = f"Tool {name} tidak ada. Tool yang tersedia: {available}."
                payload = tool_finished(call_id, name, False, f"Tool {name} tidak tersedia untuk worker ini.", "unknown_tool")
            elif steps >= max_steps or not budget.try_use():
                exceeded = exceeded or warned
                warned = True
                text = f"{LIMIT_MARKER} Batas panggilan tool tercapai. Panggil finish_worker sekarang dengan temuan yang ada."
                payload = summarize(call_id, name, text)
            else:
                steps += 1
                known.append(call_id)
                try:
                    result = await tools_by_name[name].ainvoke({**call, "type": "tool_call"})
                    raw, is_error = result_text(result.content), getattr(result, "status", "success") == "error"
                except Exception as ex:  # transport failures raise; the worker continues without this data
                    raw, is_error = f"Tool gagal: {ex}", True
                text, payload = f"[toolCallId: {call_id}]\n{raw}", summarize(call_id, name, raw, is_error)
            emit({"kind": "tool_finished", "payload": payload, "agent": agent_name})
            messages.append(ToolMessage(content=text, tool_call_id=call_id, name=name,
                                        status="success" if payload["status"] == "ok" else "error"))
        return {"messages": messages, "steps": steps, "warned": warned, "exceeded": exceeded}

    def after_tools(state: WorkerState) -> str:
        return END if state.get("exceeded") else "worker_agent"

    def finish(state: WorkerState) -> dict[str, Any]:
        call = next(c for c in _last_ai(state["messages"]).tool_calls if c["name"] == FINISH_WORKER)
        report = clean_worker_report(call["args"])
        return {"report": report.dump() if report else None}

    def nudge(state: WorkerState) -> dict[str, Any]:
        return {"messages": [HumanMessage(WORKER_CONTINUE)], "nudges": state.get("nudges", 0) + 1}

    builder = StateGraph(WorkerState)
    builder.add_node("worker_agent", worker_agent)
    builder.add_node("worker_tools", worker_tools)
    builder.add_node("finish", finish)
    builder.add_node("nudge", nudge)
    builder.add_edge(START, "worker_agent")
    builder.add_conditional_edges("worker_agent", after_agent, ["worker_tools", "finish", "nudge", END])
    builder.add_conditional_edges("worker_tools", after_tools, ["worker_agent", END])
    builder.add_edge("nudge", "worker_agent")
    builder.add_edge("finish", END)
    return builder.compile()


def build_pattern_graph(model: BaseChatModel, tools: Sequence[BaseTool], settings: Settings) -> StateGraph:
    tools_by_name = {t.name: t for t in tools}
    report_bound = model.bind_tools([submit_report_tool])

    async def workers(state: PatternState) -> dict[str, Any]:
        writer = get_stream_writer()
        job = InvestigationRequest.model_validate(state["request"]).job
        budget = JobBudget(job.limits.max_tool_calls, job.limits.max_minutes)
        known: list[str] = []

        async def narrate(text: str) -> None:
            writer({"kind": "narration", "text": text})

        async def run_worker(worker: dict[str, Any]) -> WorkerOutcome:
            writer({"kind": "worker_started", "payload": worker})
            final: dict[str, Any] = {}
            try:
                graph = build_worker_graph(model, tools_by_name, worker, job, budget, writer, known)
                final = await graph.ainvoke({"messages": [HumanMessage("Mulai tugas Anda.")]},
                                            {"recursion_limit": 4 * job.limits.max_steps_per_worker + 20})
            except Exception:  # a failed worker does not fail the job (asd-pattern-detection.md)
                final = {}
            report = WorkerReport.model_validate(final["report"]) if final.get("report") else None
            limited = final.get("warned") or final.get("exceeded")
            status = "limit_reached" if limited else "ok" if report else "error"
            summary = report.summary if report else (
                f"Batas {job.limits.max_steps_per_worker} langkah worker tercapai sebelum temuan dilaporkan." if limited
                else "Worker berhenti tanpa melaporkan temuan.")
            payload = {"workerId": worker["workerId"], "role": worker["role"], "status": status, "summary": summary[:500]}
            if report and report.leads:
                payload["leads"] = [lead.dump() for lead in report.leads]
            writer({"kind": "worker_finished", "payload": payload})
            return WorkerOutcome(worker, status, report)

        facts = await PatternOrchestrator(job, budget, run_worker, narrate).run()
        dumped = facts.dump()
        return {"facts": dumped, "known_call_ids": known, "tool_calls_used": budget.used,
                "messages": [HumanMessage("Hasil worker:\n" + json.dumps(dumped, ensure_ascii=False))]}

    async def report_agent(state: PatternState) -> dict[str, Any]:
        reply = await report_bound.ainvoke([SystemMessage(REPORT_TEMPLATE), *state["messages"]])
        text_only = 0 if reply.tool_calls else state.get("text_only_turns", 0) + 1
        return {"messages": [reply], "text_only_turns": text_only}

    def after_report_agent(state: PatternState) -> str:
        calls = _last_ai(state["messages"]).tool_calls
        if any(c["name"] == SUBMIT_REPORT for c in calls):
            return "review"
        if calls:
            return "nudge"  # only submit_report exists; anything else is a mistake
        return "no_report" if state.get("text_only_turns", 0) >= settings.max_text_only_turns else "nudge"

    async def review(state: PatternState) -> dict[str, Any]:
        # Re-runs from the top when resumed: everything before interrupt() must be repeatable.
        writer = get_stream_writer()
        calls = _last_ai(state["messages"]).tool_calls
        submits = [c for c in calls if c["name"] == SUBMIT_REPORT]
        call = submits[0]
        extra = [ToolMessage("Hanya satu laporan yang diproses per giliran.", tool_call_id=c["id"])
                 for c in calls if c["id"] != call["id"]]
        writer({"kind": "tool_started", "id": call["id"], "name": SUBMIT_REPORT, "args": {"draft": "(lihat approval_required)"}})

        report, problem = validate_report(call["args"], state["facts"], set(state.get("known_call_ids", [])))
        if report is None:
            attempts = state.get("invalid_reports", 0) + 1
            update: dict[str, Any] = {"messages": [ToolMessage(problem, tool_call_id=call["id"], status="error"), *extra],
                                      "invalid_reports": attempts}
            if attempts > MAX_INVALID_REPORTS:
                writer({"kind": "tool_finished", "payload": tool_finished(
                    call["id"], SUBMIT_REPORT, False, "Laporan tidak sesuai format.", "invalid_report", problem)})
                update["error"] = _error("model_error", "Agent tidak berhasil menyusun laporan yang sesuai format. Coba lagi.", True)
            else:
                writer({"kind": "tool_finished", "payload": tool_finished(
                    call["id"], SUBMIT_REPORT, False, "Laporan belum sesuai format; agent memperbaikinya.", "invalid_report", problem)})
            return update

        revision = state.get("revision", -1) + 1
        decision = interrupt({"approvalId": f"approval_{revision + 1}", "revision": revision, "report": report,
                              "toolCallId": call["id"]})

        def finish(summary: str, reply: str, **changes: Any) -> dict[str, Any]:
            writer({"kind": "tool_finished", "payload": tool_finished(call["id"], SUBMIT_REPORT, True, summary)})
            return {"messages": [ToolMessage(reply, tool_call_id=call["id"]), *extra], "revision": revision,
                    "report": report, **changes}

        match decision["decision"]:
            case "approve":
                return finish("Laporan disetujui analis.", "Laporan disetujui analis.", outcome="approved")
            case "reject":
                return finish("Laporan ditolak analis.", "Laporan ditolak analis.", outcome="rejected")
            case _ if revision >= settings.max_revisions:
                return finish("Revisi diminta, tetapi batas revisi sudah tercapai.", "Batas revisi tercapai.",
                              error=_error("revision_limit_exceeded",
                                           f"Laporan sudah direvisi {settings.max_revisions} kali. Setujui atau tolak draf terakhir.",
                                           False))
            case _:
                return finish("Analis meminta revisi.", instructions.REVISE_PREFIX + (decision.get("comment") or ""))

    def after_review(state: PatternState) -> str:
        return END if state.get("outcome") or state.get("error") else "report_agent"

    def nudge(state: PatternState) -> dict[str, Any]:
        calls = _last_ai(state["messages"]).tool_calls
        return {"messages": [*(ToolMessage("Tool tidak tersedia.", tool_call_id=c["id"]) for c in calls),
                             HumanMessage(REPORT_CONTINUE)]}

    def no_report(state: PatternState) -> dict[str, Any]:
        return {"error": _error("model_error", "Agent berhenti tanpa menyusun laporan. Coba lagi.", True)}

    builder = StateGraph(PatternState)
    builder.add_node("workers", workers)
    builder.add_node("report_agent", report_agent)
    builder.add_node("review", review)
    builder.add_node("nudge", nudge)
    builder.add_node("no_report", no_report)
    builder.add_edge(START, "workers")
    builder.add_edge("workers", "report_agent")
    builder.add_conditional_edges("report_agent", after_report_agent, ["review", "nudge", "no_report"])
    builder.add_conditional_edges("review", after_review, ["report_agent", END])
    builder.add_edge("nudge", "report_agent")
    builder.add_edge("no_report", END)
    return builder
