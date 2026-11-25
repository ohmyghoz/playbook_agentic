using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Surveillance.Agents.Maf;

namespace Surveillance.Web.Patterns;

/// <summary>Read-only market data for the screening code: one idx MCP tool call, the envelope's data back.</summary>
public interface IMarketData
{
    /// <exception cref="MarketDataException">The tool failed or the server cannot be reached.</exception>
    Task<JsonElement> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct);
}

public sealed class MarketDataException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Calls the idx MCP server directly, without an agent: screening is ordinary code with thresholds
/// (CLAUDE.md, Use case). Uses the same endpoint and token as the MAF adapter.
/// </summary>
public sealed class McpMarketData(MafAgentOptions options) : IMarketData, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private McpClient? _client;

    public async Task<JsonElement> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(tool, arguments, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ResetAsync();
            throw new MarketDataException($"{tool} gagal: {ex.Message}", ex);
        }

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (result.IsError == true || root.TryGetProperty("error", out _))
        {
            var message = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : text;
            throw new MarketDataException($"{tool}: {message}");
        }

        return root.GetProperty("data").Clone();
    }

    private async Task<McpClient> ClientAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            if (string.IsNullOrWhiteSpace(options.McpToken))
            {
                throw new MarketDataException("IDX_MCP_TOKEN belum diisi, jadi server data pasar tidak bisa dipakai.");
            }

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(options.McpEndpoint),
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {options.McpToken}" },
            });
            try
            {
                return _client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new MarketDataException($"Server data pasar di {options.McpEndpoint} tidak bisa dihubungi.", ex);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ResetAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }

    public async ValueTask DisposeAsync() => await ResetAsync();
}

/// <summary>
/// Canned market data in the shape of the idx envelopes, for tests and for demos without the
/// MCP server. Three stocks move more than 15% in ten sessions; one does not.
/// </summary>
public sealed class FakeMarketData : IMarketData
{
    // symbol -> closes of the last 11 sessions (10 intervals)
    private static readonly Dictionary<string, double[]> Closes = new()
    {
        ["VOKS"] = [340, 345, 352, 360, 372, 380, 395, 410, 425, 440, 450],
        ["PSKT"] = [170, 172, 175, 181, 186, 190, 198, 205, 214, 220, 224],
        ["ARII"] = [600, 610, 612, 618, 625, 630, 640, 650, 655, 662, 670],
        ["BUMI"] = [180, 176, 170, 165, 160, 158, 150, 146, 140, 137, 132],
    };

    public Task<JsonElement> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        object data = tool switch
        {
            "idx_screen" => Screen(arguments.GetValueOrDefault("filter") as string ?? ""),
            "idx_history" => History((string)arguments["symbol"]!),
            _ => throw new MarketDataException($"{tool} tidak tersedia di data tiruan."),
        };
        return Task.FromResult(JsonSerializer.SerializeToElement(data));
    }

    private static object Screen(string filter)
    {
        var up = !filter.Contains("<=", StringComparison.Ordinal);
        var rows = Closes
            .Select(kv => new { symbol = kv.Key, return_1m_pct = (kv.Value[^1] / kv.Value[0] - 1) * 100 * 1.4 })
            .Where(r => up ? r.return_1m_pct >= 15 : r.return_1m_pct <= -15)
            .ToList();
        return new { rows, total_matches = rows.Count };
    }

    private static object History(string symbol)
    {
        var closes = Closes.TryGetValue(symbol, out var c) ? c : throw new MarketDataException($"{symbol} tidak dikenal.");
        var end = new DateTime(2026, 11, 24, 9, 0, 0);
        var candles = closes.Select((close, i) => new
        {
            time = end.AddDays(i - closes.Length + 1).ToString("yyyy-MM-dd'T'HH:mm:ss+07:00"),
            close,
        });
        return new { symbol, candles, summary = new { candles = closes.Length } };
    }
}
