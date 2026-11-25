"""The orchestrator of asd-pattern-detection.md ("Protokol worker dan orchestrator") as code.

Mirrors agents/maf/PatternOrchestrator.cs and PatternInstructions.cs; agents/langgraph and
agents/adk keep identical copies. The workers are model agents supplied by the caller, so the
same delegation rules and limits run on every framework.
"""

import asyncio
import re
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from typing import Any

from pydantic import ValidationError

from .contract import DISCLAIMER, PatternJob, PatternReport, WorkerReport

ORCHESTRATOR = "orchestrator"
FINISH_WORKER = "finish_worker"
SUBMIT_REPORT = "submit_report"
MAX_PARALLEL_WORKERS = 3
MAX_INVALID_REPORTS = 3

ROLE_TOOLS = {
    "stock": {"idx_history", "idx_technical", "idx_broker", "idx_bandar", "idx_foreign", "idx_news", "idx_stockbit_actions"},
    "broker": {"idx_broker", "idx_market_brokers", "idx_members", "idx_screen"},
    "ownership": {"idx_ownership_ticker", "idx_ownership_graph", "idx_ownership_entity", "idx_ownership_changes"},
}
ROLE_NAMES = {"stock": "saham", "broker": "broker", "ownership": "kepemilikan"}

WORKER_TEMPLATE = """Anda worker {role} dalam deteksi pola akumulasi-distribusi di Bursa Efek Indonesia. Target Anda: {target}. Jendela pengamatan {windowStart} sampai {windowEnd}.
Gunakan hanya tool yang tersedia, paling banyak {maxSteps} panggilan. Setiap hasil tool diawali [toolCallId: ...]; rujuk id itu sebagai bukti.
Worker saham: periksa pergerakan harga dan volume dalam jendela, lalu broker summary (idx_broker) untuk porsi nilai beli bersih broker teratas; laporkan broker dominan di dominantBrokers dengan kode broker dua huruf (misalnya YP), bukan nama.
Worker broker: cari saham lain tempat broker target menjadi pembeli teratas dengan pola serupa; laporkan sebagai leads (kind stock, target kode saham empat huruf).
Worker kepemilikan: periksa apakah saham-saham target punya pemegang saham yang sama atau terafiliasi di atas 1%.
Bila tool gagal, catat di summary dan lanjutkan. Jangan menyimpulkan manipulasi atau pelanggaran; tulis sebagai indikasi.
Akhiri dengan memanggil finish_worker tepat satu kali."""

REPORT_TEMPLATE = """Anda penyusun laporan deteksi pola akumulasi-distribusi untuk analis pengawasan pasar modal. Anda menerima hasil worker dalam JSON: klaster, temuan, jejak yang tidak diikuti, dan batas yang tercapai.
Susun PatternReport lalu panggil submit_report. Setiap pernyataan di findings dan alternativeExplanations harus merujuk toolCallId dari temuan worker. Jangan menambah fakta yang tidak ada di hasil worker.
Gunakan bahasa indikasi, bukan kesimpulan pelanggaran. disclaimer selalu "Indikasi untuk ditelaah analis, bukan kesimpulan pelanggaran."
Bila analis meminta revisi, perbaiki laporan sesuai komentarnya lalu panggil submit_report lagi."""

BROKER_CODE = re.compile(r"[A-Z0-9]{2}")
STOCK_CODE = re.compile(r"[A-Z]{4}")

WORKER_CONTINUE = "Panggil finish_worker sekarang dengan temuan yang sudah ada."
REPORT_CONTINUE = "Lanjutkan: panggil submit_report dengan laporan lengkap."


def worker_instructions(role: str, target: str, job: PatternJob) -> str:
    return (WORKER_TEMPLATE.replace("{role}", ROLE_NAMES[role]).replace("{target}", target)
            .replace("{windowStart}", job.window_start.isoformat()).replace("{windowEnd}", job.window_end.isoformat())
            .replace("{maxSteps}", str(job.limits.max_steps_per_worker)))


class JobBudget:
    """Tool calls and time shared by all workers of one job; enforced in code, not in the prompt."""

    def __init__(self, max_tool_calls: int, max_minutes: int, used: int = 0):
        self.max_tool_calls = max_tool_calls
        self.deadline = time.monotonic() + max_minutes * 60
        self.used = used

    @property
    def exhausted(self) -> bool:
        return self.used >= self.max_tool_calls or time.monotonic() >= self.deadline

    def try_use(self) -> bool:
        if self.exhausted:
            return False
        self.used += 1
        return True

    @property
    def reason(self) -> str:
        if time.monotonic() >= self.deadline:
            return "Durasi maksimum job tercapai."
        return f"Batas {self.max_tool_calls} panggilan data per job tercapai."


@dataclass
class WorkerOutcome:
    worker: dict[str, Any]  # the worker_started payload
    status: str  # ok | error | cancelled | limit_reached
    report: WorkerReport | None

    def dump(self) -> dict[str, Any]:
        return {"worker": self.worker, "status": self.status, "report": self.report.dump() if self.report else None}


@dataclass
class PatternFacts:
    window_start: str
    window_end: str
    clusters: list[dict[str, Any]]
    workers: list[WorkerOutcome]
    leads_not_followed: list[dict[str, Any]] = field(default_factory=list)
    limits_reached: list[str] = field(default_factory=list)

    def dump(self) -> dict[str, Any]:
        return {"windowStart": self.window_start, "windowEnd": self.window_end, "clusters": self.clusters,
                "workers": [w.dump() for w in self.workers], "leadsNotFollowed": self.leads_not_followed,
                "limitsReached": self.limits_reached}


RunWorker = Callable[[dict[str, Any]], Awaitable[WorkerOutcome]]


def worker_payload(worker_id: str, role: str, target: str, depth: int, reason: str | None) -> dict[str, Any]:
    payload = {"workerId": worker_id, "role": role, "target": target, "depth": depth}
    if reason:
        payload["reason"] = reason
    return payload


class PatternOrchestrator:
    def __init__(self, job: PatternJob, budget: JobBudget, run_worker: RunWorker, narrate: Callable[[str], Awaitable[None]]):
        self.job, self.budget, self.run_worker, self.narrate = job, budget, run_worker, narrate
        self.limits = job.limits
        self.outcomes: list[WorkerOutcome] = []
        self.not_followed: list[dict[str, Any]] = []
        self.limits_reached: list[str] = []
        self.stocks: set[str] = set()
        self.brokers: set[str] = set()

    async def run(self) -> PatternFacts:
        job = self.job
        await self.narrate(f"Menerima {len(job.candidates)} kandidat dari penyaringan awal untuk "
                           f"{job.window_start.isoformat()} sampai {job.window_end.isoformat()}. ")
        stocks = []
        for c in job.candidates:
            ok, reason = self._take_stock(c.symbol)
            if ok:
                stocks.append(worker_payload(f"saham-{c.symbol}", "stock", c.symbol, 0, c.reason))
            else:
                self.not_followed.append({"kind": "stock", "target": c.symbol, "reason": reason})

        while stocks:
            await self.narrate(f"Menjalankan {len(stocks)} worker saham: {', '.join(s['target'] for s in stocks)}. ")
            stock_outcomes = await self._run_wave(stocks)
            brokers = self._brokers_to_follow(stock_outcomes)
            if not brokers:
                break
            await self.narrate(f"Mengikuti broker {', '.join(b['target'] for b in brokers)}. ")
            broker_outcomes = await self._run_wave(brokers)
            stocks = self._stock_leads(broker_outcomes)
            if stocks:
                await self.narrate(f"Jejak baru dari worker broker: {', '.join(s['target'] for s in stocks)}. ")

        clusters = self._clusters()
        ownership = [worker_payload(f"kepemilikan-{i + 1}", "ownership", ", ".join(c["stocks"]), 1,
                                    f"Klaster dengan broker dominan {', '.join(c['dominantBrokers'])}.")
                     for i, c in enumerate(clusters)]
        if ownership:
            await self.narrate(f"Memeriksa kepemilikan {len(ownership)} klaster. ")
            owned = await self._run_wave(ownership)
            by_id = {o.worker["workerId"]: o for o in owned}
            for i, c in enumerate(clusters):
                o = by_id.get(f"kepemilikan-{i + 1}")
                c["ownershipSummary"] = o.report.summary if o and o.report else None

        await self.narrate("Menyusun laporan untuk ditelaah analis. ")
        return PatternFacts(job.window_start.isoformat(), job.window_end.isoformat(), clusters, self.outcomes,
                            self.not_followed, self.limits_reached)

    async def _run_wave(self, workers: list[dict[str, Any]]) -> list[WorkerOutcome]:
        gate = asyncio.Semaphore(MAX_PARALLEL_WORKERS)

        async def one(w: dict[str, Any]) -> WorkerOutcome | None:
            async with gate:
                if self.budget.exhausted:
                    self._note(self.budget.reason)
                    target = w["target"].split(",")[0].strip() if w["role"] == "ownership" else w["target"]
                    self.not_followed.append({"kind": "broker" if w["role"] == "broker" else "stock", "target": target,
                                              "reason": f"Tidak dijalankan: {self.budget.reason}"})
                    return None
                return await self.run_worker(w)

        outcomes = [o for o in await asyncio.gather(*(one(w) for w in workers)) if o is not None]
        self.outcomes.extend(outcomes)
        for o in outcomes:
            if o.status == "limit_reached":
                self._note(self.budget.reason if self.budget.exhausted
                           else f"Batas {self.limits.max_steps_per_worker} langkah worker tercapai ({o.worker['workerId']}).")
        return outcomes

    def _brokers_to_follow(self, stock_outcomes: list[WorkerOutcome]) -> list[dict[str, Any]]:
        follow: list[dict[str, Any]] = []
        for o in stock_outcomes:
            shares = o.report.dominant_brokers if o.report else []
            top = sorted(shares, key=lambda b: b.net_buy_share_pct, reverse=True)[: self.limits.brokers_per_stock]
            if not top or sum(b.net_buy_share_pct for b in top) < self.limits.min_top_buyer_share_pct:
                continue  # concentration below the screening threshold: nothing to follow
            for b in top:
                code = b.broker.strip().upper()
                if code in self.brokers or any(f["target"] == code for f in follow):
                    continue
                spread = self._spread_pct(code)
                if spread > self.limits.max_broker_spread_pct and b.net_buy_share_pct < self.limits.min_top_buyer_share_pct:
                    self.not_followed.append({"kind": "broker", "target": code, "reason":
                                              f"Dominan di {spread:.0f}% saham kandidat (di atas {self.limits.max_broker_spread_pct:.0f}%) "
                                              "dengan porsi beli bersih di bawah ambang."})
                    self.brokers.add(code)
                    continue
                depth = o.worker["depth"] + 1
                if depth > self.limits.max_depth:
                    self.not_followed.append({"kind": "broker", "target": code, "reason": f"Batas kedalaman {self.limits.max_depth} tercapai."})
                    self._note(f"Batas kedalaman {self.limits.max_depth} tercapai.")
                    continue
                self.brokers.add(code)
                follow.append(worker_payload(f"broker-{code}", "broker", code, depth,
                                             f"Pembeli dominan di {o.worker['target']} ({b.net_buy_share_pct:.1f}% beli bersih)."))
        return follow

    def _stock_leads(self, broker_outcomes: list[WorkerOutcome]) -> list[dict[str, Any]]:
        nxt: list[dict[str, Any]] = []
        for o in broker_outcomes:
            for lead in (o.report.leads if o.report else []):
                if lead.kind != "stock":
                    continue
                symbol = lead.target.strip().upper()
                if symbol in self.stocks:
                    continue
                ok, reason = self._take_stock(symbol)
                if ok:
                    nxt.append(worker_payload(f"saham-{symbol}", "stock", symbol, o.worker["depth"],
                                              f"Jejak dari broker {o.worker['target']}: {lead.reason}"))
                else:
                    self.not_followed.append({"kind": "stock", "target": symbol, "reason": reason})
        return nxt

    def _clusters(self) -> list[dict[str, Any]]:
        by_broker: dict[str, set[str]] = {}
        for o in self.outcomes:
            if o.worker["role"] == "stock" and o.report:
                for b in sorted(o.report.dominant_brokers, key=lambda x: x.net_buy_share_pct, reverse=True)[: self.limits.brokers_per_stock]:
                    by_broker.setdefault(b.broker.strip().upper(), set()).add(o.worker["target"])
            elif o.worker["role"] == "broker" and o.report:
                for lead in o.report.leads:
                    if lead.kind == "stock" and lead.target.strip().upper() in self.stocks:
                        by_broker.setdefault(o.worker["target"], set()).add(lead.target.strip().upper())
        skipped = {n["target"] for n in self.not_followed if n["kind"] == "broker"}
        return [{"name": f"Klaster {broker}", "stocks": sorted(stocks), "dominantBrokers": [broker], "ownershipSummary": None}
                for broker, stocks in by_broker.items()
                if len(stocks) >= 2 and broker in self.brokers and broker not in skipped]

    def _spread_pct(self, broker: str) -> float:
        candidates = {c.symbol for c in self.job.candidates}
        dominant_in = sum(1 for o in self.outcomes
                          if o.worker["role"] == "stock" and o.worker["target"] in candidates and o.report
                          and any(b.broker.strip().upper() == broker
                                  for b in o.report.dominant_brokers[: self.limits.brokers_per_stock]))
        return dominant_in * 100.0 / len(candidates) if candidates else 0

    def _take_stock(self, symbol: str) -> tuple[bool, str]:
        if len(self.stocks) >= self.limits.max_stocks:
            reason = f"Batas {self.limits.max_stocks} saham per job tercapai."
            self._note(reason)
            return False, reason
        self.stocks.add(symbol)
        return True, ""

    def _note(self, text: str) -> None:
        if text not in self.limits_reached:
            self.limits_reached.append(text)


def clean_worker_report(raw: dict[str, Any]) -> WorkerReport | None:
    """finish_worker arguments as a WorkerReport; incomplete leads and brokers are dropped."""
    try:
        report = WorkerReport.model_validate(raw)
    except ValidationError:
        summary = str(raw.get("summary") or "").strip()
        if not summary:
            return None
        report = WorkerReport(summary=summary)
    # Models sometimes give broker names or loose targets; only exchange codes reach the orchestrator.
    report.dominant_brokers = [b.model_copy(update={"broker": b.broker.strip().upper()}) for b in report.dominant_brokers
                               if BROKER_CODE.fullmatch(b.broker.strip().upper())]
    report.leads = [lead.model_copy(update={"target": lead.target.strip().upper()}) for lead in report.leads
                    if (STOCK_CODE if lead.kind == "stock" else BROKER_CODE).fullmatch(lead.target.strip().upper())]
    return report


def validate_report(args: dict[str, Any], facts: dict[str, Any], known_call_ids: set[str]) -> tuple[dict[str, Any] | None, str | None]:
    """The report as wire JSON, or a reason the model can act on. Mirrors PatternRunner.ValidateReport in MAF."""
    raw = args.get("report")
    if raw is None:
        return None, "Argumen 'report' tidak ada."
    try:
        report = PatternReport.model_validate(raw)
    except ValidationError as ex:
        details = "; ".join(f"{'.'.join(str(p) for p in e['loc'])}: {e['msg']}" for e in ex.errors()[:8])
        return None, f"Laporan belum sesuai skema: {details}."

    cited = [ev.tool_call_id for s in [*(f for c in report.clusters for f in c.findings), *report.alternative_explanations]
             for ev in s.evidence if ev.tool_call_id not in known_call_ids]
    if cited:
        return None, (f"toolCallId berikut tidak berasal dari worker: {', '.join(sorted(set(cited)))}. "
                      "Rujuk hanya toolCallId dari hasil worker.")

    def key(stocks: list[str]) -> str:
        return ",".join(sorted(s.strip().upper() for s in stocks))

    expected = {key(c["stocks"]) for c in facts["clusters"]}
    if len(report.clusters) != len(expected) or any(key(c.stocks) not in expected for c in report.clusters):
        listed = "; ".join(", ".join(c["stocks"]) for c in facts["clusters"]) or "tidak ada"
        return None, (f"clusters harus sama dengan klaster dari orchestrator ({listed}). Bila tidak ada klaster, isi clusters "
                      "dengan daftar kosong dan tulis pengamatan per saham di summary dan alternativeExplanations.")

    # The orchestrator's own lists are authoritative and replace the model's.
    dumped = report.dump()
    dumped.update({
        "periodStart": facts["windowStart"], "periodEnd": facts["windowEnd"],
        "counts": {"clusters": len(report.clusters),
                   "stocks": len({s for c in report.clusters for s in c.stocks}),
                   "brokers": len({b for c in report.clusters for b in c.dominant_brokers})},
        "leadsNotFollowed": facts["leadsNotFollowed"], "limitsReached": facts["limitsReached"],
        "disclaimer": DISCLAIMER,
    })
    return dumped, None
