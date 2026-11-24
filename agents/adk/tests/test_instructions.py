"""The three solutions must use the same system instructions as the ASD."""

import re

from app import instructions
from app.summarizer import summarize

from .conftest import REPO


def asd_instructions() -> str:
    text = (REPO / "contracts" / "asd-investigator-uma.md").read_text(encoding="utf-8")
    section = text.split("## Instruksi sistem", 1)[1]
    return re.search(r"```text\n(.*?)\n```", section, re.S).group(1)


def test_template_matches_the_asd():
    assert instructions.TEMPLATE == asd_instructions()


def test_summaries_match_the_maf_adapter():
    # Same inputs and expected strings as ToolResultSummarizerTests.cs.
    quote = '{"$type":"text","text":"{\\"schema\\":\\"idx.quotes.v1\\",\\"data\\":[{\\"price\\":5800.0,\\"change_pct\\":2.2026,\\"volume\\":235635300,\\"avg_volume_3m\\":212048028}]}"}'
    assert summarize("c1", "idx_quote", quote)["summary"] == "Harga 5.800 (+2,2%), volume 235.635.300 lembar, 1,1x rata-rata 3 bulan."
    broker = '{"schema":"idx.broker.v1","data":[{"buyer_count":53,"seller_count":70,"source":"stockbit","buyers_complete":true}]}'
    assert summarize("c1", "idx_broker", broker)["summary"] == "53 pembeli dan 70 penjual (stockbit, daftar lengkap)."
    status = '{"logged_in":true}'
    assert summarize("c1", "idx_stockbit_status", status)["summary"] == "Sesi Stockbit aktif."
