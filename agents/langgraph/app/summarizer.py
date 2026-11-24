"""Short Indonesian summaries of idx tool results for tool_call_finished.

Mirrors agents/maf/ToolResultSummarizer.cs; agents/langgraph and agents/adk keep identical copies.
"""

import json
from typing import Any

from .instructions import SUBMIT_MEMO

LIMIT_MARKER = "[batas-panggilan-tool]"


def result_text(content: Any) -> str:
    """Flattens ToolMessage content (str or content blocks) to text."""
    if content is None:
        return ""
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts = []
        for block in content:
            if isinstance(block, dict):
                parts.append(str(block.get("text", "")))
            else:
                parts.append(str(block))
        return "".join(parts)
    return json.dumps(content, ensure_ascii=False, default=str)


def summarize(call_id: str, tool_name: str, text: str, is_error: bool = False) -> dict[str, Any]:
    """Returns a tool_call_finished payload."""
    from .contract import tool_finished

    if text.startswith(LIMIT_MARKER):
        return tool_finished(call_id, tool_name, False, "Tidak dijalankan: batas panggilan data tercapai.", "step_limit")
    if tool_name == SUBMIT_MEMO:
        return tool_finished(call_id, tool_name, True, "Memo disetujui analis.")

    try:
        envelope = _find_envelope(text)
    except (ValueError, TypeError):
        envelope = None

    if envelope is None:
        if is_error:
            return tool_finished(call_id, tool_name, False, f"Data {tool_name} tidak tersedia (tool_error).",
                                 "tool_error", text[:500])
        return tool_finished(call_id, tool_name, True, "Data diterima.")

    if "error" in envelope:
        err = envelope["error"] or {}
        if isinstance(err, str):  # ADK: {"error": "MCP tool execution failed: ..."}
            return tool_finished(call_id, tool_name, False, f"Data {tool_name} tidak tersedia (tool_error).", "tool_error", err)
        code = str(err.get("code") or "error")
        return tool_finished(call_id, tool_name, False, _failure_text(tool_name, code), code, str(err.get("message", "")))

    try:
        return tool_finished(call_id, tool_name, True, _describe(tool_name, envelope["data"]))
    except (KeyError, IndexError, TypeError, ValueError):
        return tool_finished(call_id, tool_name, True, "Data diterima.")


def _find_envelope(text: str) -> dict[str, Any] | None:
    start = text.find("{")
    if start < 0:
        return None
    root = json.loads(text[start:])
    if not isinstance(root, dict):
        return None
    if "schema" in root or "error" in root:
        return root
    if isinstance(root.get("text"), str):  # a single MCP text content block
        return _find_envelope(root["text"])
    if "logged_in" in root:  # idx_stockbit_status answers without an envelope
        return {"schema": "idx.stockbit.status", "data": root}
    if isinstance(root.get("content"), list):  # MCP CallToolResult
        for item in root["content"]:
            if isinstance(item, dict) and isinstance(item.get("text"), str):
                return _find_envelope(item["text"])
    return None


def _failure_text(tool_name: str, code: str) -> str:
    return {
        "blocked": "Sumber data menolak permintaan sementara. Data tidak tersedia.",
        "not_found": "Kode saham tidak dikenal sumber data.",
    }.get(code, f"Data {tool_name} tidak tersedia ({code}).")


def _describe(tool_name: str, data: Any) -> str:
    if tool_name == "idx_stockbit_status":
        return "Sesi Stockbit aktif." if data.get("logged_in") is True else "Sesi Stockbit tidak aktif; tool Stockbit dilewati."
    if tool_name == "idx_quote":
        q = data[0]
        text = f"Harga {_num(q.get('price'))} ({_signed(q.get('change_pct'))}%), volume {_num(q.get('volume'))} lembar"
        if q.get("volume") and q.get("avg_volume_3m"):
            text += f", {_dec(q['volume'] / q['avg_volume_3m'], 1)}x rata-rata 3 bulan"
        return text + "."
    if tool_name == "idx_history":
        s = data["summary"]
        return (f"{int(s.get('candles') or 0)} sesi. Rata-rata volume {_num(s.get('avg_volume'))} lembar, "
                f"rentang harga {_num(s.get('low'))} hingga {_num(s.get('high'))}.")
    if tool_name == "idx_news":
        return f"{int(data.get('company_matches') or 0)} berita menyebut emiten dari {len(data.get('items') or [])} berita yang dikirim."
    if tool_name in ("idx_stockbit_actions", "idx_corporate_actions"):
        actions = data.get("actions") or []
        if not actions:
            return "Tidak ada aksi korporasi."
        first = actions[0]
        return f"{len(actions)} aksi korporasi. Terbaru: {first.get('kind', '-')} pada {first.get('date') or first.get('cum_date') or '-'}."
    if tool_name == "idx_foreign":
        t = data["totals"]
        net = t.get("foreign_net") or 0
        direction = "net buy" if net >= 0 else "net sell"
        return f"Asing {direction} {_num(abs(net))} lembar dalam {int(t.get('sessions') or 0)} sesi, streak {int(t.get('streak') or 0)}."
    if tool_name == "idx_broker":
        b = data[0]
        complete = "daftar lengkap" if b.get("buyers_complete") is True else "10 teratas"
        return f"{int(b.get('buyer_count') or 0)} pembeli dan {int(b.get('seller_count') or 0)} penjual ({b.get('source', '-')}, {complete})."
    if tool_name == "idx_bandar":
        return f"Sinyal {data[0].get('signal', '-')}, fase {data[0].get('phase', '-')}."
    if tool_name == "idx_stockbit_movements":
        return f"{len(data.get('movements') or [])} transaksi insider atau pemegang besar."
    return "Data diterima."


def _num(value: Any) -> str:
    """Indonesian thousands separator, no decimals: 235.635.300."""
    if not isinstance(value, (int, float)):
        return "-"
    return f"{value:,.0f}".replace(",", ".")


def _dec(value: float, places: int) -> str:
    return f"{value:.{places}f}".replace(".", ",")


def _signed(value: Any) -> str:
    if not isinstance(value, (int, float)):
        return "-"
    text = f"{value:.2f}".rstrip("0").rstrip(".").replace(".", ",")
    return ("+" if value >= 0 else "") + text
