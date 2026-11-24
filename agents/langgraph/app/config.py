"""Service settings, read from the environment and agents/langgraph/.env."""

import os
from dataclasses import dataclass, field
from pathlib import Path

from dotenv import load_dotenv

SERVICE_DIR = Path(__file__).resolve().parent.parent

# .env holds the model settings and wins over same-named machine variables; IDX_MCP_TOKEN is not in it.
load_dotenv(SERVICE_DIR / ".env", override=True)


@dataclass(frozen=True)
class Settings:
    model_name: str = field(default_factory=lambda: os.environ.get("MODEL_NAME", "qwen/qwen3-30b-a3b-instruct-2507"))
    model_base_url: str = field(default_factory=lambda: os.environ.get("MODEL_BASE_URL", "https://openrouter.ai/api/v1"))
    api_key: str | None = field(default_factory=lambda: os.environ.get("OPENROUTER_API_KEY"))
    mcp_endpoint: str = field(default_factory=lambda: os.environ.get("MCP_ENDPOINT", "http://127.0.0.1:8765/mcp"))
    mcp_token: str | None = field(default_factory=lambda: os.environ.get("IDX_MCP_TOKEN"))
    data_dir: Path = field(default_factory=lambda: Path(os.environ.get("LANGGRAPH_DATA_DIR", SERVICE_DIR / "data")))
    max_tool_calls: int = 15
    max_revisions: int = 2
    max_invalid_memos: int = 3
    max_text_only_turns: int = 3
