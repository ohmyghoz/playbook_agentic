using Microsoft.Extensions.AI;
using Surveillance.Agents.Abstractions;
using Surveillance.Agents.Maf;

namespace Surveillance.Web.Tests.Maf;

/// <summary>Results shaped as McpClientTool returns them: a TextContent holding the idx envelope.</summary>
public class ToolResultSummarizerTests
{
    private static ToolCallFinishedPayload Summarize(string tool, string envelope) =>
        ToolResultSummarizer.Summarize("c1", tool, new FunctionResultContent("c1", $"[toolCallId: c1]\n{System.Text.Json.JsonSerializer.Serialize<AIContent>(new TextContent(envelope), AIJsonUtilities.DefaultOptions)}"));

    [Theory]
    [InlineData("idx_quote", """{"schema":"idx.quotes.v1","data":[{"price":5800.0,"change_pct":2.2026,"volume":235635300,"avg_volume_3m":212048028}]}""", "Harga 5.800 (+2,2%), volume 235.635.300 lembar, 1,1x rata-rata 3 bulan.")]
    [InlineData("idx_history", """{"schema":"idx.history.v1","data":{"summary":{"candles":66,"avg_volume":210424313,"low":4740.0,"high":6850.0}}}""", "66 sesi. Rata-rata volume 210.424.313 lembar, rentang harga 4.740 hingga 6.850.")]
    [InlineData("idx_stockbit_status", """{"logged_in":true,"renewable":true}""", "Sesi Stockbit aktif.")]
    [InlineData("idx_news", """{"schema":"idx.news.v1","data":{"company_matches":1,"items":[{},{}]}}""", "1 berita menyebut emiten dari 2 berita yang dikirim.")]
    [InlineData("idx_foreign", """{"schema":"idx.foreign.v1","data":{"totals":{"sessions":5,"foreign_net":103508500.0,"streak":2}}}""", "Asing net buy 103.508.500 lembar dalam 5 sesi, streak 2.")]
    [InlineData("idx_broker", """{"schema":"idx.broker.v1","data":[{"buyer_count":53,"seller_count":70,"source":"stockbit","buyers_complete":true}]}""", "53 pembeli dan 70 penjual (stockbit, daftar lengkap).")]
    [InlineData("idx_stockbit_actions", """{"schema":"idx.stockbit.actions.v1","data":{"total":55,"actions":[{"kind":"dividend","date":"2026-11-27"}]}}""", "1 aksi korporasi. Terbaru: dividend pada 2026-11-27.")]
    public void Summarizes_real_result_shapes(string tool, string envelope, string expected)
    {
        var payload = Summarize(tool, envelope);

        Assert.Equal(ToolCallStatus.Ok, payload.Status);
        Assert.Equal(expected, payload.Summary);
    }

    [Fact]
    public void Error_envelope_becomes_failed_call_with_source_code()
    {
        var payload = Summarize("idx_bandar", """{"error":{"code":"provider","message":"ipot: HTTP 502"},"exit_code":3}""");

        Assert.Equal(ToolCallStatus.Error, payload.Status);
        Assert.Equal("provider", payload.Error!.Code);
    }
}
