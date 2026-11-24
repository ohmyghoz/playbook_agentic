using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Agents.Maf;

/// <summary>
/// Turns an idx MCP tool result into the short Indonesian summary of
/// <c>tool_call_finished</c>. Raw tool output never reaches the front end.
/// </summary>
public static class ToolResultSummarizer
{
    private static readonly CultureInfo Id = CultureInfo.GetCultureInfo("id-ID");

    public static ToolCallFinishedPayload Summarize(string callId, string toolName, FunctionResultContent result)
    {
        if (result.Exception is { } ex)
        {
            return Failed(callId, toolName, "tool_error", ex.Message);
        }

        var text = ResultText(result.Result);
        if (text.StartsWith(ToolLimits.LimitMarker, StringComparison.Ordinal))
        {
            return Failed(callId, toolName, "step_limit", "Tidak dijalankan: batas panggilan data tercapai.");
        }

        if (toolName == InvestigatorInstructions.SubmitMemo)
        {
            return new ToolCallFinishedPayload(callId, toolName, ToolCallStatus.Ok, "Memo disetujui analis.");
        }

        try
        {
            var envelope = FindEnvelope(text);
            if (envelope is null)
            {
                return new ToolCallFinishedPayload(callId, toolName, ToolCallStatus.Ok, "Data diterima.");
            }

            var root = envelope.Value;
            if (root.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() ?? "error" : "error";
                var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                return Failed(callId, toolName, code, message);
            }

            var data = root.GetProperty("data");
            return new ToolCallFinishedPayload(callId, toolName, ToolCallStatus.Ok, Clip(Describe(toolName, data)));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new ToolCallFinishedPayload(callId, toolName, ToolCallStatus.Ok, "Data diterima.");
        }
    }

    /// <summary>Flattens a tool result (string, JSON, or MCP CallToolResult) to text.</summary>
    public static string ResultText(object? result) => result switch
    {
        null => "",
        string s => s,
        JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText(),
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions),
    };

    private static ToolCallFinishedPayload Failed(string callId, string toolName, string code, string message) =>
        new(callId, toolName, ToolCallStatus.Error, Clip(FailureText(toolName, code)), new ToolError(code, Clip(message)));

    private static string FailureText(string toolName, string code) => code switch
    {
        "step_limit" => "Tidak dijalankan: batas panggilan data tercapai.",
        "blocked" => "Sumber data menolak permintaan sementara. Data tidak tersedia.",
        "not_found" => "Kode saham tidak dikenal sumber data.",
        _ => $"Data {toolName} tidak tersedia ({code}).",
    };

    /// <summary>Finds the idx JSON envelope, which may be wrapped in MCP content or prefixed by the call id.</summary>
    private static JsonElement? FindEnvelope(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(text[start..]);
        var root = doc.RootElement;
        if (root.TryGetProperty("schema", out _) || root.TryGetProperty("error", out _))
        {
            return root.Clone();
        }

        // A single MCP text content, as McpClientTool returns it: { "$type": "text", "text": "<envelope>" }
        if (root.TryGetProperty("text", out var single) && single.ValueKind == JsonValueKind.String)
        {
            return FindEnvelope(single.GetString()!);
        }

        // idx_stockbit_status answers with a bare object instead of an envelope.
        if (root.TryGetProperty("logged_in", out _))
        {
            using var wrapped = JsonDocument.Parse($$"""{"schema":"idx.stockbit.status","data":{{root.GetRawText()}}}""");
            return wrapped.RootElement.Clone();
        }

        // MCP CallToolResult: { "content": [ { "type": "text", "text": "<envelope>" } ], "isError": ... }
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var inner) && inner.GetString() is { } innerText)
                {
                    return FindEnvelope(innerText);
                }
            }
        }

        return null;
    }

    private static string Describe(string toolName, JsonElement data) => toolName switch
    {
        "idx_stockbit_status" => Bool(data, "logged_in") ? "Sesi Stockbit aktif." : "Sesi Stockbit tidak aktif; tool Stockbit dilewati.",
        "idx_quote" => Quote(data[0]),
        "idx_history" => History(data),
        "idx_news" => $"{Int(data, "company_matches")} berita menyebut emiten dari {Count(data, "items")} berita yang dikirim.",
        "idx_stockbit_actions" => Actions(data, "actions"),
        "idx_corporate_actions" => Actions(data, "actions"),
        "idx_foreign" => Foreign(data),
        "idx_broker" => Broker(data[0]),
        "idx_bandar" => $"Sinyal {Str(data[0], "signal")}, fase {Str(data[0], "phase")}.",
        "idx_stockbit_movements" => $"{Count(data, "movements")} transaksi insider atau pemegang besar.",
        _ => "Data diterima.",
    };

    private static string Quote(JsonElement q)
    {
        var text = $"Harga {Num(q, "price")} ({Signed(q, "change_pct")}%), volume {Num(q, "volume")} lembar";
        if (Double(q, "volume") is { } v && Double(q, "avg_volume_3m") is { } avg && avg > 0)
        {
            text += $", {(v / avg).ToString("0.0", Id)}x rata-rata 3 bulan";
        }

        return text + ".";
    }

    private static string History(JsonElement data)
    {
        var s = data.GetProperty("summary");
        return $"{Int(s, "candles")} sesi. Rata-rata volume {Num(s, "avg_volume")} lembar, rentang harga {Num(s, "low")} hingga {Num(s, "high")}.";
    }

    private static string Actions(JsonElement data, string listName)
    {
        var total = Count(data, listName);
        if (total == 0)
        {
            return "Tidak ada aksi korporasi.";
        }

        var first = data.GetProperty(listName)[0];
        return $"{total} aksi korporasi. Terbaru: {Str(first, "kind")} pada {Str(first, "date", Str(first, "cum_date"))}.";
    }

    private static string Foreign(JsonElement data)
    {
        var t = data.GetProperty("totals");
        var net = Double(t, "foreign_net") ?? 0;
        var direction = net >= 0 ? "net buy" : "net sell";
        return $"Asing {direction} {Math.Abs(net).ToString("N0", Id)} lembar dalam {Int(t, "sessions")} sesi, streak {Int(t, "streak")}.";
    }

    private static string Broker(JsonElement b)
    {
        var source = Str(b, "source");
        var complete = Bool(b, "buyers_complete") ? "daftar lengkap" : "10 teratas";
        return $"{Int(b, "buyer_count")} pembeli dan {Int(b, "seller_count")} penjual ({source}, {complete}).";
    }

    private static string Clip(string text) => text.Length <= 500 ? text : text[..497] + "...";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double? Double(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Num(JsonElement e, string name) => Double(e, name)?.ToString("N0", Id) ?? "-";

    private static string Signed(JsonElement e, string name) =>
        Double(e, name) is { } d ? (d >= 0 ? "+" : "") + d.ToString("0.##", Id) : "-";

    private static int Int(JsonElement e, string name) => (int)(Double(e, name) ?? 0);

    private static int Count(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

    private static string Str(JsonElement e, string name, string fallback = "-") =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
}
