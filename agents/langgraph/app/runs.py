"""Run bookkeeping and the mapping from LangGraph streams to catalog events.

Events are stored in SQLite so they can be replayed after a reconnect or a
service restart; LangGraph's own checkpointer holds the graph state.
"""

import asyncio
import json
import logging
import uuid
from collections.abc import AsyncIterator, Awaitable, Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import aiosqlite
import openai
from langchain_core.messages import AIMessage, AIMessageChunk
from langgraph.types import Command

from .config import Settings
from .contract import PAUSE_TYPES, TERMINAL_TYPES, ApprovalDecision, InvestigationRequest, event, tool_finished

log = logging.getLogger(__name__)

TOKEN_BATCH_CHARS = 60


class McpUnavailable(Exception):
    pass


class ServiceError(Exception):
    """An error with an HTTP status and contract code."""

    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status, self.code, self.message = status, code, message


@dataclass
class RunInfo:
    run_id: str
    request: dict[str, Any]
    status: str = "running"  # running | paused | finished
    sequence: int = 0
    pending: dict[str, Any] | None = None  # {"approvalId", "toolCallId"} while paused
    open_calls: dict[str, str] = field(default_factory=dict)  # toolCallId -> toolName
    seen_calls: list[str] = field(default_factory=list)


class RunStore:
    def __init__(self, db: aiosqlite.Connection):
        self._db = db

    @classmethod
    async def open(cls, path: Path) -> "RunStore":
        db = await aiosqlite.connect(path)
        await db.execute("PRAGMA journal_mode=WAL")
        await db.executescript("""
            CREATE TABLE IF NOT EXISTS runs (run_id TEXT PRIMARY KEY, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events (
                run_id TEXT NOT NULL, sequence INTEGER NOT NULL, body TEXT NOT NULL,
                PRIMARY KEY (run_id, sequence));
        """)
        await db.commit()
        return cls(db)

    async def close(self) -> None:
        await self._db.close()

    async def save(self, run: RunInfo, new_event: dict[str, Any] | None = None) -> None:
        body = json.dumps(run.__dict__, default=str)
        await self._db.execute("INSERT INTO runs(run_id, body) VALUES (?, ?) "
                               "ON CONFLICT(run_id) DO UPDATE SET body = excluded.body", (run.run_id, body))
        if new_event is not None:
            await self._db.execute("INSERT INTO events(run_id, sequence, body) VALUES (?, ?, ?)",
                                   (run.run_id, new_event["sequence"], json.dumps(new_event, ensure_ascii=False)))
        await self._db.commit()

    async def get(self, run_id: str) -> RunInfo | None:
        async with self._db.execute("SELECT body FROM runs WHERE run_id = ?", (run_id,)) as cur:
            row = await cur.fetchone()
        return RunInfo(**json.loads(row[0])) if row else None

    async def by_status(self, status: str) -> list[RunInfo]:
        async with self._db.execute("SELECT body FROM runs") as cur:
            rows = await cur.fetchall()
        return [r for r in (RunInfo(**json.loads(row[0])) for row in rows) if r.status == status]

    async def events_after(self, run_id: str, after: int) -> list[dict[str, Any]]:
        async with self._db.execute("SELECT body FROM events WHERE run_id = ? AND sequence > ? ORDER BY sequence",
                                    (run_id, after)) as cur:
            return [json.loads(row[0]) for row in await cur.fetchall()]


GraphFactory = Callable[[], Awaitable[Any]]


class RunManager:
    def __init__(self, graph_factory: GraphFactory, store: RunStore, settings: Settings):
        self._graph_factory = graph_factory
        self._store = store
        self._settings = settings
        self._tasks: dict[str, asyncio.Task] = {}
        self._signals: dict[str, asyncio.Event] = {}
        self._locks: dict[str, asyncio.Lock] = {}

    # ---- contract operations -------------------------------------------------------

    async def start(self, request: InvestigationRequest) -> str:
        run = RunInfo(run_id=f"lg-{uuid.uuid4().hex}", request=request.dump())
        await self._store.save(run)
        await self._emit(run, "run_started", {"caseId": request.case_id, "symbol": request.symbol,
                                              "maxToolCalls": self._settings.max_tool_calls})
        initial = {"request": run.request, "messages": [("user", _first_message(request))],
                   "tool_calls_used": 0, "agent_turns": 0, "revision": -1}
        self._spawn(run, initial)
        return run.run_id

    async def resume(self, run_id: str, decision: ApprovalDecision) -> None:
        async with self._lock(run_id):
            run = await self._require(run_id)
            if run.status != "paused" or not run.pending:
                raise ServiceError(409, "not_awaiting_approval", f"Run {run_id} tidak sedang menunggu keputusan.")
            if run.pending["approvalId"] != decision.approval_id:
                raise ServiceError(409, "stale_approval",
                                   f"Approval {decision.approval_id} sudah diganti oleh {run.pending['approvalId']}.")
            if decision.decision != "approve" and not (decision.comment or "").strip():
                raise ServiceError(400, "invalid_input", "Komentar wajib untuk revise dan reject.")
            run.status, run.pending = "running", None
            await self._store.save(run)
        self._spawn(run, Command(resume=decision.dump()))

    async def cancel(self, run_id: str) -> None:
        run = await self._require(run_id)
        task = self._tasks.get(run_id)
        if task and not task.done():
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)
            return
        async with self._lock(run_id):
            run = await self._require(run_id)
            if run.status == "paused":
                await self._close_open_calls(run, "cancelled", "Dihentikan oleh pengguna.")
                await self._emit(run, "done", {"outcome": "cancelled", "toolCallsUsed": await self._tool_calls_used(run)})

    async def events(self, run_id: str, after: int) -> AsyncIterator[dict[str, Any] | None]:
        """Stored events after `after`, then live ones until a pause or the end. None means keep-alive."""
        last = after
        while True:
            signal = self._signal(run_id)  # captured before reading, so no emit is missed
            for e in await self._store.events_after(run_id, last):
                yield e
                last = e["sequence"]
                if e["type"] in TERMINAL_TYPES or e["type"] in PAUSE_TYPES:
                    return
            run = await self._require(run_id)
            if run.status != "running" and not await self._store.events_after(run_id, last):
                return
            try:
                await asyncio.wait_for(signal.wait(), timeout=15)
            except TimeoutError:
                yield None

    async def get(self, run_id: str) -> RunInfo | None:
        return await self._store.get(run_id)

    async def recover(self) -> None:
        """Closes runs that were streaming when the service stopped; paused runs stay resumable."""
        for run in await self._store.by_status("running"):
            await self._close_open_calls(run, "internal", "Service berhenti di tengah run.")
            await self._emit(run, "error", {"code": "internal", "retryable": True,
                                            "message": "Investigasi terputus karena service agent dinyalakan ulang. Mulai investigasi baru."})

    async def shutdown(self) -> None:
        tasks = [t for t in self._tasks.values() if not t.done()]
        for t in tasks:
            t.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)

    # ---- execution -----------------------------------------------------------------

    def _spawn(self, run: RunInfo, graph_input: Any) -> None:
        self._tasks[run.run_id] = asyncio.create_task(self._execute(run.run_id, graph_input))

    async def _execute(self, run_id: str, graph_input: Any) -> None:
        run = await self._require(run_id)
        tokens: list[str] = []
        config = {"configurable": {"thread_id": run_id}, "recursion_limit": 200}

        async def flush() -> None:
            if tokens:
                text = "".join(tokens)
                tokens.clear()
                await self._emit(run, "token", {"text": text})

        try:
            try:
                graph = await self._graph_factory()
            except McpUnavailable:
                log.exception("MCP unavailable for run %s", run_id)
                await self._close_open_calls(run, "mcp_unavailable", "Server MCP tidak tersedia.")
                await self._emit(run, "error", {"code": "mcp_unavailable", "retryable": True,
                                                "message": "Server data pasar tidak bisa dihubungi. Coba lagi dalam beberapa menit."})
                return

            async for part in graph.astream(graph_input, config, stream_mode=["messages", "custom"],
                                            version="v2", durability="sync"):
                if part["type"] == "messages":
                    chunk, meta = part["data"]
                    if meta.get("langgraph_node") == "agent" and isinstance(chunk, (AIMessageChunk, AIMessage)):
                        text = _text_of(chunk)
                        if text:
                            tokens.append(text)
                            if sum(map(len, tokens)) >= TOKEN_BATCH_CHARS:
                                await flush()
                elif part["type"] == "custom":
                    await flush()
                    await self._on_custom(run, part["data"])
            await flush()
            await self._finish_pass(run, graph, config)
        except asyncio.CancelledError:
            await flush()
            await self._close_open_calls(run, "cancelled", "Dihentikan oleh pengguna.")
            await self._emit(run, "done", {"outcome": "cancelled", "toolCallsUsed": await self._tool_calls_used(run)})
        except openai.APIError:
            log.exception("Model call failed for run %s", run_id)
            await flush()
            await self._close_open_calls(run, "model_error", "Model gagal merespons.")
            await self._emit(run, "error", {"code": "model_error", "retryable": True,
                                            "message": "Model AI sedang tidak merespons. Coba lagi dalam beberapa menit."})
        except Exception:
            log.exception("Run %s failed", run_id)
            await self._close_open_calls(run, "internal", "Terjadi kesalahan.")
            await self._emit(run, "error", {"code": "internal", "retryable": False,
                                            "message": "Terjadi kesalahan pada agent. Mulai investigasi baru untuk mencoba lagi."})

    async def _finish_pass(self, run: RunInfo, graph: Any, config: dict[str, Any]) -> None:
        snapshot = await graph.aget_state(config)
        interrupts = [i for task in snapshot.tasks for i in task.interrupts]
        if interrupts:
            value = interrupts[0].value
            run.pending = {"approvalId": value["approvalId"], "toolCallId": value["toolCallId"]}
            run.status = "paused"
            await self._emit(run, "approval_required",
                             {"approvalId": value["approvalId"], "revision": value["revision"], "memo": value["memo"]})
            return

        values = snapshot.values
        if error := values.get("error"):
            await self._close_open_calls(run, error["code"], "Run berhenti.")
            await self._emit(run, "error", error)
        elif outcome := values.get("outcome"):
            await self._close_open_calls(run, "internal", "Run selesai.")
            await self._emit(run, "done", {"outcome": outcome, "toolCallsUsed": values.get("tool_calls_used", 0),
                                           "memo": values.get("memo")})
        else:
            await self._close_open_calls(run, "internal", "Run berhenti.")
            await self._emit(run, "error", {"code": "internal", "retryable": True,
                                            "message": "Agent berhenti tanpa hasil. Mulai investigasi baru."})

    async def _on_custom(self, run: RunInfo, data: dict[str, Any]) -> None:
        # A node re-runs from its start when resumed, so started/finished may repeat; keep the first.
        if data.get("kind") == "tool_started":
            if data["id"] in run.seen_calls:
                return
            run.seen_calls.append(data["id"])
            run.open_calls[data["id"]] = data["name"]
            await self._emit(run, "tool_call_started",
                             {"toolCallId": data["id"], "toolName": data["name"], "arguments": data.get("args") or {}})
        elif data.get("kind") == "tool_finished":
            payload = data["payload"]
            if run.open_calls.pop(payload["toolCallId"], None) is None:
                return
            await self._emit(run, "tool_call_finished", payload)

    async def _close_open_calls(self, run: RunInfo, code: str, message: str) -> None:
        for call_id, name in list(run.open_calls.items()):
            run.open_calls.pop(call_id)
            await self._emit(run, "tool_call_finished", tool_finished(call_id, name, False, message, code, message))

    async def _tool_calls_used(self, run: RunInfo) -> int:
        try:
            graph = await self._graph_factory()
            snapshot = await graph.aget_state({"configurable": {"thread_id": run.run_id}})
            return int(snapshot.values.get("tool_calls_used", 0))
        except Exception:
            return 0

    async def _emit(self, run: RunInfo, type_: str, payload: dict[str, Any]) -> None:
        if run.status == "finished":
            return
        run.sequence += 1
        if type_ in TERMINAL_TYPES:
            run.status, run.pending = "finished", None
        new_event = event(run.run_id, run.sequence, type_, {k: v for k, v in payload.items() if v is not None})
        await self._store.save(run, new_event)
        self._signals.pop(run.run_id, asyncio.Event()).set()

    # ---- helpers -------------------------------------------------------------------

    async def _require(self, run_id: str) -> RunInfo:
        run = await self._store.get(run_id)
        if run is None:
            raise ServiceError(404, "run_not_found", f"Run {run_id} tidak dikenal.")
        return run

    def _signal(self, run_id: str) -> asyncio.Event:
        return self._signals.setdefault(run_id, asyncio.Event())

    def _lock(self, run_id: str) -> asyncio.Lock:
        return self._locks.setdefault(run_id, asyncio.Lock())


def _text_of(message: AIMessage | AIMessageChunk) -> str:
    return str(message.text or "")


def _first_message(request: InvestigationRequest) -> str:
    from .instructions import first_message
    return first_message(request)
