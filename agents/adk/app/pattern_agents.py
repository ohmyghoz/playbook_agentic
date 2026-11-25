"""Pattern detection (use case 2) agents on Google ADK.

Workers: one LlmAgent per worker with its role's MCP tools (a McpToolset filtered per role) and
finish_worker; limits in before_tool_callback. They run in their own Runner with an in-memory
session: a worker does not outlive the job, and a service restart ends the run (run_interrupted).

Report writer: an LlmAgent with only submit_report, which pauses for the analyst with Tool
Confirmation in a DatabaseSessionService session, exactly like submit_memo. See EVENT-MAPPING.md.
"""

import json
from typing import Any

from google.adk.agents import LlmAgent
from google.adk.agents.readonly_context import ReadonlyContext
from google.adk.tools import BaseTool, FunctionTool
from google.adk.tools.tool_context import ToolContext

from . import instructions
from .config import Settings
from .contract import PatternJob
from .pattern_orchestrator import (FINISH_WORKER, MAX_INVALID_REPORTS, REPORT_TEMPLATE, SUBMIT_REPORT, JobBudget,
                                   clean_worker_report, validate_report, worker_instructions)
from .summarizer import LIMIT_MARKER


def _error(code: str, message: str, retryable: bool) -> dict[str, Any]:
    return {"code": code, "message": message, "retryable": retryable}


def build_worker_agent(index: int, model: Any, tools: Any, worker: dict[str, Any], job: PatternJob,
                       budget: JobBudget, known: list[str], outcome: dict[str, Any]) -> LlmAgent:
    """One worker. `outcome` collects steps, warned, exceeded and the finish_worker report."""
    max_steps = job.limits.max_steps_per_worker
    outcome.update({"steps": 0, "warned": False, "exceeded": False, "report": None})

    def finish_worker(summary: str, tool_context: ToolContext, dominantBrokers: list[dict] | None = None,
                      leads: list[dict] | None = None, findings: list[dict] | None = None) -> dict:
        """Laporkan temuan worker ke orchestrator. Panggil tepat satu kali di akhir tugas."""
        outcome["report"] = clean_worker_report({"summary": summary, "dominantBrokers": dominantBrokers or [],
                                                 "leads": leads or [], "findings": findings or []})
        tool_context.actions.skip_summarization = True  # the worker's job is over; no closing reply
        return {"result": "Temuan diterima."}

    def before_tool(tool: BaseTool, args: dict[str, Any], tool_context: ToolContext) -> dict | None:
        if tool.name == FINISH_WORKER:
            return None
        if outcome["steps"] >= max_steps or not budget.try_use():
            if outcome["warned"]:
                outcome["exceeded"] = True
                tool_context.actions.skip_summarization = True
            outcome["warned"] = True
            return {"result": f"{LIMIT_MARKER} Batas panggilan tool tercapai. Panggil finish_worker sekarang dengan temuan yang ada."}
        outcome["steps"] += 1
        known.append(tool_context.function_call_id)
        return None

    def after_tool(tool: BaseTool, args: dict[str, Any], tool_context: ToolContext, tool_response: Any) -> dict | None:
        if tool.name == FINISH_WORKER:
            return None
        text = tool_response.get("result") if isinstance(tool_response, dict) and isinstance(tool_response.get("result"), str) \
            else json.dumps(tool_response, ensure_ascii=False, default=str)
        if text.startswith(LIMIT_MARKER):
            return None
        return {"result": f"[toolCallId: {tool_context.function_call_id}]\n{text}"}

    role_tools = tools if isinstance(tools, list) else [tools]
    return LlmAgent(
        name=f"worker_{index}",
        model=model,
        instruction=worker_instructions(worker["role"], worker["target"], job),
        tools=[*role_tools, FunctionTool(finish_worker)],
        before_tool_callback=before_tool,
        after_tool_callback=after_tool,
    )


def _declaration_only(report: dict) -> dict:
    """Ajukan laporan deteksi pola untuk ditelaah analis. Analis bisa menyetujui, meminta revisi, atau menolak."""
    raise NotImplementedError


class SubmitReportTool(FunctionTool):
    """submit_report with Tool Confirmation; mirrors SubmitMemoTool in agent.py. Session state keys:
    request, facts, known_call_ids, revision, invalid_reports, pending, report, outcome, error."""

    def __init__(self, settings: Settings):
        super().__init__(self._submit)
        self.name = SUBMIT_REPORT
        self.description = _declaration_only.__doc__
        self._settings = settings

    def _get_declaration(self):
        return FunctionTool(_declaration_only)._get_declaration().model_copy(update={"name": SUBMIT_REPORT})

    def _submit(self, report: dict, tool_context: ToolContext) -> dict:
        state = tool_context.state
        confirmation = tool_context.tool_confirmation

        if confirmation is None:
            report_json, problem = validate_report({"report": report}, state["facts"], set(state.get("known_call_ids", [])))
            if report_json is None:
                attempts = state.get("invalid_reports", 0) + 1
                state["invalid_reports"] = attempts
                if attempts > MAX_INVALID_REPORTS:
                    state["error"] = _error("model_error", "Agent tidak berhasil menyusun laporan yang sesuai format. Coba lagi.", True)
                    tool_context.actions.skip_summarization = True
                    return {"error": problem, "code": "invalid_report", "summary": "Laporan tidak sesuai format."}
                return {"error": problem, "code": "invalid_report", "summary": "Laporan belum sesuai format; agent memperbaikinya."}

            revision = state.get("revision", -1) + 1
            state["pending"] = {"approvalId": f"approval_{revision + 1}", "revision": revision, "report": report_json,
                                "toolCallId": tool_context.function_call_id}
            tool_context.request_confirmation(hint="Keputusan analis untuk draf laporan.", payload=state["pending"])
            tool_context.actions.skip_summarization = True
            return {"status": "waiting"}

        # Re-entered with the analyst's decision (possibly in a new process).
        pending = state.get("pending") or {}
        decision = confirmation.payload or {}
        revision = pending.get("revision", 0)
        state["revision"] = revision
        state["report"] = pending.get("report")
        state["pending"] = None
        match decision.get("decision"):
            case "approve":
                state["outcome"] = "approved"
                tool_context.actions.skip_summarization = True
                return {"result": "Laporan disetujui analis.", "summary": "Laporan disetujui analis."}
            case "reject":
                state["outcome"] = "rejected"
                tool_context.actions.skip_summarization = True
                return {"result": "Laporan ditolak analis.", "summary": "Laporan ditolak analis."}
            case _ if revision >= self._settings.max_revisions:
                state["error"] = _error("revision_limit_exceeded",
                                        f"Laporan sudah direvisi {self._settings.max_revisions} kali. Setujui atau tolak draf terakhir.",
                                        False)
                tool_context.actions.skip_summarization = True
                return {"result": "Batas revisi tercapai.", "summary": "Revisi diminta, tetapi batas revisi sudah tercapai."}
            case _:
                return {"result": instructions.REVISE_PREFIX + (decision.get("comment") or ""), "summary": "Analis meminta revisi."}


def build_report_agent(model: Any, settings: Settings) -> LlmAgent:
    def instruction(ctx: ReadonlyContext) -> str:
        return REPORT_TEMPLATE

    return LlmAgent(name="penyusun_laporan", model=model, instruction=instruction, tools=[SubmitReportTool(settings)])
