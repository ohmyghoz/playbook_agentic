"""Scripted model and canned idx tools for the agent contract tests (AGENT_SCRIPT=<scenarios.json>).

Only used when AGENT_SCRIPT is set. The scenario comes from 'scenario=<name>' in the analyst
notes (part of the system instructions); the turn is the first one whose tool call ids are not
yet in the conversation, so the model is stateless and survives a restart. The same file drives
the MAF and LangGraph contract tests (app/Surveillance.Web.Tests/Contract/scenarios.json).
"""

import asyncio
import json
import re
from pathlib import Path
from typing import Any

from google.adk.models import BaseLlm, LlmResponse
from google.adk.tools import BaseTool
from google.genai import types


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


class ScriptedLlm(BaseLlm):
    model: str = "scripted"
    script: Any = None

    async def generate_content_async(self, llm_request, stream: bool = False):
        system = str(llm_request.config.system_instruction or "") if llm_request.config else ""
        seen = {p.function_call.id for c in llm_request.contents for p in (c.parts or [])
                if p.function_call and p.function_call.id}
        items, delay = self.script.next_turn(system, seen)
        if delay:
            await asyncio.sleep(delay)
        parts = [types.Part(text=i["text"]) if "text" in i else
                 types.Part(function_call=types.FunctionCall(id=i["call"]["id"], name=i["call"]["name"], args=i["call"]["args"]))
                 for i in items]
        yield LlmResponse(content=types.Content(role="model", parts=parts))


class CannedTool(BaseTool):
    """An idx MCP tool stand-in returning a CallToolResult-shaped dict, as McpToolset does."""

    def __init__(self, name: str, text: str, is_error: bool = False):
        super().__init__(name=name, description="canned idx tool")
        self._result = {"content": [{"type": "text", "text": text}], "isError": is_error}

    def _get_declaration(self) -> types.FunctionDeclaration:
        return types.FunctionDeclaration(name=self.name, description=self.description,
                                         parameters_json_schema={"type": "object", "properties": {}})

    async def run_async(self, *, args: dict[str, Any], tool_context) -> Any:
        return self._result


def canned_tools(script: Script) -> list[BaseTool]:
    return [CannedTool(name, spec["text"], spec.get("isError", False)) for name, spec in script.tools.items()]
