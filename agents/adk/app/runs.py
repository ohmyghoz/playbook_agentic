"""Run bookkeeping and the mapping from ADK events to catalog events.

Catalog events are stored in SQLite so they can be replayed after a reconnect
or a service restart; ADK's DatabaseSessionService holds the agent state.
The store and the SSE replay are the same as in agents/langgraph/app/runs.py.
"""

import asyncio
import json
import logging
import uuid
from collections.abc import AsyncIterator
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Protocol

import aiosqlite
from google.adk.agents import RunConfig
from google.adk.agents.run_config import StreamingMode
from google.genai import errors as genai_errors
from google.genai import types

from . import instructions
from .config import Settings
from .contract import PAUSE_TYPES, TERMINAL_TYPES, ApprovalDecision, InvestigationRequest, event, tool_finished
from .instructions import SUBMIT_MEMO
from .summarizer import summarize

log = logging.getLogger(__name__)

TOKEN_BATCH_CHARS = 60
USER_ID = "analyst"
CONFIRMATION_CALL = "adk_request_confirmation"


class McpUnavailable(Exception):
    pass


class ServiceError(Exception):
    """An error with an HTTP status and contract code."""

    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status, self.code, self.message = status, code, message


class Engine(Protocol):
    """What the run manager needs from ADK: a runner, its session service, and an MCP check."""

    runner: Any
    sessions: Any

    async def check_tools(self) -> None: ...


@dataclass
class RunInfo:
    run_id: str
    request: dict[str, Any]
    status: str = "running"  # running | paused | finished
    sequence: int = 0
    pending: dict[str, Any] | None = None  # {"approvalId", "toolCallId", "confirmationId"} while paused
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


class RunManager:
    def __init__(self, engine: Engine, store: RunStore, settings: Settings):
        self._engine = engine
        self._store = store
        self._settings = settings
        self._tasks: dict[str, asyncio.Task] = {}
        self._aborts: dict[str, asyncio.Event] = {}
        self._signals: dict[str, asyncio.Event] = {}
        self._locks: dict[str, asyncio.Lock] = {}

    # ---- contract operations -------------------------------------------------------

    async def start(self, request: InvestigationRequest) -> str:
        run = RunInfo(run_id=f"adk-{uuid.uuid4().hex}", request=request.dump())
        await self._engine.sessions.create_session(
            app_name=self._settings.app_name, user_id=USER_ID, session_id=run.run_id,
            state={"request": run.request, "tool_calls_used": 0, "agent_turns": 0, "revision": -1})
        await self._store.save(run)
        await self._emit(run, "run_started", {"caseId": request.case_id, "symbol": request.symbol,
                                              "maxToolCalls": self._settings.max_tool_calls})
        self._spawn(run, _user_text(instructions.first_message(request)))
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
            answer = types.Content(role="user", parts=[types.Part(function_response=types.FunctionResponse(
                id=run.pending["confirmationId"], name=CONFIRMATION_CALL,
                response={"confirmed": True, "payload": decision.dump()}))])
            run.status, run.pending = "running", None
            await self._store.save(run)
        self._spawn(run, answer)

    async def cancel(self, run_id: str) -> None:
        await self._require(run_id)
        task = self._tasks.get(run_id)
        if task and not task.done():
            self._aborts[run_id].set()  # ADK ends run_async cleanly on abort
            done, _ = await asyncio.wait([task], timeout=10)
            if not done:
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

    def _spawn(self, run: RunInfo, message: types.Content) -> None:
        self._aborts[run.run_id] = asyncio.Event()
        self._tasks[run.run_id] = asyncio.create_task(self._execute(run.run_id, message))

    async def _execute(self, run_id: str, message: types.Content) -> None:
        run = await self._require(run_id)
        abort = self._aborts[run_id]
        tokens: list[str] = []

        async def flush() -> None:
            if tokens:
                text = "".join(tokens)
                tokens.clear()
                await self._emit(run, "token", {"text": text})

        try:
            try:
                await self._engine.check_tools()
            except McpUnavailable:
                log.exception("MCP unavailable for run %s", run_id)
                await self._emit(run, "error", {"code": "mcp_unavailable", "retryable": True,
                                                "message": "Server data pasar tidak bisa dihubungi. Coba lagi dalam beberapa menit."})
                return

            text_only_turns = 0
            while True:
                confirmation = None
                called_tools = False
                partial_text_seen = False
                async for e in self._engine.runner.run_async(
                        user_id=USER_ID, session_id=run_id, new_message=message,
                        run_config=RunConfig(streaming_mode=StreamingMode.SSE), abort_signal=abort):
                    if abort.is_set():
                        continue
                    if e.error_code:
                        raise ModelFailure(f"{e.error_code}: {e.error_message}")

                    # SSE sends partial text and then the aggregated text; show the aggregate only when no partials came.
                    texts = [p.text for p in (e.content.parts if e.content and e.content.parts else [])
                             if p.text and not p.thought]
                    if texts and e.author != "user":
                        if e.partial or not partial_text_seen:
                            tokens.extend(texts)
                            if sum(map(len, tokens)) >= TOKEN_BATCH_CHARS:
                                await flush()
                        partial_text_seen = bool(e.partial)
                    if e.partial:
                        continue

                    for fc in e.get_function_calls():
                        if fc.name == CONFIRMATION_CALL:
                            confirmation = fc
                        else:
                            called_tools = True
                            await flush()
                            await self._started(run, fc.id, fc.name, dict(fc.args or {}))
                    for fr in e.get_function_responses():
                        if fr.name != CONFIRMATION_CALL:
                            await flush()
                            await self._on_response(run, fr.id, fr.name, fr.response or {})
                await flush()

                if abort.is_set():
                    await self._close_open_calls(run, "cancelled", "Dihentikan oleh pengguna.")
                    await self._emit(run, "done", {"outcome": "cancelled", "toolCallsUsed": await self._tool_calls_used(run)})
                    return

                state = await self._state(run_id)
                if confirmation is not None and state.get("pending"):
                    pending = state["pending"]
                    run.pending = {"approvalId": pending["approvalId"], "toolCallId": pending["toolCallId"],
                                   "confirmationId": confirmation.id}
                    run.status = "paused"
                    await self._emit(run, "approval_required", {"approvalId": pending["approvalId"],
                                                                "revision": pending["revision"], "memo": pending["memo"]})
                    return
                if error := state.get("error"):
                    await self._close_open_calls(run, error["code"], "Run berhenti.")
                    await self._emit(run, "error", error)
                    return
                if outcome := state.get("outcome"):
                    await self._close_open_calls(run, "internal", "Run selesai.")
                    await self._emit(run, "done", {"outcome": outcome, "toolCallsUsed": state.get("tool_calls_used", 0),
                                                   "memo": state.get("memo")})
                    return

                # The model answered with text only: treat it as narration and ask it to go on.
                text_only_turns = 1 if called_tools else text_only_turns + 1
                if text_only_turns >= self._settings.max_text_only_turns:
                    await self._close_open_calls(run, "model_error", "Agent berhenti tanpa memo.")
                    await self._emit(run, "error", {"code": "model_error", "retryable": True,
                                                    "message": "Agent berhenti tanpa menyusun memo. Coba lagi."})
                    return
                message = _user_text(instructions.CONTINUE)
        except asyncio.CancelledError:
            await self._close_open_calls(run, "cancelled", "Dihentikan oleh pengguna.")
            await self._emit(run, "done", {"outcome": "cancelled", "toolCallsUsed": await self._tool_calls_used(run)})
        except (genai_errors.APIError, ModelFailure):
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

    async def _started(self, run: RunInfo, call_id: str, name: str, args: dict[str, Any]) -> None:
        if call_id in run.seen_calls:
            return
        run.seen_calls.append(call_id)
        run.open_calls[call_id] = name
        if name == SUBMIT_MEMO:
            args = {"memo": "(lihat approval_required)"}
        await self._emit(run, "tool_call_started", {"toolCallId": call_id, "toolName": name, "arguments": args})

    async def _on_response(self, run: RunInfo, call_id: str, name: str, response: dict[str, Any]) -> None:
        if call_id not in run.open_calls:
            return
        if name == SUBMIT_MEMO:
            if response.get("status") == "waiting":
                return  # still open until the analyst decides
            payload = (tool_finished(call_id, name, False, response.get("summary", "Memo tidak sesuai format."),
                                     response.get("code", "invalid_memo"), str(response["error"]))
                       if "error" in response else tool_finished(call_id, name, True, response.get("summary", "Selesai.")))
        else:
            text = response["result"] if isinstance(response.get("result"), str) else json.dumps(response, ensure_ascii=False)
            payload = summarize(call_id, name, text)
        run.open_calls.pop(call_id)
        await self._emit(run, "tool_call_finished", payload)

    async def _close_open_calls(self, run: RunInfo, code: str, message: str) -> None:
        for call_id, name in list(run.open_calls.items()):
            run.open_calls.pop(call_id)
            await self._emit(run, "tool_call_finished", tool_finished(call_id, name, False, message, code, message))

    async def _state(self, run_id: str) -> dict[str, Any]:
        session = await self._engine.sessions.get_session(app_name=self._settings.app_name, user_id=USER_ID,
                                                          session_id=run_id)
        return dict(session.state) if session else {}

    async def _tool_calls_used(self, run: RunInfo) -> int:
        try:
            return int((await self._state(run.run_id)).get("tool_calls_used", 0))
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


class ModelFailure(Exception):
    """An ADK event carrying a model error code."""


def _user_text(text: str) -> types.Content:
    return types.Content(role="user", parts=[types.Part(text=text)])
