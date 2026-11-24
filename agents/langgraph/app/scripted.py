"""Scripted model and canned idx tools for the agent contract tests (AGENT_SCRIPT=<scenarios.json>).

Only used when AGENT_SCRIPT is set. The scenario comes from 'scenario=<name>' in the analyst
notes (part of the system instructions); the turn is the first one whose tool call ids are not
yet in the conversation, so the model is stateless and survives a restart. The same file drives
the MAF and ADK contract tests (app/Surveillance.Web.Tests/Contract/scenarios.json).
"""

import asyncio
import json
import re
from pathlib import Path
from typing import Any

from langchain_core.language_models import BaseChatModel
from langchain_core.messages import AIMessage, BaseMessage, SystemMessage
from langchain_core.outputs import ChatGeneration, ChatResult
from langchain_core.tools import BaseTool
from pydantic import BaseModel, ConfigDict


class Script:
    def __init__(self, path: str | Path):
        data = json.loads(Path(path).read_text(encoding="utf-8"))
        self.tools: dict[str, dict[str, Any]] = data["tools"]
        self.memos: dict[str, Any] = data.get("memos", {})
        self.scenarios: dict[str, dict[str, Any]] = data["scenarios"]

    def next_turn(self, system_text: str, seen_call_ids: set[str]) -> tuple[list[dict[str, Any]], float]:
        match = re.search(r"scenario=(\w+)", system_text)
        scenario = self.scenarios[match.group(1) if match else "approve"]
        for turn in scenario["turns"]:
            ids = [item["call"]["id"] for item in turn if "call" in item]
            if not ids or not all(i in seen_call_ids for i in ids):
                return self._resolve(turn, 0), scenario.get("delaySeconds", 0)
        fallback = scenario.get("fallback")
        if fallback is None:
            return [{"text": "Selesai."}], 0
        n = sum(1 for i in seen_call_ids if i.startswith("q")) + 1
        return self._resolve(fallback, n), scenario.get("delaySeconds", 0)

    def _resolve(self, turn: list[dict[str, Any]], n: int) -> list[dict[str, Any]]:
        def fill(value: Any) -> Any:
            if isinstance(value, str) and value.startswith("$"):
                return self.memos[value[1:]]
            if isinstance(value, str):
                return value.replace("{n}", str(n))
            if isinstance(value, dict):
                return {k: fill(v) for k, v in value.items()}
            return value
        return [fill(item) for item in turn]


class ScriptedChatModel(BaseChatModel):
    model_config = ConfigDict(arbitrary_types_allowed=True)
    script: Any

    @property
    def _llm_type(self) -> str:
        return "scripted"

    def bind_tools(self, tools, **kwargs):
        return self

    def _turn(self, messages: list[BaseMessage]) -> tuple[AIMessage, float]:
        system = "".join(str(m.content) for m in messages if isinstance(m, SystemMessage))
        seen = {c["id"] for m in messages if isinstance(m, AIMessage) for c in m.tool_calls}
        items, delay = self.script.next_turn(system, seen)
        text = "".join(i["text"] for i in items if "text" in i)
        calls = [{"id": i["call"]["id"], "name": i["call"]["name"], "args": i["call"]["args"], "type": "tool_call"}
                 for i in items if "call" in i]
        return AIMessage(content=text, tool_calls=calls), delay

    def _generate(self, messages, stop=None, run_manager=None, **kwargs) -> ChatResult:
        message, _ = self._turn(messages)
        return ChatResult(generations=[ChatGeneration(message=message)])

    async def _agenerate(self, messages, stop=None, run_manager=None, **kwargs) -> ChatResult:
        message, delay = self._turn(messages)
        if delay:
            await asyncio.sleep(delay)
        return ChatResult(generations=[ChatGeneration(message=message)])


class _AnyArgs(BaseModel):
    model_config = ConfigDict(extra="allow")


class CannedTool(BaseTool):
    """An idx MCP tool stand-in returning the envelope from the script."""

    description: str = "canned idx tool"
    args_schema: type[BaseModel] = _AnyArgs
    result: str = ""

    def _run(self, **kwargs: Any) -> str:
        return self.result

    async def _arun(self, **kwargs: Any) -> str:
        return self.result


def canned_tools(script: Script) -> list[BaseTool]:
    return [CannedTool(name=name, result=spec["text"]) for name, spec in script.tools.items()]
