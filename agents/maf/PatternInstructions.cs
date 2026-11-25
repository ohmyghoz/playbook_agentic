using Surveillance.Agents.Abstractions;

namespace Surveillance.Agents.Maf;

/// <summary>
/// Texts and tool sets of the pattern-detection agents, copied from asd-pattern-detection.md
/// ("Instruksi worker", "Instruksi penyusun laporan", "Peran dan tool"); a test keeps them equal.
/// </summary>
public static class PatternInstructions
{
    public const string Orchestrator = "orchestrator";
    public const string FinishWorker = "finish_worker";
    public const string SubmitReport = "submit_report";
    public const int MaxParallelWorkers = 3;

    public static IReadOnlySet<string> ToolsFor(WorkerRole role) => role switch
    {
        WorkerRole.Stock => new HashSet<string> { "idx_history", "idx_technical", "idx_broker", "idx_bandar", "idx_foreign", "idx_news", "idx_stockbit_actions" },
        WorkerRole.Broker => new HashSet<string> { "idx_broker", "idx_market_brokers", "idx_members", "idx_screen" },
        _ => new HashSet<string> { "idx_ownership_ticker", "idx_ownership_graph", "idx_ownership_entity", "idx_ownership_changes" },
    };

    public static string RoleName(WorkerRole role) => role switch
    {
        WorkerRole.Stock => "saham",
        WorkerRole.Broker => "broker",
        _ => "kepemilikan",
    };

    public const string WorkerTemplate = """
        Anda worker {role} dalam deteksi pola akumulasi-distribusi di Bursa Efek Indonesia. Target Anda: {target}. Jendela pengamatan {windowStart} sampai {windowEnd}.
        Gunakan hanya tool yang tersedia, paling banyak {maxSteps} panggilan. Setiap hasil tool diawali [toolCallId: ...]; rujuk id itu sebagai bukti.
        Worker saham: periksa pergerakan harga dan volume dalam jendela, lalu broker summary (idx_broker) untuk porsi nilai beli bersih broker teratas; laporkan broker dominan di dominantBrokers.
        Worker broker: cari saham lain tempat broker target menjadi pembeli teratas dengan pola serupa; laporkan sebagai leads (kind stock).
        Worker kepemilikan: periksa apakah saham-saham target punya pemegang saham yang sama atau terafiliasi di atas 1%.
        Bila tool gagal, catat di summary dan lanjutkan. Jangan menyimpulkan manipulasi atau pelanggaran; tulis sebagai indikasi.
        Akhiri dengan memanggil finish_worker tepat satu kali.
        """;

    public const string ReportTemplate = """
        Anda penyusun laporan deteksi pola akumulasi-distribusi untuk analis pengawasan pasar modal. Anda menerima hasil worker dalam JSON: klaster, temuan, jejak yang tidak diikuti, dan batas yang tercapai.
        Susun PatternReport lalu panggil submit_report. Setiap pernyataan di findings dan alternativeExplanations harus merujuk toolCallId dari temuan worker. Jangan menambah fakta yang tidak ada di hasil worker.
        Gunakan bahasa indikasi, bukan kesimpulan pelanggaran. disclaimer selalu "Indikasi untuk ditelaah analis, bukan kesimpulan pelanggaran."
        Bila analis meminta revisi, perbaiki laporan sesuai komentarnya lalu panggil submit_report lagi.
        """;

    public static string Worker(WorkerRole role, string target, PatternJob job) => WorkerTemplate
        .Replace("{role}", RoleName(role))
        .Replace("{target}", target)
        .Replace("{windowStart}", job.WindowStart.ToString("yyyy-MM-dd"))
        .Replace("{windowEnd}", job.WindowEnd.ToString("yyyy-MM-dd"))
        .Replace("{maxSteps}", job.Limits.MaxStepsPerWorker.ToString());
}

/// <summary>One broker's share of net buying in a stock, as a stock worker reports it.</summary>
public sealed record BrokerShare(string Broker, double NetBuySharePct);

/// <summary>The arguments of finish_worker (asd-pattern-detection.md, Protokol worker).</summary>
public sealed record WorkerReport(
    string Summary,
    IReadOnlyList<BrokerShare>? DominantBrokers = null,
    IReadOnlyList<Lead>? Leads = null,
    IReadOnlyList<Statement>? Findings = null);

/// <summary>What the orchestrator knows about one worker after it ended.</summary>
public sealed record WorkerOutcome(WorkerStartedPayload Worker, WorkerStatus Status, WorkerReport? Report);
