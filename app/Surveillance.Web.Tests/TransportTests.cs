using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;
using Surveillance.Web.Hubs;
using Surveillance.Web.Investigations;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

/// <summary>Slow tools so a run is still streaming while a SignalR client joins and reconnects.</summary>
public class SignalRTransportTests(SlowSurveillanceFactory factory) : IClassFixture<SlowSurveillanceFactory>
{
    private static StartInvestigation Input() =>
        new("ASII", new DateOnly(2026, 11, 24), TriggerKind.Volume, "Volume di atas 1,1x", null, null, null, null, null);

    private HubConnection Connect(ConcurrentDictionary<long, AgentEvent> received, Action<AgentEvent>? onLive = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, InvestigationHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling; // TestServer has no WebSocket upgrade for SignalR
                options.Headers[TestAuthHandler.Header] = "analis1";
            })
            .Build();
        connection.On<string>(InvestigationHub.EventMessage, json =>
        {
            var e = System.Text.Json.JsonSerializer.Deserialize<AgentEvent>(json, AgentJson.Options)!;
            received.TryAdd(e.Sequence, e);
            onLive?.Invoke(e);
        });
        return connection;
    }

    private static async Task<long> JoinAsync(HubConnection connection, Guid caseId, long after, ConcurrentDictionary<long, AgentEvent> received)
    {
        var stored = await connection.InvokeAsync<List<string>>(nameof(InvestigationHub.JoinCase), caseId, after);
        foreach (var json in stored)
        {
            var e = System.Text.Json.JsonSerializer.Deserialize<AgentEvent>(json, AgentJson.Options)!;
            received.TryAdd(e.Sequence, e);
        }

        return received.IsEmpty ? after : received.Keys.Max();
    }

    [Fact]
    public async Task Join_replays_stored_events_then_follows_live_and_resumes_after_reconnect()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        var received = new ConcurrentDictionary<long, AgentEvent>();

        // First connection: join while the run streams, take a few live events, then drop.
        var firstLive = new TaskCompletionSource();
        await using (var first = Connect(received, _ => firstLive.TrySetResult()))
        {
            await first.StartAsync();
            await JoinAsync(first, caseId, 0, received);
            await firstLive.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        // Second connection resumes from the last sequence it holds.
        var last = received.Keys.Max();
        await using var second = Connect(received);
        await second.StartAsync();
        await JoinAsync(second, caseId, last, received);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval, 30_000);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var stored = await factory.Investigations.GetEventsAsync(caseId, 0, default);
        while (received.Count < stored.Count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        // Nothing lost: every stored sequence arrived, through replay or live; the dictionary drops duplicates.
        Assert.Equal(stored.Select(s => s.Sequence), received.Keys.Order());
        EventAssertions.AssertWellFormedRun(received.Values.OrderBy(e => e.Sequence).ToList(), expectTerminal: false);
    }
}

/// <summary>A short ping interval so the keep-alive shows within a test.</summary>
public class PingSurveillanceFactory : SurveillanceFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Transport:SsePingSeconds", "1");
        builder.UseSetting("Lab:SyncTimeoutSeconds", "2");
    }
}

public partial class SseAndLabTransportTests(PingSurveillanceFactory factory) : IClassFixture<PingSurveillanceFactory>
{
    private static StartInvestigation Input() =>
        new("UNVR", new DateOnly(2026, 11, 24), TriggerKind.Price, "Harga naik di atas 5%", 5.5, null, null, null, null);

    [Fact]
    public async Task Sse_sends_retry_pings_and_no_buffering_header_while_waiting()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        var paused = await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await factory.CreateClient().GetAsync($"/investigations/{caseId}/events?after={paused.LastSequence - 1}",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            lines.Add(line);
            if (line == "event: " + SseKeepAlive.PingEvent && lines.Contains("event: approval_required"))
            {
                break; // the idle ping after the stored events
            }
        }

        // A ping opens the response at once; the stored approval_required carries id and retry;
        // then the idle line gets a ping without an id.
        Assert.Equal("event: " + SseKeepAlive.PingEvent, lines[0]);
        Assert.Contains("event: approval_required", lines);
        Assert.Contains("retry: 3000", lines);
        // The ping frame (from the blank line before it) has no id, so Last-Event-ID does not move.
        var pingFrame = lines.Skip(lines.LastIndexOf("") + 1).ToList();
        Assert.DoesNotContain(pingFrame, l => l.StartsWith("id:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "sse")]
    [InlineData("polling", "polling")]
    [InlineData("signalr", "signalr")]
    [InlineData("carrier-pigeon", "sse")]
    public async Task Details_page_picks_the_transport(string? requested, string expected)
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        var page = await factory.CreateClient().GetStringAsync(
            $"/investigations/{caseId}" + (requested is null ? "" : "?transport=" + requested));

        Assert.Contains($"data-transport=\"{expected}\"", page);
        Assert.Equal(expected == "signalr", page.Contains("signalr.min.js"));
        Assert.Contains("tetap berjalan di server walau tab ini ditutup", page);
    }

    [Fact]
    public async Task Polling_returns_only_events_after_the_given_sequence()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        var paused = await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);

        var json = await factory.CreateClient().GetStringAsync($"/investigations/{caseId}/events.json?after={paused.LastSequence - 2}");
        var events = System.Text.Json.JsonSerializer.Deserialize<List<AgentEvent>>(json, AgentJson.Options)!;

        Assert.Equal([paused.LastSequence - 1, paused.LastSequence], events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Lab_pages_follow_the_active_solution()
    {
        var selection = (AgentSelection)factory.Services.GetService(typeof(AgentSelection))!;
        var client = factory.CreateClient();
        try
        {
            await selection.SetActiveAsync("maf", default);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/lab")).StatusCode);
            Assert.DoesNotContain("Halaman demo (Lab)", await client.GetStringAsync("/investigations"));

            await selection.SetActiveAsync("fake", default);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/lab")).StatusCode);
            Assert.Contains("Halaman demo (Lab)", await client.GetStringAsync("/investigations"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/lab/transport")).StatusCode);
        }
        finally
        {
            await selection.SetActiveAsync("fake", default);
        }
    }

    [Fact]
    public async Task Lab_sync_answers_after_the_run_pauses_and_times_out_when_slow()
    {
        var client = factory.CreateClient();
        var token = AntiforgeryToken().Match(await client.GetStringAsync("/lab/sync")).Groups[1].Value;

        var quick = await client.PostAsync("/lab/sync", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, quick.StatusCode);
        Assert.Contains("Jawaban datang setelah", await quick.Content.ReadAsStringAsync());

        // 3 seconds per tool against a 2 second limit: the gateway gives up, the run does not.
        var slow = await client.PostAsync("/lab/sync", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["slow"] = "true",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.GatewayTimeout, slow.StatusCode);
        Assert.Contains("Permintaan terlalu lama", await slow.Content.ReadAsStringAsync());
        var running = (await factory.Investigations.ListCasesAsync(default)).First(c => c.Notes == "scenario=slow");
        Assert.False(running.Status.IsTerminal());
        await factory.Investigations.StopAsync(running.Id, default);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
