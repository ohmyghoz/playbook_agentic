"""FastAPI service implementing contracts/agent-service-api.md on port 8102.

Run: .venv\\Scripts\\python -m uvicorn app.main:app --host 127.0.0.1 --port 8102
The HTTP layer is the same as agents/langgraph/app/main.py; only the engine differs.
"""

import contextlib
import json
import logging
import warnings
from collections.abc import AsyncIterator
from typing import Any

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, StreamingResponse
from pydantic import ValidationError

from .config import Settings
from .contract import ApprovalDecision, InvestigationRequest
from .instructions import ALLOWED_MCP_TOOLS
from .runs import McpUnavailable, RunManager, RunStore, ServiceError

log = logging.getLogger(__name__)


class AdkEngine:
    """LlmAgent + Runner + DatabaseSessionService, with idx MCP tools over streamable HTTP."""

    def __init__(self, settings: Settings):
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")  # ADK marks tool confirmation as experimental
            from google.adk.models.google_llm import Gemini
            from google.adk.runners import Runner
            from google.genai import types
            from google.adk.sessions import DatabaseSessionService
            from google.adk.tools.mcp_tool import McpToolset, StreamableHTTPConnectionParams

            from .agent import build_agent

        self._settings = settings
        self.toolset = McpToolset(
            connection_params=StreamableHTTPConnectionParams(
                url=settings.mcp_endpoint, headers={"Authorization": f"Bearer {settings.mcp_token or ''}"}, timeout=30),
            tool_filter=sorted(ALLOWED_MCP_TOOLS))
        db_path = (settings.data_dir / "sessions.db").as_posix()
        self.sessions = DatabaseSessionService(db_url=f"sqlite+aiosqlite:///{db_path}")
        # The ASD allows two retries of transient model errors (429, 5xx), like MAF and LangGraph do.
        model = Gemini(model=settings.model_name, retry_options=types.HttpRetryOptions(
            attempts=3, initial_delay=2.0, max_delay=20.0, http_status_codes=[429, 500, 502, 503, 504]))
        self.runner = Runner(app_name=settings.app_name, agent=build_agent(model, self.toolset, settings),
                             session_service=self.sessions)

    async def check_tools(self) -> None:
        # ADK keeps running without tools when the server is down; an investigation without data is useless.
        if not self._settings.mcp_token:
            raise McpUnavailable("IDX_MCP_TOKEN is not set.")
        try:
            tools = await self.toolset.get_tools()
        except Exception as ex:
            raise McpUnavailable(f"MCP server at {self._settings.mcp_endpoint} is unavailable.") from ex
        if not tools:
            raise McpUnavailable(f"MCP server at {self._settings.mcp_endpoint} returned no tools.")

    async def close(self) -> None:
        await self.runner.close()


def _error(status: int, code: str, message: str) -> JSONResponse:
    return JSONResponse({"error": {"code": code, "message": message}}, status_code=status)


async def _body(request: Request, model: type) -> Any:
    try:
        return model.model_validate(await request.json())
    except (ValidationError, json.JSONDecodeError, ValueError) as ex:
        raise ServiceError(400, "invalid_input", str(ex)[:500]) from ex


@contextlib.asynccontextmanager
async def _production_manager() -> AsyncIterator[RunManager]:
    settings = Settings()
    if not settings.api_key:
        raise RuntimeError("GOOGLE_API_KEY is not set. Put it in agents/adk/.env.")
    settings.data_dir.mkdir(parents=True, exist_ok=True)

    engine = AdkEngine(settings)
    store = await RunStore.open(settings.data_dir / "service.db")
    manager = RunManager(engine, store, settings)
    await manager.recover()
    try:
        yield manager
    finally:
        await manager.shutdown()
        await store.close()
        await engine.close()


def create_app(manager_context=_production_manager) -> FastAPI:
    @contextlib.asynccontextmanager
    async def lifespan(app: FastAPI):
        async with manager_context() as manager:
            app.state.manager = manager
            yield

    app = FastAPI(title="UMA investigator (Google ADK)", lifespan=lifespan)

    @app.exception_handler(ServiceError)
    async def service_error(_: Request, ex: ServiceError) -> JSONResponse:
        return _error(ex.status, ex.code, ex.message)

    def manager(request: Request) -> RunManager:
        m = getattr(request.app.state, "manager", None)
        if m is None:
            raise ServiceError(503, "not_ready", "Service belum siap.")
        return m

    @app.get("/health")
    async def health(request: Request) -> dict[str, str]:
        manager(request)
        return {"status": "ok"}

    @app.post("/runs", status_code=201)
    async def start_run(request: Request) -> dict[str, str]:
        body = await _body(request, InvestigationRequest)
        return {"runId": await manager(request).start(body)}

    @app.get("/runs/{run_id}/events")
    async def run_events(run_id: str, request: Request, after: int = 0) -> StreamingResponse:
        m = manager(request)
        if await m.get(run_id) is None:
            raise ServiceError(404, "run_not_found", f"Run {run_id} tidak dikenal.")
        last_event_id = request.headers.get("last-event-id")
        start = int(last_event_id) if last_event_id and last_event_id.isdigit() else after

        async def frames() -> AsyncIterator[str]:
            async for e in m.events(run_id, start):
                if e is None:
                    yield ": ping\n\n"
                else:
                    yield f"id: {e['sequence']}\nevent: {e['type']}\ndata: {json.dumps(e, ensure_ascii=False)}\n\n"

        return StreamingResponse(frames(), media_type="text/event-stream",
                                 headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"})

    @app.post("/runs/{run_id}/resume", status_code=202)
    async def resume_run(run_id: str, request: Request) -> dict[str, Any]:
        decision = await _body(request, ApprovalDecision)
        await manager(request).resume(run_id, decision)
        return {}

    @app.post("/runs/{run_id}/cancel", status_code=202)
    async def cancel_run(run_id: str, request: Request) -> dict[str, Any]:
        await manager(request).cancel(run_id)
        return {}

    return app


app = create_app()
