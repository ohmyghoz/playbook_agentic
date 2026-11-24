"""Service settings, read from the environment and agents/adk/.env."""

import os
from dataclasses import dataclass, field
from pathlib import Path

from dotenv import load_dotenv

SERVICE_DIR = Path(__file__).resolve().parent.parent

# .env holds GOOGLE_API_KEY and MODEL_NAME and wins over same-named machine variables; IDX_MCP_TOKEN is not in it.
load_dotenv(SERVICE_DIR / ".env", override=True)


@dataclass(frozen=True)
class Settings:
    model_name: str = field(default_factory=lambda: os.environ.get("MODEL_NAME", "gemini-3.8-flash"))
    api_key: str | None = field(default_factory=lambda: os.environ.get("GOOGLE_API_KEY"))
    mcp_endpoint: str = field(default_factory=lambda: os.environ.get("MCP_ENDPOINT", "http://127.0.0.1:8765/mcp"))
    mcp_token: str | None = field(default_factory=lambda: os.environ.get("IDX_MCP_TOKEN"))
    data_dir: Path = field(default_factory=lambda: Path(os.environ.get("ADK_DATA_DIR", SERVICE_DIR / "data")))
    app_name: str = "investigator_uma"
    # Set only by the contract tests: a scenarios file replaces the model and the MCP tools.
    script_path: str | None = field(default_factory=lambda: os.environ.get("AGENT_SCRIPT"))
    max_tool_calls: int = field(default_factory=lambda: int(os.environ.get("MAX_TOOL_CALLS", "15")))
    max_revisions: int = 2
    max_invalid_memos: int = 3
    max_text_only_turns: int = 3
