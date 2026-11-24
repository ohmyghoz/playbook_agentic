using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Agents.Http;

namespace Surveillance.Web.Tests.Http;

/// <summary>The client side of contracts/agent-service-api.md against a scripted service.
/// LangGraphAgentClient and AdkAgentClient share this code; the tests run against both.</summary>
public class HttpSseAgentClientTests
{
    private static readonly InvestigationRequest Request = new(
        "case-1", "BBCA", new DateOnly(2026, 11, 24), new AnomalyTrigger(TriggerKind.Volume, "Volume di atas 1,1x"));

    private sealed class ScriptedService(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Calls)
            {
                Calls.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
            }

            return respond(request, body);
        }
    }

    public static TheoryData<string> Providers => new() { "langgraph", "adk" };

    private static HttpSseAgentClient Client(string provider, ScriptedService service)
    {
        var http = new HttpClient(service) { BaseAddress = new Uri("http://127.0.0.1:8101/") };
        return provider == "adk"
            ? new AdkAgentClient(http, NullLogger<AdkAgentClient>.Instance)
            : new LangGraphAgentClient(http, NullLogger<LangGraphAgentClient>.Instance);
    }

    private static string Frame(long sequence, AgentEventType type, object payload)
    {
        var e = AgentEvent.Create("lg-1", sequence, type, payload);
        return $"id: {sequence}\nevent: {e.ToJson().Split("\"type\":\"")[1].Split('"')[0]}\ndata: {e.ToJson()}\n\n";
    }

    private static HttpResponseMessage Sse(params string[] frames) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(frames), Encoding.UTF8, "text/event-stream"),
    };

    private static readonly string Started = Frame(1, AgentEventType.RunStarted, new RunStartedPayload("case-1", "BBCA", 15));
    private static readonly string Token = Frame(2, AgentEventType.Token, new TokenPayload("Memeriksa. "));
    private static readonly string Approval = Frame(3, AgentEventType.ApprovalRequired, new ApprovalRequiredPayload("approval_1", 0,
        new Memo("BBCA", new DateOnly(2026, 11, 24), "s", new AnomalyFacts("d"), [], [], EvidenceLevel.Weak, "r", Recommendation.Monitor, "r")));
    private static readonly string Done = Frame(4, AgentEventType.Done, new DonePayload(RunOutcome.Cancelled, 0));

    private static async Task<List<AgentEvent>> Drain(IAgentClient client, string runId, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in client.StreamAsync(runId, ct))
        {
            events.Add(e);
        }

        return events;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Start_posts_the_request_in_wire_format(string provider)
    {
        var service = new ScriptedService((_, _) => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"runId":"lg-1"}""", Encoding.UTF8, "application/json"),
        });

        var runId = await Client(provider, service).StartAsync(Request, default);

        Assert.Equal("lg-1", runId);
        var (method, path, body) = Assert.Single(service.Calls);
        Assert.Equal(("POST", "/runs"), (method, path));
        using var json = JsonDocument.Parse(body);
        Assert.Equal("2026-11-24", json.RootElement.GetProperty("anomalyDate").GetString());
        Assert.Equal("volume", json.RootElement.GetProperty("trigger").GetProperty("kind").GetString());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Stream_reads_until_pause_and_continues_after_the_last_sequence(string provider)
    {
        var service = new ScriptedService((req, _) => req.RequestUri!.Query.Contains("after=0")
            ? Sse(Started, Token, Approval)
            : Sse(Done));
        var client = Client(provider, service);

        var first = await Drain(client, "lg-1");
        var second = await Drain(client, "lg-1");

        Assert.Equal([1L, 2, 3], first.Select(e => e.Sequence));
        Assert.Equal(AgentEventType.ApprovalRequired, first[^1].Type);
        Assert.Equal([4L], second.Select(e => e.Sequence));
        Assert.Contains(service.Calls, c => c.Path == "/runs/lg-1/events?after=3");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Dropped_stream_reconnects_from_the_last_sequence_and_skips_duplicates(string provider)
    {
        var attempt = 0;
        var service = new ScriptedService((_, _) => ++attempt == 1 ? Sse(Started, Token) : Sse(Token, Approval));

        var events = await Drain(Client(provider, service), "lg-1");

        Assert.Equal([1L, 2, 3], events.Select(e => e.Sequence));
        Assert.Equal("/runs/lg-1/events?after=2", service.Calls[^1].Path);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Cancelling_posts_cancel_and_reads_until_done(string provider)
    {
        var service = new ScriptedService((req, _) => req.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{}") }
            : Sse(Done));

        var events = await Drain(Client(provider, service), "lg-1", new CancellationToken(canceled: true));

        Assert.Contains(service.Calls, c => c is ("POST", "/runs/lg-1/cancel", _));
        Assert.Equal(RunOutcome.Cancelled, Assert.Single(events).PayloadAs<DonePayload>().Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Resume_conflict_becomes_invalid_operation(string provider)
    {
        var service = new ScriptedService((_, _) => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{"error":{"code":"stale_approval","message":"x"}}"""),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(provider, service).ResumeAsync("lg-1", new ApprovalDecision("approval_0", Decision.Approve, "analis"), default));
    }
}
