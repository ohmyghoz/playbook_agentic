"""Test doubles and helpers: a scripted chat model, canned idx tools, and event assertions."""

import asyncio
import contextlib
import json
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest
from jsonschema import Draft202012Validator
from langchain_core.language_models import BaseChatModel
from langchain_core.messages import AIMessage, BaseMessage
from langchain_core.outputs import ChatGeneration, ChatResult
from langchain_core.tools import BaseTool
from langgraph.checkpoint.sqlite.aio import AsyncSqliteSaver
from pydantic import BaseModel, ConfigDict

from app.config import Settings
from app.contract import InvestigationRequest
from app.graph import build_graph
from app.runs import RunManager, RunStore

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

Step = AIMessage | Callable[[list[BaseMessage]], AIMessage]


class ScriptedModel(BaseChatModel):
    """Answers each request with the next scripted message; the real LangGraph pipeline runs around it."""

    model_config = ConfigDict(arbitrary_types_allowed=True)
    script: list[Any] = []
    fallback: Any = None
    delay: float = 0.0
    requests: list[Any] = []
    error: Any = None

    @property
    def _llm_type(self) -> str:
        return "scripted"

    def bind_tools(self, tools, **kwargs):
        return self

    def _next(self, messages: list[BaseMessage]) -> AIMessage:
        self.requests.append(list(messages))
        if self.error is not None:
            raise self.error
        step = self.script.pop(0) if self.script else self.fallback
        if step is None:
            raise AssertionError("script exhausted")
        return step(messages) if callable(step) else step

    def _generate(self, messages, stop=None, run_manager=None, **kwargs) -> ChatResult:
        return ChatResult(generations=[ChatGeneration(message=self._next(messages))])

    async def _agenerate(self, messages, stop=None, run_manager=None, **kwargs) -> ChatResult:
        if self.delay:
            await asyncio.sleep(self.delay)
        return ChatResult(generations=[ChatGeneration(message=self._next(messages))])


class _AnyArgs(BaseModel):
    model_config = ConfigDict(extra="allow")


class CannedTool(BaseTool):
    """An idx MCP tool stand-in returning a fixed envelope."""

    name: str
    description: str = "canned idx tool"
    args_schema: type[BaseModel] = _AnyArgs
    result: str = ""
    calls: int = 0

    def _run(self, **kwargs: Any) -> str:
        self.calls += 1
        return self.result

    async def _arun(self, **kwargs: Any) -> str:
        return self._run(**kwargs)


def tools() -> list[BaseTool]:
    return [CannedTool(name="idx_stockbit_status", result=STATUS), CannedTool(name="idx_quote", result=QUOTE),
            CannedTool(name="idx_history", result=HISTORY), CannedTool(name="idx_bandar", result=BANDAR_ERROR)]


def call(call_id: str, name: str, args: dict[str, Any] | None = None) -> dict[str, Any]:
    return {"id": call_id, "name": name, "args": args or {}, "type": "tool_call"}


def ai(text: str = "", *calls: dict[str, Any]) -> AIMessage:
    return AIMessage(content=text, tool_calls=list(calls))


def memo_args(*evidence_ids: str, symbol: str = "BBCA") -> dict[str, Any]:
    return {"memo": {
        "symbol": symbol, "anomalyDate": "2026-11-24", "summary": "Volume di atas rata-rata tanpa berita material.",
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
        ai("Memeriksa sesi Stockbit.", call("c1", "idx_stockbit_status")),
        ai("Mengukur harga dan volume.", call("c2", "idx_quote", {"symbols": "BBCA"}), call("c3", "idx_history", {"symbol": "BBCA"})),
        ai("Menyusun memo.", call("m1", "submit_memo", memo_args("c2"))),
    ]


@contextlib.asynccontextmanager
async def manager_for(data_dir: Path, model: ScriptedModel, max_tool_calls: int = 15, factory=None):
    settings = Settings(data_dir=data_dir, max_tool_calls=max_tool_calls)
    async with AsyncSqliteSaver.from_conn_string(str(data_dir / "checkpoints.db")) as saver:
        store = await RunStore.open(data_dir / "service.db")
        graph = build_graph(model, tools(), settings).compile(checkpointer=saver)

        async def graph_factory():
            return graph

        manager = RunManager(factory or graph_factory, store, settings)
        await manager.recover()
        try:
            yield manager
        finally:
            await manager.shutdown()
            await store.close()


async def drain(manager: RunManager, run_id: str, after: int = 0, timeout: float = 10) -> list[dict[str, Any]]:
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
            assert i == len(events) - 1 and not open_calls
    assert (events[-1]["type"] in ("done", "error")) == terminal
    if not terminal:
        assert events[-1]["type"] == "approval_required"


def finished(events: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [e["payload"] for e in events if e["type"] == "tool_call_finished"]


@pytest.fixture
def data_dir(tmp_path: Path) -> Path:
    return tmp_path
