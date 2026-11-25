"""FastAPI service implementing contracts/agent-service-api.md on port 8101.

Run: .venv\\Scripts\\python -m uvicorn app.main:app --host 127.0.0.1 --port 8101
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


class ToolProvider:
    """idx MCP tools over streamable HTTP, filtered to the ASD allowlist; reconnects after a failure."""

    def __init__(self, settings: Settings):
        self._settings = settings
        self._stack: contextlib.AsyncExitStack | None = None
        self._tools: list[Any] | None = None
        self._all: list[Any] | None = None

    async def get_all(self) -> list[Any]:
        """Every tool on the server, for pattern-detection workers; each worker binds only its role's tools."""
        await self.get()
        return self._all or []

    async def get(self) -> list[Any]:
        if self._tools is not None:
            return self._tools
        if not self._settings.mcp_token:
            raise McpUnavailable("IDX_MCP_TOKEN is not set.")

        with warnings.catch_warnings():
            warnings.simplefilter("ignore")  # langchain.mcp emits LangChainBetaWarning on import
            from fastmcp.client import Client
            from fastmcp.client.transports import StreamableHttpTransport
            from langchain.mcp import MCPAdapter

        stack = contextlib.AsyncExitStack()
        try:
            transport = StreamableHttpTransport(self._settings.mcp_endpoint,
                                                headers={"Authorization": f"Bearer {self._settings.mcp_token}"})
            adapter = await stack.enter_async_context(MCPAdapter(Client(transport)))
            tools = await adapter.list_tools()
        except Exception as ex:
            await stack.aclose()
            raise McpUnavailable(f"MCP server at {self._settings.mcp_endpoint} is unavailable.") from ex

        self._stack = stack
        self._all = list(tools)
        self._tools = [t for t in tools if t.name in ALLOWED_MCP_TOOLS]
        return self._tools

    async def close(self) -> None:
        if self._stack is not None:
            await self._stack.aclose()


class CannedToolProvider:
    """Fixed tools for AGENT_SCRIPT mode."""

    def __init__(self, tools: list[Any]):
        self._tools = tools

    async def get(self) -> list[Any]:
        return self._tools

    async def get_all(self) -> list[Any]:
        return self._tools

    async def close(self) -> None:
        pass


def _error(status: int, code: str, message: str) -> JSONResponse:
    return JSONResponse({"error": {"code": code, "message": message}}, status_code=status)


async def _body(request: Request, model: type) -> Any:
    try:
        return model.model_validate(await request.json())
    except (ValidationError, json.JSONDecodeError, ValueError) as ex:
        raise ServiceError(400, "invalid_input", str(ex)[:500]) from ex


@contextlib.asynccontextmanager
async def _production_manager() -> AsyncIterator[RunManager]:
    from langchain_openai import ChatOpenAI
    from langgraph.checkpoint.sqlite.aio import AsyncSqliteSaver

    from .graph import build_graph
    from .pattern_graph import build_pattern_graph

    settings = Settings()
    settings.data_dir.mkdir(parents=True, exist_ok=True)

    if settings.script_path:
        # Contract tests: scripted model and canned tools, no API key or MCP server.
        from .scripted import ScriptedChatModel, Script, canned_tools
        script = Script(settings.script_path)
        model = ScriptedChatModel(script=script)
        tools = CannedToolProvider(canned_tools(script))
    else:
        if not settings.api_key:
            raise RuntimeError("OPENROUTER_API_KEY is not set. Put it in agents/langgraph/.env.")
        model = ChatOpenAI(model=settings.model_name, base_url=settings.model_base_url, api_key=settings.api_key,
                           max_retries=2, timeout=120)
        tools = ToolProvider(settings)
    async with AsyncSqliteSaver.from_conn_string(str(settings.data_dir / "checkpoints.db")) as saver:
        store = await RunStore.open(settings.data_dir / "service.db")
        compiled: dict[str, Any] = {}

        async def graph_factory() -> Any:
            if "graph" not in compiled:
                compiled["graph"] = build_graph(model, await tools.get(), settings).compile(checkpointer=saver)
            return compiled["graph"]

        async def pattern_graph_factory() -> Any:
            if "pattern" not in compiled:
                compiled["pattern"] = build_pattern_graph(model, await tools.get_all(), settings).compile(checkpointer=saver)
            return compiled["pattern"]

        manager = RunManager(graph_factory, store, settings, pattern_graph_factory)
        await manager.recover()
        try:
            yield manager
        finally:
            await manager.shutdown()
            await store.close()
            await tools.close()


def create_app(manager_context=_production_manager) -> FastAPI:
    @contextlib.asynccontextmanager
    async def lifespan(app: FastAPI):
        async with manager_context() as manager:
            app.state.manager = manager
            yield

    app = FastAPI(title="UMA investigator (LangGraph)", lifespan=lifespan)

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
