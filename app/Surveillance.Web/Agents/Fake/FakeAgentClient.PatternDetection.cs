using System.Globalization;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Web.Agents.Fake;

/// <summary>
/// Use case 2 script of the fake agent (asd-pattern-detection.md): an orchestrator, stock workers
/// for the first candidates, a broker worker that finds one new stock, a stock worker for that
/// lead at depth 1, and an ownership worker for the cluster. Workers interleave, as real workers
/// running side by side would. The run stops at approval of the report.
/// </summary>
public sealed partial class FakeAgentClient
{
    public const string Orchestrator = "orchestrator";
    private const string SubmitReport = "submit_report";
    private const string Broker = "YP";
    private const string SpreadBroker = "CC";
    private const string LeadSymbol = "BRMS";

    private static IEnumerable<Step> PatternSteps(FakeRun run)
    {
        var job = run.Request.Job!;
        var a = job.Candidates[0].Symbol;
        var b = job.Candidates.Count > 1 ? job.Candidates[1].Symbol : null;
        string Stock(string s) => $"saham-{s}";

        yield return new TextStep($"Menerima {job.Candidates.Count} kandidat dari penyaringan awal untuk {Period(job)}. Membagi tugas ke worker saham.");

        yield return new WorkerStartStep(new WorkerStartedPayload(Stock(a), WorkerRole.Stock, a, 0, job.Candidates[0].Reason));
        yield return new ToolStep("idx_history", new { symbol = a, period = "1mo" }, $"10 sesi terakhir: harga {a} bergerak {Pct(job.Candidates[0].PriceChangePct)}.");
        if (b is not null)
        {
            yield return new WorkerStartStep(new WorkerStartedPayload(Stock(b), WorkerRole.Stock, b, 0, job.Candidates[1].Reason));
            yield return new ToolStep("idx_history", new { symbol = b, period = "1mo" }, $"10 sesi terakhir: harga {b} bergerak {Pct(job.Candidates[1].PriceChangePct)}.");
        }

        yield return new AgentStep(Stock(a));
        yield return new ToolStep("idx_broker", new { symbols = a, period = "1w" }, $"Tiga pembeli teratas {Broker}, {SpreadBroker}, AK menguasai 52% nilai beli bersih.");
        if (b is not null)
        {
            yield return new AgentStep(Stock(b));
            yield return new ToolStep("idx_broker", new { symbols = b, period = "1w" }, $"{Broker} pembeli teratas dengan 31% nilai beli bersih; tiga teratas 47%.");
        }

        yield return new WorkerFinishStep(new WorkerFinishedPayload(Stock(a), WorkerRole.Stock, WorkerStatus.Ok,
            $"Konsentrasi beli di atas ambang 40%. Broker dominan: {Broker}, {SpreadBroker}.",
            [new Lead(LeadKind.Broker, Broker, $"Pembeli teratas di {a}."), new Lead(LeadKind.Broker, SpreadBroker, $"Pembeli kedua di {a}.")]));
        if (b is not null)
        {
            yield return new WorkerFinishStep(new WorkerFinishedPayload(Stock(b), WorkerRole.Stock, WorkerStatus.Ok,
                $"Konsentrasi beli 47%, di atas ambang. Broker dominan: {Broker}.",
                [new Lead(LeadKind.Broker, Broker, $"Pembeli teratas di {b}.")]));
        }

        yield return new AgentStep(Orchestrator);
        yield return new TextStep($"Broker {Broker} dominan di lebih dari satu kandidat, jadi jejaknya diikuti. Broker {SpreadBroker} aktif di sebagian besar saham kandidat dan tidak diikuti.");

        var brokerWorker = $"broker-{Broker}";
        yield return new WorkerStartStep(new WorkerStartedPayload(brokerWorker, WorkerRole.Broker, Broker, 1, $"Dominan di {a}{(b is null ? "" : " dan " + b)}."));
        yield return new ToolStep("idx_screen", new { preset = "high-volume", limit = 20 }, "20 saham teramai hari ini diambil sebagai daftar periksa.");
        yield return new ToolStep("idx_broker", new { symbols = LeadSymbol, period = "1w" }, $"{Broker} pembeli teratas di {LeadSymbol} dengan 38% nilai beli bersih.");
        yield return new ToolStep("idx_market_brokers", new { top = 20 }, $"{Broker} masuk 10 besar nilai transaksi pasar; aktivitasnya tidak menyebar merata.");
        yield return new WorkerFinishStep(new WorkerFinishedPayload(brokerWorker, WorkerRole.Broker, WorkerStatus.Ok,
            $"Pola beli {Broker} serupa ditemukan di {LeadSymbol}.",
            [new Lead(LeadKind.Stock, LeadSymbol, $"{Broker} pembeli teratas dengan 38% nilai beli bersih.")]));

        yield return new AgentStep(Orchestrator);
        yield return new TextStep($"Mengikuti jejak baru {LeadSymbol} pada kedalaman 1.");
        yield return new WorkerStartStep(new WorkerStartedPayload(Stock(LeadSymbol), WorkerRole.Stock, LeadSymbol, 1, $"Jejak dari broker {Broker}."));
        yield return new ToolStep("idx_history", new { symbol = LeadSymbol, period = "1mo" }, $"Harga {LeadSymbol} naik 18% dalam 10 sesi, volume 2,3x rata-rata.");
        yield return new ToolStep("idx_news", new { symbol = LeadSymbol, company_only = true }, "Tidak ada berita material yang menjelaskan kenaikan.");
        yield return new WorkerFinishStep(new WorkerFinishedPayload(Stock(LeadSymbol), WorkerRole.Stock, WorkerStatus.Ok,
            $"Kenaikan harga dan konsentrasi beli {Broker} tanpa berita material."));

        var cluster = b is null ? $"{a}, {LeadSymbol}" : $"{a}, {b}, {LeadSymbol}";
        yield return new AgentStep(Orchestrator);
        yield return new TextStep($"Klaster terbentuk: {cluster} dengan broker dominan {Broker}. Memeriksa kepemilikan.");
        yield return new WorkerStartStep(new WorkerStartedPayload("kepemilikan-1", WorkerRole.Ownership, cluster, 1, "Klaster dengan broker dominan yang sama."));
        yield return new ToolStep("idx_ownership_ticker", new { symbol = a }, $"Pemegang di atas 1% untuk {a}: tiga entitas korporasi.");
        yield return new ToolStep("idx_ownership_graph", new { root = a, depth = 2 }, $"Tidak ada pemegang di atas 1% yang juga memegang {LeadSymbol}.");
        yield return new WorkerFinishStep(new WorkerFinishedPayload("kepemilikan-1", WorkerRole.Ownership, WorkerStatus.Ok,
            "Tidak ditemukan pemegang saham yang sama atau terafiliasi di atas 1%."));

        yield return new AgentStep(Orchestrator);
        yield return new TextStep("Menyusun laporan untuk ditelaah analis.");
        yield return new ReportApprovalStep();
    }

    private static IEnumerable<Step> PatternResumeSteps(FakeRun run)
    {
        var decision = run.Decision!;
        switch (decision.Decision)
        {
            case Decision.Approve:
                yield return new FinishSubmitStep("Laporan disetujui analis.");
                yield return new DoneStep(RunOutcome.Approved);
                break;

            case Decision.Reject:
                yield return new FinishSubmitStep("Laporan ditolak analis.");
                yield return new DoneStep(RunOutcome.Rejected);
                break;

            case Decision.Revise when run.Revision >= MaxRevisions:
                yield return new FinishSubmitStep("Revisi diminta, tetapi batas revisi sudah tercapai.");
                yield return new ErrorStep(AgentErrorCode.RevisionLimitExceeded,
                    $"Laporan sudah direvisi {MaxRevisions} kali. Setujui atau tolak draf terakhir.", false);
                break;

            case Decision.Revise:
                yield return new FinishSubmitStep("Analis meminta revisi.");
                yield return new AgentStep(Orchestrator);
                yield return new TextStep($"Memperbaiki laporan sesuai komentar analis: \"{decision.Comment}\".");
                yield return new ReportApprovalStep();
                break;
        }
    }

    private static PatternReport BuildReport(FakeRun run)
    {
        var job = run.Request.Job!;
        var a = job.Candidates[0].Symbol;
        var b = job.Candidates.Count > 1 ? job.Candidates[1].Symbol : null;
        string Ref(string agent, string tool) => run.ToolCallIds.GetValueOrDefault(agent + "/" + tool, "call_1");
        Evidence Ev(string agent, string tool, string detail) => new(Ref(agent, tool), tool, detail);

        var stocks = new List<string> { a };
        if (b is not null) stocks.Add(b);
        stocks.Add(LeadSymbol);

        var findings = new List<Statement>
        {
            new($"{Broker} menjadi pembeli teratas di {string.Join(", ", stocks)} dalam jendela pengamatan yang sama.",
            [
                Ev($"saham-{a}", "idx_broker", $"{Broker} pembeli teratas di {a}"),
                Ev($"broker-{Broker}", "idx_broker", $"{Broker} 38% nilai beli bersih di {LeadSymbol}"),
            ]),
            new($"Harga {a} bergerak {Pct(job.Candidates[0].PriceChangePct)} dalam 10 sesi, bersamaan dengan konsentrasi beli di atas 40%.",
            [Ev($"saham-{a}", "idx_history", "Perubahan harga 10 sesi"), Ev($"saham-{a}", "idx_broker", "Tiga pembeli teratas 52%")]),
        };

        var revised = run.Revision > 0;
        return new PatternReport(
            Summary: (revised ? $"Revisi ke-{run.Revision}. " : "") +
                     (run.LimitsReached.Count > 0 ? "Laporan parsial karena batas job tercapai. " : "") +
                     $"Satu klaster dengan {stocks.Count} saham menunjukkan pola beli terkonsentrasi oleh broker {Broker}. " +
                     "Tidak ditemukan keterkaitan kepemilikan di atas 1%. Pola ini perlu ditelaah lebih lanjut.",
            PeriodStart: job.WindowStart,
            PeriodEnd: job.WindowEnd,
            Counts: new ReportCounts(1, stocks.Count, 1),
            Clusters:
            [
                new PatternCluster($"Klaster {Broker}", stocks, [Broker],
                    $"{Broker} mulai menjadi pembeli teratas di {a} pada awal jendela, lalu di {LeadSymbol} menjelang akhir jendela. Belum terlihat fase distribusi.",
                    findings, "Tidak ditemukan pemegang saham yang sama atau terafiliasi di atas 1%."),
            ],
            AlternativeExplanations:
            [
                new($"Tidak ada berita material untuk {LeadSymbol} yang menjelaskan kenaikan.", [Ev($"saham-{LeadSymbol}", "idx_news", "Berita emiten")]),
            ],
            LeadsNotFollowed: [new Lead(LeadKind.Broker, SpreadBroker, "Aktif di lebih dari 30% saham kandidat dan porsi beli bersihnya di bawah ambang."), .. run.NotFollowed],
            LimitsReached: [.. run.LimitsReached],
            DataLimitations:
            [
                "Data bandar dari IPOT tidak tersedia (HTTP 502); konsentrasi broker dihitung dari broker summary Stockbit.",
                "Harga tertunda dan kepemilikan hanya untuk pemegang di atas 1% dari register bulanan.",
            ],
            FollowUps: [$"Pantau transaksi {Broker} di ketiga saham pada sesi berikutnya.", $"Minta data transaksi rinci {Broker} untuk periode jendela."]);
    }

    /// <summary>Only pattern runs have a job budget; the UMA script keeps its own step limit.</summary>
    private static bool BudgetExhausted(FakeRun run) =>
        run.Request.Job is { } job && run.ToolCallsUsed >= job.Limits.MaxToolCalls;

    private static string BudgetReason(FakeRun run) => $"Batas {run.Request.Job!.Limits.MaxToolCalls} panggilan data per job tercapai.";

    private static void NoteLimit(FakeRun run, string reason)
    {
        if (!run.LimitsReached.Contains(reason))
        {
            run.LimitsReached.Add(reason);
        }
    }

    private static void SkipWorker(FakeRun run, WorkerStartedPayload worker)
    {
        run.SkippedWorkers.Add(worker.WorkerId);
        NoteLimit(run, BudgetReason(run));
        var (kind, target) = worker.Role switch
        {
            WorkerRole.Broker => (LeadKind.Broker, worker.Target),
            WorkerRole.Ownership => (LeadKind.Stock, worker.Target.Split(',')[0].Trim()),
            _ => (LeadKind.Stock, worker.Target),
        };
        run.NotFollowed.Add(new Lead(kind, target, $"Tidak dijalankan: {BudgetReason(run)}"));
    }

    private static string Period(PatternJob job) =>
        $"{job.WindowStart.ToString("d MMMM", Id)} sampai {job.WindowEnd.ToString("d MMMM yyyy", Id)}";

    private static string Pct(double pct) => (pct >= 0 ? "naik " : "turun ") + Math.Abs(pct).ToString("0.#", Id) + "%";

    private sealed record AgentStep(string Name) : Step;
    private sealed record WorkerStartStep(WorkerStartedPayload Payload) : Step;
    private sealed record WorkerFinishStep(WorkerFinishedPayload Payload) : Step;
    private sealed record ReportApprovalStep : Step;
}
