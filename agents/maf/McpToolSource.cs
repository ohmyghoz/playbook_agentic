using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Surveillance.Agents.Maf;

/// <summary>Supplies the MCP tools the investigator may use.</summary>
public interface IToolSource
{
    /// <exception cref="McpUnavailableException">The MCP server cannot be reached.</exception>
    Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct);

    /// <summary>The MCP tools with these names, for pattern-detection workers (asd-pattern-detection.md).</summary>
    async Task<IReadOnlyList<AITool>> GetToolsAsync(IReadOnlySet<string> names, CancellationToken ct) =>
        (await GetToolsAsync(ct)).Where(t => names.Contains(t.Name)).ToList();
}

public sealed class McpUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Connects to the idx MCP server over streamable HTTP and returns only the
/// tools on the ASD allowlist. The connection is reused and re-created after
/// a failure.
/// </summary>
public sealed class McpToolSource(MafAgentOptions options) : IToolSource, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private McpClient? _client;
    private IReadOnlyList<AITool>? _all;
    private IReadOnlyList<AITool>? _tools;

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(IReadOnlySet<string> names, CancellationToken ct)
    {
        await GetToolsAsync(ct);
        return _all!.Where(t => names.Contains(t.Name)).ToList();
    }

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_tools is not null)
            {
                return _tools;
            }

            if (string.IsNullOrWhiteSpace(options.McpToken))
            {
                throw new McpUnavailableException("IDX_MCP_TOKEN is not set.");
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
                _client = await McpClient.CreateAsync(transport, cancellationToken: ct);
                var all = await _client.ListToolsAsync(cancellationToken: ct);
                _all = all.Cast<AITool>().ToList();
                _tools = _all.Where(t => InvestigatorInstructions.AllowedMcpTools.Contains(t.Name)).ToList();
                return _tools;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ResetAsync();
                throw new McpUnavailableException($"MCP server at {options.McpEndpoint} is unavailable.", ex);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Drops the cached connection so the next run reconnects.</summary>
    public async Task ResetAsync()
    {
        _tools = null;
        _all = null;
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }

    public async ValueTask DisposeAsync() => await ResetAsync();
}
