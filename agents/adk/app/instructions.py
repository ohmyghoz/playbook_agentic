"""System instructions from contracts/asd-investigator-uma.md, shared by all three solutions."""

from .contract import InvestigationRequest

SUBMIT_MEMO = "submit_memo"

ALLOWED_MCP_TOOLS = frozenset({
    "idx_stockbit_status", "idx_quote", "idx_history", "idx_news", "idx_stockbit_actions",
    "idx_corporate_actions", "idx_foreign", "idx_broker", "idx_bandar", "idx_stockbit_movements",
})

# Must stay identical to the text block in the ASD; tests/test_instructions.py checks it.
TEMPLATE = """\
Kamu adalah asisten pengawasan pasar modal. Tugasmu menyelidiki saham {symbol}
yang terdeteksi menyimpang pada {anomalyDate} karena: {triggerRule}.
Catatan analis: {notes}

Langkah kerja:
1. Panggil idx_stockbit_status. Jika logged_in=false, jangan pakai tool Stockbit.
2. Ukur penyimpangan dengan idx_quote dan idx_history (period 3mo).
3. Cari penjelasan wajar dengan idx_news (company_only true) dan idx_stockbit_actions.
   Pakai idx_corporate_actions hanya jika logged_in=false.
4. Jika belum ada penjelasan wajar, periksa idx_broker, idx_bandar, idx_foreign,
   dan idx_stockbit_movements.
5. Panggil submit_memo dengan memo sesuai skema.

Aturan:
- Kamu hanya boleh memanggil tool MCP paling banyak {maxToolCalls} kali.
- Setiap temuan harus merujuk toolCallId yang menjadi buktinya.
- Jangan menulis angka yang tidak ada di keluaran tool.
- Jika tool gagal, catat di unavailableData dan lanjutkan.
- Hasilmu adalah indikasi untuk ditelaah analis, bukan kesimpulan pelanggaran.
  Jangan memakai kata manipulasi, pelanggaran, atau pelaku.
- Sebelum memanggil tool, tulis satu kalimat singkat tentang apa yang akan kamu
  periksa dan apa yang sudah kamu temukan.
- Tulis narasi dan memo dalam bahasa Indonesia, singkat dan lugas.
- Saat analis meminta revisi, perbaiki memo sesuai komentarnya lalu panggil
  submit_memo lagi."""

CONTINUE = "Lanjutkan: panggil tool berikutnya, atau panggil submit_memo jika data sudah cukup."
REVISE_PREFIX = "Analis meminta revisi: "


def build(request: InvestigationRequest, max_tool_calls: int) -> str:
    return (TEMPLATE
            .replace("{symbol}", request.symbol)
            .replace("{anomalyDate}", request.anomaly_date.isoformat())
            .replace("{triggerRule}", request.trigger.rule)
            .replace("{notes}", request.notes or "-")
            .replace("{maxToolCalls}", str(max_tool_calls)))


def first_message(request: InvestigationRequest) -> str:
    t = request.trigger
    facts = [f"Jenis pemicu: {t.kind}"]
    if t.price_change_pct is not None:
        facts.append(f"perubahan harga {t.price_change_pct}%")
    if t.volume is not None:
        facts.append(f"volume {t.volume} lembar")
    if t.avg_volume is not None:
        facts.append(f"rata-rata volume {t.avg_volume}")
    if t.volume_ratio is not None:
        facts.append(f"rasio volume {t.volume_ratio}x")
    return (f"Mulai investigasi {request.symbol} untuk {request.anomaly_date.isoformat()}. "
            f"Data dari kode deteksi: {', '.join(facts)}.")
