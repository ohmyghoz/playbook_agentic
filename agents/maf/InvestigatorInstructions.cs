using System.Globalization;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Agents.Maf;

/// <summary>The system instructions from contracts/asd-investigator-uma.md, shared by all three solutions.</summary>
public static class InvestigatorInstructions
{
    public const string SubmitMemo = "submit_memo";

    /// <summary>MCP tools the investigator may call (ASD allowlist). Everything else is dropped.</summary>
    public static readonly IReadOnlySet<string> AllowedMcpTools = new HashSet<string>
    {
        "idx_stockbit_status", "idx_quote", "idx_history", "idx_news", "idx_stockbit_actions",
        "idx_corporate_actions", "idx_foreign", "idx_broker", "idx_bandar", "idx_stockbit_movements",
    };

    private const string Template = """
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
          submit_memo lagi.
        """;

    public static string Build(InvestigationRequest request, int maxToolCalls) => Template
        .Replace("{symbol}", request.Symbol)
        .Replace("{anomalyDate}", request.AnomalyDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        .Replace("{triggerRule}", request.Trigger.Rule)
        .Replace("{notes}", string.IsNullOrWhiteSpace(request.Notes) ? "-" : request.Notes)
        .Replace("{maxToolCalls}", maxToolCalls.ToString(CultureInfo.InvariantCulture));

    /// <summary>The first user message: the trigger facts from the detection code.</summary>
    public static string FirstMessage(InvestigationRequest request)
    {
        var t = request.Trigger;
        var facts = new List<string> { $"Jenis pemicu: {t.Kind.ToString().ToLowerInvariant()}" };
        if (t.PriceChangePct is { } p) facts.Add($"perubahan harga {p.ToString(CultureInfo.InvariantCulture)}%");
        if (t.Volume is { } v) facts.Add($"volume {v.ToString(CultureInfo.InvariantCulture)} lembar");
        if (t.AvgVolume is { } a) facts.Add($"rata-rata volume {a.ToString(CultureInfo.InvariantCulture)}");
        if (t.VolumeRatio is { } r) facts.Add($"rasio volume {r.ToString(CultureInfo.InvariantCulture)}x");
        return $"Mulai investigasi {request.Symbol} untuk {request.AnomalyDate:yyyy-MM-dd}. Data dari kode deteksi: {string.Join(", ", facts)}.";
    }
}
