"""Test doubles and helpers: a scripted LLM, canned idx tools, and event assertions."""

import asyncio
import contextlib
import json
import warnings
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest
from google.adk.models import BaseLlm, LlmResponse
from google.adk.runners import Runner
from google.adk.sessions import DatabaseSessionService
from google.adk.tools import BaseTool
from google.genai import types
from jsonschema import Draft202012Validator

from app.agent import build_agent
from app.config import Settings
from app.contract import InvestigationRequest
from app.runs import McpUnavailable, RunManager, RunStore

warnings.filterwarnings("ignore")

REPO = Path(__file__).resolve().parents[3]
SCHEMA = Draft202012Validator(json.loads((REPO / "contracts" / "events.schema.json").read_text(encoding="utf-8")))

QUOTE = '{"schema":"idx.quotes.v1","provider":"yahoo","data":[{"symbol":"BBCA","price":5800.0,"change_pct":2.2026,"volume":235635300,"avg_volume_3m":212048028}]}'
HISTORY = '{"schema":"idx.history.v1","data":{"summary":{"candles":66,"avg_volume":210424313,"low":4740.0,"high":6850.0}}}'
STATUS = '{"logged_in":true,"renewable":true}'
BANDAR_ERROR = '{"error":{"code":"provider","message":"ipot: broker summary for BBCA: HTTP 502"},"exit_code":3}'

REQUEST = InvestigationRequest.model_validate({
    "caseId": "case-1", "symbol": "BBCA", "anomalyDate": "2026-11-24",
    "trigger": {"kind": "volume", "rule": "Volume di atas 1,1x rata-rata 3 bulan", "priceChangePct": 2.2,
                "volume": 235635300, "avgVolume": 210424313, "volumeRatio": 1.12},
})

Step = list[types.Part] | Callable[[Any], list[types.Part]]


class ScriptedModel(BaseLlm):
    """Answers each request with the next scripted turn; the real ADK runner and callbacks run around it."""

    model: str = "scripted"
    script: list[Any] = []
    fallback: Any = None
    delay: float = 0.0
    requests: list[Any] = []
    error: Any = None

    async def generate_content_async(self, llm_request, stream: bool = False):
        self.requests.append(list(llm_request.contents))
        if self.delay:
            await asyncio.sleep(self.delay)
        if self.error is not None:
            raise self.error
        step = self.script.pop(0) if self.script else self.fallback
        if step is None:
            raise AssertionError("script exhausted")
        parts = step(llm_request) if callable(step) else step
        yield LlmResponse(content=types.Content(role="model", parts=parts))


class CannedTool(BaseTool):
    """An idx MCP tool stand-in returning a CallToolResult-shaped dict, as McpToolset does."""

    def __init__(self, name: str, envelope: str, is_error: bool = False):
        super().__init__(name=name, description="canned idx tool")
        self._result = {"content": [{"type": "text", "text": envelope}], "isError": is_error}

    def _get_declaration(self) -> types.FunctionDeclaration:
        return types.FunctionDeclaration(name=self.name, description=self.description,
                                         parameters_json_schema={"type": "object", "properties": {}})

    async def run_async(self, *, args: dict[str, Any], tool_context) -> Any:
        return self._result


def tools() -> list[BaseTool]:
    return [CannedTool("idx_stockbit_status", STATUS), CannedTool("idx_quote", QUOTE),
            CannedTool("idx_history", HISTORY), CannedTool("idx_bandar", BANDAR_ERROR, is_error=True)]


def text(t: str) -> types.Part:
    return types.Part(text=t)


def call(call_id: str, name: str, args: dict[str, Any] | None = None) -> types.Part:
    return types.Part(function_call=types.FunctionCall(id=call_id, name=name, args=args or {}))


def memo_args(*evidence_ids: str) -> dict[str, Any]:
    return {"memo": {
        "symbol": "BBCA", "anomalyDate": "2026-11-24", "summary": "Volume di atas rata-rata tanpa berita material.",
        "anomaly": {"description": "Volume 1,1x rata-rata 3 bulan.", "volume": 235635300},
        "findings": [{"category": "price_volume", "statement": "Volume di atas rata-rata.",
                      "evidence": [{"toolCallId": i, "toolName": "idx_quote", "detail": "volume"} for i in evidence_ids]}],
        "unavailableData": [{"toolName": "idx_bandar", "reason": "IPOT HTTP 502"}],
        "evidenceLevel": "weak", "evidenceRationale": "Penyimpangan kecil.",
        "recommendation": "monitor", "recommendationRationale": "Pantau sesi berikutnya.",
        "disclaimer": "parafrase",
    }}


def until_memo() -> list[Step]:
    return [
        [text("Memeriksa sesi Stockbit."), call("c1", "idx_stockbit_status")],
        [text("Mengukur harga dan volume."), call("c2", "idx_quote", {"symbols": "BBCA"}), call("c3", "idx_history", {"symbol": "BBCA"})],
        [text("Menyusun memo."), call("m1", "submit_memo", memo_args("c2"))],
    ]


class FakeEngine:
    def __init__(self, model: ScriptedModel, data_dir: Path, settings: Settings, tools_available: bool = True):
        self.sessions = DatabaseSessionService(db_url=f"sqlite+aiosqlite:///{(data_dir / 'sessions.db').as_posix()}")
        self.runner = Runner(app_name=settings.app_name, agent=build_agent(model, tools(), settings),
                             session_service=self.sessions)
        self._tools_available = tools_available

    async def check_tools(self) -> None:
        if not self._tools_available:
            raise McpUnavailable("down")


@contextlib.asynccontextmanager
async def manager_for(data_dir: Path, model: ScriptedModel, max_tool_calls: int = 15, tools_available: bool = True):
    settings = Settings(data_dir=data_dir, max_tool_calls=max_tool_calls)
    engine = FakeEngine(model, data_dir, settings, tools_available)
    store = await RunStore.open(data_dir / "service.db")
    manager = RunManager(engine, store, settings)
    await manager.recover()
    try:
        yield manager
    finally:
        await manager.shutdown()
        await store.close()
        await engine.runner.close()


async def drain(manager: RunManager, run_id: str, after: int = 0, timeout: float = 15) -> list[dict[str, Any]]:
    async def collect():
        return [e async for e in manager.events(run_id, after) if e is not None]
    return await asyncio.wait_for(collect(), timeout)


def assert_well_formed(events: list[dict[str, Any]], terminal: bool) -> None:
    """Schema, contiguous sequence from 1, run_started first, paired tool calls, nothing after the end."""
    assert events and events[0]["type"] == "run_started"
    open_calls: dict[str, str] = {}
    for i, e in enumerate(events):
        errors = [f"{list(err.path)}: {err.message}" for err in SCHEMA.iter_errors(e)]
        assert not errors, f"event {e['sequence']} {e['type']}: {errors}"
        assert e["sequence"] == i + 1
        p = e["payload"]
        if e["type"] == "tool_call_started":
            assert p["toolCallId"] not in open_calls
            open_calls[p["toolCallId"]] = p["toolName"]
        elif e["type"] == "tool_call_finished":
            assert open_calls.pop(p["toolCallId"]) == p["toolName"]
        elif e["type"] in ("done", "error"):
            assert i == len(events) - 1 and not open_calls, f"open calls at the end: {open_calls}"
    assert (events[-1]["type"] in ("done", "error")) == terminal, events[-1]
    if not terminal:
        assert events[-1]["type"] == "approval_required"


def finished(events: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [e["payload"] for e in events if e["type"] == "tool_call_finished"]


def function_responses(contents: list[types.Content]) -> list[types.FunctionResponse]:
    return [p.function_response for c in contents for p in (c.parts or []) if p.function_response]


@pytest.fixture
def data_dir(tmp_path: Path) -> Path:
    return tmp_path
