using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Web.Agents.Fake;

/// <summary>
/// Scripted agent for development without a model. It follows the UMA flow
/// from asd-investigator-uma.md with numbers taken from real BBCA output in
/// mcp-tools.md. Runs live in memory and do not survive a restart.
/// </summary>
public sealed class FakeAgentClient(IOptions<FakeAgentOptions> options) : IAgentClient
{
    public const int MaxToolCalls = 15;
    public const int MaxRevisions = 2;
    private const string SubmitMemo = "submit_memo";

    private static readonly CultureInfo Id = CultureInfo.GetCultureInfo("id-ID");
    private readonly ConcurrentDictionary<string, FakeRun> _runs = new();
    private readonly FakeAgentOptions _options = options.Value;

    public Task<string> StartAsync(InvestigationRequest request, CancellationToken ct)
    {
        var runId = $"fake-{Guid.NewGuid():N}";
        // The contract tests choose a scenario per run through the analyst notes ('scenario=<name>').
        var scenario = request.Notes switch
        {
            { } n when n.Contains("scenario=step_limit", StringComparison.Ordinal) => FakeScenario.StepLimit,
            { } n when n.Contains("scenario=model_error", StringComparison.Ordinal) => FakeScenario.ModelError,
            _ => _options.Scenario,
        };
        var slow = request.Notes?.Contains("scenario=slow", StringComparison.Ordinal) == true;
        // scenario=silent_30s: the agent says nothing for 30 seconds after it starts (thinking indicator demo).
        var silent = request.Notes?.Contains("scenario=silent_30s", StringComparison.Ordinal) == true;
        _runs[runId] = new FakeRun(runId, request, scenario)
        {
            ToolDelayMs = slow ? 3_000 : _options.ToolDelayMs,
            SilenceMs = silent ? 30_000 : 0,
        };
        return Task.FromResult(runId);
    }

    public Task ResumeAsync(string runId, ApprovalDecision decision, CancellationToken ct)
    {
        var run = GetRun(runId);
        lock (run)
        {
            if (run.Phase != RunPhase.Paused || run.ApprovalId != decision.ApprovalId)
            {
                throw new InvalidOperationException($"Run {runId} is not waiting for approval {decision.ApprovalId}.");
            }

            run.Decision = decision;
            run.Phase = RunPhase.Resumed;
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(string runId, [EnumeratorCancellation] CancellationToken ct)
    {
        var run = GetRun(runId);
        await run.Gate.WaitAsync(CancellationToken.None);
        try
        {
            if (run.Phase == RunPhase.Finished)
            {
                yield break;
            }

            if (run.Phase == RunPhase.Paused)
            {
                if (ct.IsCancellationRequested)
                {
                    foreach (var e in Cancel(run))
                    {
                        yield return e;
                    }
                }

                yield break;
            }

            if (run.Phase == RunPhase.New)
            {
                yield return Emit(run, AgentEventType.RunStarted,
                    new RunStartedPayload(run.Request.CaseId, run.Request.Symbol, MaxToolCalls));
                if (StepBefore(run, AgentEventType.RunStarted) is { } first) yield return first;
                Enqueue(run, InitialSteps(run));
            }
            else if (run.Phase == RunPhase.Resumed)
            {
                Enqueue(run, ResumeSteps(run));
            }

            run.Phase = RunPhase.Running;

            while (run.Pending.TryDequeue(out var step))
            {
                switch (step)
                {
                    case SilenceStep silence:
                        if (await DelayAsync(silence.Milliseconds, ct))
                        {
                            foreach (var e in Cancel(run)) yield return e;
                            yield break;
                        }

                        break;

                    case TextStep text:
                        foreach (var chunk in Chunk(text.Text))
                        {
                            if (await DelayAsync(_options.TokenDelayMs, ct))
                            {
                                foreach (var e in Cancel(run)) yield return e;
                                yield break;
                            }

                            yield return Emit(run, AgentEventType.Token, new TokenPayload(chunk));
                        }

                        break;

                    case ToolStep tool:
                        var callId = $"call_{++run.CallCounter}";
                        run.ToolCallIds[tool.Name] = callId;
                        run.ToolCallsUsed++;
                        if (StepBefore(run, AgentEventType.ToolCallStarted, tool.Name) is { } toolStep) yield return toolStep;
                        yield return Emit(run, AgentEventType.ToolCallStarted,
                            new ToolCallStartedPayload(callId, tool.Name, JsonSerializer.SerializeToElement(tool.Arguments)));

                        if (await DelayAsync(run.ToolDelayMs, ct))
                        {
                            run.InFlightTool = (callId, tool.Name);
                            foreach (var e in Cancel(run)) yield return e;
                            yield break;
                        }

                        var raw = RawOutput(run, tool);
                        yield return Emit(run, AgentEventType.ToolCallFinished, new ToolCallFinishedPayload(
                            callId, tool.Name, tool.Error is null ? ToolCallStatus.Ok : ToolCallStatus.Error, tool.Summary, tool.Error,
                            ToolEvidence.Source(JsonDocument.Parse(raw).RootElement), ToolEvidence.Preview(raw)));
                        break;

                    case ApprovalStep approval:
                        run.Revision++;
                        run.Memo = approval.BuildMemo(run);
                        run.ApprovalId = $"approval_{run.Revision + 1}";
                        var submitId = $"call_memo_{run.Revision + 1}";
                        run.InFlightTool = (submitId, SubmitMemo);
                        if (StepBefore(run, AgentEventType.ToolCallStarted, SubmitMemo) is { } assess) yield return assess;
                        yield return Emit(run, AgentEventType.ToolCallStarted, new ToolCallStartedPayload(
                            submitId, SubmitMemo, JsonSerializer.SerializeToElement(new { memo = run.Memo }, AgentJson.Options)));
                        if (StepBefore(run, AgentEventType.ApprovalRequired) is { } memoStep) yield return memoStep;
                        yield return Emit(run, AgentEventType.ApprovalRequired,
                            new ApprovalRequiredPayload(run.ApprovalId, run.Revision, run.Memo));
                        lock (run)
                        {
                            run.Phase = RunPhase.Paused;
                        }

                        yield break;

                    case FinishSubmitStep finish:
                        var (id, name) = run.InFlightTool!.Value;
                        run.InFlightTool = null;
                        yield return Emit(run, AgentEventType.ToolCallFinished,
                            new ToolCallFinishedPayload(id, name, ToolCallStatus.Ok, finish.Summary));
                        break;

                    case DoneStep done:
                        run.Phase = RunPhase.Finished;
                        yield return Emit(run, AgentEventType.Done, new DonePayload(done.Outcome, run.ToolCallsUsed, run.Memo));
                        yield break;

                    case ErrorStep error:
                        run.Phase = RunPhase.Finished;
                        yield return Emit(run, AgentEventType.Error, new ErrorPayload(error.Code, error.Message, error.Retryable));
                        yield break;
                }
            }
        }
        finally
        {
            run.Gate.Release();
        }
    }

    private FakeRun GetRun(string runId) =>
        _runs.TryGetValue(runId, out var run) ? run : throw new KeyNotFoundException($"Unknown run {runId}.");

    private static void Enqueue(FakeRun run, IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            run.Pending.Enqueue(step);
        }
    }

    private static IEnumerable<AgentEvent> Cancel(FakeRun run)
    {
        run.Pending.Clear();
        run.Phase = RunPhase.Finished;
        if (run.InFlightTool is var (id, name))
        {
            run.InFlightTool = null;
            yield return Emit(run, AgentEventType.ToolCallFinished, new ToolCallFinishedPayload(
                id, name, ToolCallStatus.Error, "Dihentikan oleh pengguna.", new ToolError("cancelled", "Run dihentikan.")));
        }

        yield return Emit(run, AgentEventType.Done, new DonePayload(RunOutcome.Cancelled, run.ToolCallsUsed));
    }

    private static AgentEvent Emit<T>(FakeRun run, AgentEventType type, T payload) =>
        AgentEvent.Create(run.RunId, ++run.Sequence, type, payload);

    /// <summary>The step_started event that must come before an event, if the stage moves.</summary>
    private static AgentEvent? StepBefore(FakeRun run, AgentEventType type, string? toolName = null)
    {
        if (InvestigationSteps.Next(run.Step, type, toolName) is not { } step)
        {
            return null;
        }

        run.Step = step;
        return Emit(run, AgentEventType.StepStarted, InvestigationSteps.Payload(step));
    }

    /// <summary>
    /// What the idx MCP server would return, in its JSON envelope. idx_history carries daily
    /// candles, so its output is larger than the 8 KB preview limit, as it is for real.
    /// </summary>
    private static string RawOutput(FakeRun run, ToolStep tool)
    {
        if (tool.Error is { } error)
        {
            return JsonSerializer.Serialize(new { error = new { code = error.Code, message = error.Message }, exit_code = 3 });
        }

        var provider = tool.Name switch
        {
            "idx_quote" or "idx_history" => "yahoo",
            "idx_news" or "idx_foreign" => "idx",
            _ => "stockbit",
        };
        object data = tool.Name == "idx_history"
            ? new
            {
                summary = new { candles = 64, avg_volume = 210_424_313, low = 4_740, high = 6_850 },
                candles = Enumerable.Range(0, 120).Select(i => new
                {
                    date = run.Request.AnomalyDate.AddDays(i - 119).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    open = 5_800 + i, high = 5_850 + i, low = 5_750 + i, close = 5_810 + i, volume = 200_000_000 + i * 1_000_000,
                }),
            }
            : new { note = tool.Summary, arguments = tool.Arguments };

        return JsonSerializer.Serialize(new
        {
            schema = "idx." + tool.Name[4..],
            provider,
            fetched_at = "2026-11-24T14:09:39Z",
            cached = false,
            stale = false,
            data,
        });
    }

    /// <summary>Returns true when the run was cancelled during the delay.</summary>
    private static async Task<bool> DelayAsync(int milliseconds, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return true;
        }

        if (milliseconds <= 0)
        {
            return false;
        }

        try
        {
            await Task.Delay(milliseconds, ct);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private static IEnumerable<string> Chunk(string text)
    {
        // Word-sized chunks keep the streaming visible without flooding the database.
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i += 3)
        {
            var end = Math.Min(i + 3, words.Length);
            var chunk = string.Join(' ', words[i..end]);
            yield return end < words.Length ? chunk + " " : chunk + "\n";
        }
    }

    private static IEnumerable<Step> InitialSteps(FakeRun run)
    {
        var r = run.Request;
        var s = r.Symbol;
        var date = r.AnomalyDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var t = r.Trigger;
        var volume = t.Volume ?? 235_635_300;
        var change = t.PriceChangePct ?? 2.2;

        if (run.SilenceMs > 0)
        {
            yield return new SilenceStep(run.SilenceMs);
        }

        yield return new TextStep($"Memulai investigasi {s} untuk tanggal {r.AnomalyDate.ToString("d MMMM yyyy", Id)}. Pemicu: {t.Rule}.");
        yield return new ToolStep("idx_stockbit_status", new { }, "Sesi Stockbit aktif.");
        yield return new TextStep("Mengukur penyimpangan harga dan volume terhadap tiga bulan terakhir.");
        yield return new ToolStep("idx_quote", new { symbols = s },
            $"Harga berubah {change.ToString("0.##", Id)}%, volume {volume.ToString("N0", Id)} lembar.");

        if (run.Scenario == FakeScenario.StepLimit)
        {
            foreach (var step in StepLimitSteps(s))
            {
                yield return step;
            }

            yield break;
        }

        yield return new ToolStep("idx_history", new { symbol = s, period = "3mo" },
            "Rata-rata volume 3 bulan 210.424.313 lembar. Rentang harga 4.740 hingga 6.850.");

        if (run.Scenario == FakeScenario.ModelError)
        {
            yield return new ErrorStep(AgentErrorCode.ModelError, "Model AI sedang tidak merespons. Coba lagi dalam beberapa menit.", true);
            yield break;
        }

        yield return new TextStep("Mencari penjelasan wajar di berita dan aksi korporasi.");
        yield return new ToolStep("idx_news", new { symbol = s, company_only = true },
            "1 berita menyebut emiten, isinya soal pergerakan IHSG. Tidak ada informasi material.");
        yield return new ToolStep("idx_stockbit_actions", new { symbol = s },
            "Dividen tunai Rp35 per saham, cum date 27 November 2026.");
        yield return new TextStep("Penjelasan wajar belum cukup. Memeriksa broker, bandar, dan arus dana asing.");
        yield return new ToolStep("idx_broker", new { symbols = s, date },
            "53 pembeli dan 70 penjual. Tiga pembeli teratas broker asing dengan 56% nilai beli.");
        yield return new ToolStep("idx_bandar", new { symbols = s, period = "1d" },
            "Data bandar tidak tersedia (IPOT HTTP 502).",
            new ToolError("provider", $"ipot: broker summary for {s}: HTTP 502"));
        yield return new ToolStep("idx_foreign", new { symbol = s, days = 5, source = "idx" },
            "Asing net buy 70.518.000 lembar pada tanggal anomali, dua sesi beruntun.");
        yield return new TextStep("Menyusun draf memo untuk ditelaah analis.");
        yield return new ApprovalStep(BuildMemo);
    }

    private static IEnumerable<Step> StepLimitSteps(string symbol)
    {
        string[] tools = ["idx_history", "idx_news", "idx_foreign", "idx_broker"];
        for (var i = 0; i < MaxToolCalls - 2; i++)
        {
            var name = tools[i % tools.Length];
            yield return new ToolStep(name, new { symbol, attempt = i + 1 }, $"Pemeriksaan ulang ke-{i + 1}.");
        }

        yield return new ErrorStep(AgentErrorCode.StepLimitExceeded,
            $"Agent mencapai batas {MaxToolCalls} panggilan data tanpa menyusun memo.", false);
    }

    private static IEnumerable<Step> ResumeSteps(FakeRun run)
    {
        var decision = run.Decision!;
        switch (decision.Decision)
        {
            case Decision.Approve:
                yield return new FinishSubmitStep("Memo disetujui analis.");
                yield return new DoneStep(RunOutcome.Approved);
                break;

            case Decision.Reject:
                yield return new FinishSubmitStep("Memo ditolak analis.");
                yield return new DoneStep(RunOutcome.Rejected);
                break;

            case Decision.Revise when run.Revision >= MaxRevisions:
                yield return new FinishSubmitStep("Revisi diminta, tetapi batas revisi sudah tercapai.");
                yield return new ErrorStep(AgentErrorCode.RevisionLimitExceeded,
                    $"Memo sudah direvisi {MaxRevisions} kali. Setujui atau tolak draf terakhir.", false);
                break;

            case Decision.Revise:
                yield return new FinishSubmitStep("Analis meminta revisi.");
                yield return new TextStep($"Memperbaiki memo sesuai komentar analis: \"{decision.Comment}\".");
                if (run.ToolCallsUsed < MaxToolCalls)
                {
                    yield return new ToolStep("idx_foreign", new { symbol = run.Request.Symbol, days = 20, source = "idx" },
                        "20 sesi terakhir: asing net buy 12 sesi, net sell 8 sesi.");
                }

                yield return new ApprovalStep(BuildMemo);
                break;
        }
    }

    private static Memo BuildMemo(FakeRun run)
    {
        var r = run.Request;
        var t = r.Trigger;
        string Ref(string tool) => run.ToolCallIds.GetValueOrDefault(tool, "call_1");

        var findings = new List<Finding>
        {
            new(FindingCategory.PriceVolume,
                "Volume pada tanggal anomali di atas rata-rata 3 bulan, dengan kenaikan harga moderat.",
                [new Evidence(Ref("idx_quote"), "idx_quote", "Volume dan perubahan harga harian"),
                 new Evidence(Ref("idx_history"), "idx_history", "Rata-rata volume 3 bulan 210.424.313 lembar")]),
            new(FindingCategory.News,
                "Tidak ada berita material yang menjelaskan pergerakan.",
                [new Evidence(Ref("idx_news"), "idx_news", "1 berita menyebut emiten, soal IHSG")]),
            new(FindingCategory.CorporateAction,
                "Cum date dividen tunai Rp35 per saham pada 27 November 2026 bisa menjelaskan sebagian minat beli.",
                [new Evidence(Ref("idx_stockbit_actions"), "idx_stockbit_actions", "Dividen tunai, cum date 27 November 2026")]),
            new(FindingCategory.Broker,
                "Transaksi beli terkonsentrasi pada tiga broker asing (56% nilai beli).",
                [new Evidence(Ref("idx_broker"), "idx_broker", "Broker summary Stockbit, daftar lengkap 53 pembeli")]),
            new(FindingCategory.Foreign,
                "Asing mencatat net buy dua sesi beruntun.",
                [new Evidence(Ref("idx_foreign"), "idx_foreign", "foreign_net 70.518.000 lembar")]),
        };

        var revised = run.Revision > 0;
        return new Memo(
            Symbol: r.Symbol,
            AnomalyDate: r.AnomalyDate,
            Summary: revised
                ? $"Revisi ke-{run.Revision}. Volume {r.Symbol} di atas rata-rata dengan pembelian terkonsentrasi di broker asing. Pola asing 20 sesi sudah ditambahkan. Ada aksi korporasi yang bisa menjelaskan sebagian pergerakan."
                : $"Volume {r.Symbol} di atas rata-rata dengan pembelian terkonsentrasi di broker asing. Tidak ada berita material. Ada aksi korporasi yang bisa menjelaskan sebagian pergerakan.",
            Anomaly: new AnomalyFacts(
                Description: $"Pemicu: {t.Rule}.",
                PriceChangePct: t.PriceChangePct ?? 2.2,
                Volume: t.Volume ?? 235_635_300,
                AvgVolume: t.AvgVolume ?? 210_424_313,
                VolumeRatio: t.VolumeRatio ?? 1.12),
            Findings: findings,
            UnavailableData: [new UnavailableData("idx_bandar", "IPOT membalas HTTP 502.")],
            EvidenceLevel: EvidenceLevel.Moderate,
            EvidenceRationale: "Pola konsentrasi broker terlihat, tetapi data bandar tidak tersedia dan ada aksi korporasi yang relevan.",
            Recommendation: Recommendation.Monitor,
            RecommendationRationale: "Pantau konsentrasi broker asing pada dua sesi berikutnya sebelum memutuskan eskalasi.");
    }

    private enum RunPhase
    {
        New,
        Running,
        Paused,
        Resumed,
        Finished,
    }

    private sealed class FakeRun(string runId, InvestigationRequest request, FakeScenario scenario)
    {
        public string RunId { get; } = runId;
        public InvestigationRequest Request { get; } = request;
        public FakeScenario Scenario { get; } = scenario;
        public int ToolDelayMs { get; init; }
        public int SilenceMs { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Queue<Step> Pending { get; } = new();
        public Dictionary<string, string> ToolCallIds { get; } = new();
        public RunPhase Phase { get; set; } = RunPhase.New;
        public long Sequence { get; set; }
        public int ToolCallsUsed { get; set; }
        public int CallCounter { get; set; }
        public int Revision { get; set; } = -1;
        public string? ApprovalId { get; set; }
        public ApprovalDecision? Decision { get; set; }
        public Memo? Memo { get; set; }
        public (string Id, string Name)? InFlightTool { get; set; }
        public InvestigationStep? Step { get; set; }
    }

    private abstract record Step;
    private sealed record TextStep(string Text) : Step;
    private sealed record SilenceStep(int Milliseconds) : Step;
    private sealed record ToolStep(string Name, object Arguments, string Summary, ToolError? Error = null) : Step;
    private sealed record ApprovalStep(Func<FakeRun, Memo> BuildMemo) : Step;
    private sealed record FinishSubmitStep(string Summary) : Step;
    private sealed record DoneStep(RunOutcome Outcome) : Step;
    private sealed record ErrorStep(AgentErrorCode Code, string Message, bool Retryable) : Step;
}
