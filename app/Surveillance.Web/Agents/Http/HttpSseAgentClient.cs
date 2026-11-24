using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Web.Agents.Http;

public sealed class HttpAgentOptions
{
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// An agent service reached over the HTTP + SSE contract in
/// contracts/agent-service-api.md. The service already emits catalog events,
/// so this client only transports them; LangGraph and ADK differ only in the URL.
/// </summary>
public abstract class HttpSseAgentClient(HttpClient http, ILogger logger) : IAgentClient
{
    private const int MaxReconnects = 3;

    // Last sequence yielded per run. Lost on restart; the caller drops duplicates by sequence.
    private readonly ConcurrentDictionary<string, long> _lastSequence = new();

    public async Task<string> StartAsync(InvestigationRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("runs", request, AgentJson.Options, ct);
        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(AgentJson.Options, ct);
        return body.GetProperty("runId").GetString() ?? throw new InvalidDataException("Service returned no runId.");
    }

    public async Task ResumeAsync(string runId, ApprovalDecision decision, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"runs/{Uri.EscapeDataString(runId)}/resume", decision, AgentJson.Options, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(
                $"Run {runId} is not waiting for approval {decision.ApprovalId}: {await response.Content.ReadAsStringAsync(ct)}");
        }

        await EnsureSuccessAsync(response, ct);
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(string runId, [EnumeratorCancellation] CancellationToken ct)
    {
        // Cancelling means "stop the run": tell the service, then keep reading until it reports done.
        await using var registration = ct.Register(() => _ = CancelRunAsync(runId));

        var reconnects = 0;
        while (true)
        {
            var after = _lastSequence.GetValueOrDefault(runId);
            var received = 0;
            AgentEvent? last = null;

            await using var stream = await OpenEventsAsync(runId, after);
            var reader = SseParser.Create(stream).EnumerateAsync(CancellationToken.None).GetAsyncEnumerator();
            try
            {
                while (true)
                {
                    SseItem<string> item;
                    try
                    {
                        if (!await reader.MoveNextAsync())
                        {
                            break;
                        }

                        item = reader.Current;
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException && reconnects < MaxReconnects)
                    {
                        logger.LogWarning(ex, "Event stream for {RunId} dropped; reconnecting", runId);
                        break;
                    }

                    var e = JsonSerializer.Deserialize<AgentEvent>(item.Data, AgentJson.Options)
                            ?? throw new InvalidDataException("Empty event.");
                    if (e.Sequence <= _lastSequence.GetValueOrDefault(runId))
                    {
                        continue;
                    }

                    _lastSequence[runId] = e.Sequence;
                    received++;
                    last = e;
                    yield return e;
                }
            }
            finally
            {
                await reader.DisposeAsync();
            }

            // The service closes the stream after a pause or the end; anything else was a dropped connection.
            if (last is null && received == 0 || last is { IsTerminal: true } || last?.Type == AgentEventType.ApprovalRequired)
            {
                yield break;
            }

            if (++reconnects > MaxReconnects)
            {
                throw new IOException($"Event stream for run {runId} kept dropping.");
            }

            await Task.Delay(TimeSpan.FromSeconds(reconnects), CancellationToken.None);
        }
    }

    private async Task<Stream> OpenEventsAsync(string runId, long after)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"runs/{Uri.EscapeDataString(runId)}/events?after={after}");
        request.Headers.Accept.ParseAdd("text/event-stream");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new KeyNotFoundException($"Unknown run {runId}.");
        }

        await EnsureSuccessAsync(response, CancellationToken.None);
        return await response.Content.ReadAsStreamAsync(CancellationToken.None);
    }

    private async Task CancelRunAsync(string runId)
    {
        try
        {
            using var response = await http.PostAsync($"runs/{Uri.EscapeDataString(runId)}/cancel", null, CancellationToken.None);
            await EnsureSuccessAsync(response, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not cancel run {RunId}", runId);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {body}", null, response.StatusCode);
        }
    }
}

/// <summary>Solution 2: the LangGraph service (agents/langgraph, port 8101).</summary>
public sealed class LangGraphAgentClient(HttpClient http, ILogger<LangGraphAgentClient> logger)
    : HttpSseAgentClient(http, logger);

/// <summary>Solution 3: the Google ADK service (agents/adk, port 8102).</summary>
public sealed class AdkAgentClient(HttpClient http, ILogger<AdkAgentClient> logger)
    : HttpSseAgentClient(http, logger);
