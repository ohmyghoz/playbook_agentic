using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Surveillance.Agents.Abstractions;
using Surveillance.Agents.Maf;
using Surveillance.Web.Tests.Support;
using static Surveillance.Web.Tests.Maf.ScriptedChatClient;

namespace Surveillance.Web.Tests.Maf;

public sealed class MafAgentClientTests : IDisposable
{
    private const string QuoteJson = """{"schema":"idx.quotes.v1","provider":"yahoo","data":[{"symbol":"BBCA","price":5800.0,"change_pct":2.2026,"volume":235635300,"avg_volume_3m":212048028}]}""";
    private const string HistoryJson = """{"schema":"idx.history.v1","data":{"summary":{"candles":66,"avg_volume":210424313,"low":4740.0,"high":6850.0}}}""";
    private const string StatusJson = """{"schema":"idx.stockbit.status.v1","data":{"logged_in":true}}""";
    private const string BandarErrorJson = """{"error":{"code":"provider","message":"ipot: broker summary for BBCA: HTTP 502"},"exit_code":3}""";

    private static readonly InvestigationRequest Request = new(
        "case-1", "BBCA", new DateOnly(2026, 11, 24),
        new AnomalyTrigger(TriggerKind.Volume, "Volume di atas 1,1x rata-rata 3 bulan", 2.2, 235_635_300, 210_424_313, 1.12));

    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), $"maf-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_stateDir))
        {
            Directory.Delete(_stateDir, recursive: true);
        }
    }

    private MafAgentClient Client(IChatClient chat, int maxToolCalls = 15, IToolSource? tools = null) =>
        new(new MafAgentOptions { StateDirectory = _stateDir, MaxToolCalls = maxToolCalls }, chat, tools ?? Tools());

    private static FakeToolSource Tools() => new(
        new CannedTool("idx_stockbit_status", StatusJson),
        new CannedTool("idx_quote", QuoteJson),
        new CannedTool("idx_history", HistoryJson),
        new CannedTool("idx_bandar", BandarErrorJson));

    private static object MemoArgs(params string[] evidenceIds) => new
    {
        memo = JsonSerializer.SerializeToElement(new Memo(
            "BBCA", new DateOnly(2026, 11, 24), "Volume di atas rata-rata tanpa berita material.",
            new AnomalyFacts("Volume 1,1x rata-rata 3 bulan.", 2.2, 235_635_300, 212_048_028, 1.11),
            [new Finding(FindingCategory.PriceVolume, "Volume di atas rata-rata.",
                evidenceIds.Select(id => new Evidence(id, "idx_quote", "volume")).ToList())],
            [new UnavailableData("idx_bandar", "IPOT HTTP 502")],
            EvidenceLevel.Weak, "Penyimpangan kecil.", Recommendation.Monitor, "Pantau sesi berikutnya.",
            Disclaimer: "parafrase"), AgentJson.Options),
    };

    /// <summary>Tool calls, then a memo citing the quote call: the usual first stretch of a run.</summary>
    private static Func<IList<ChatMessage>, IEnumerable<AIContent>>[] UntilMemo() =>
    [
        _ => [Text("Memeriksa sesi Stockbit. "), Call("c1", "idx_stockbit_status")],
        _ => [Call("c2", "idx_quote", new { symbols = "BBCA" }), Call("c3", "idx_history", new { symbol = "BBCA" })],
        _ => [Text("Menyusun memo."), Call("m1", "submit_memo", MemoArgs("c2"))],
    ];

    private static async Task<List<AgentEvent>> Drain(IAgentClient client, string runId, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in client.StreamAsync(runId, ct))
        {
            events.Add(e);
        }

        return events;
    }

    private static string ApprovalId(List<AgentEvent> events) =>
        events[^1].PayloadAs<ApprovalRequiredPayload>().ApprovalId;

    [Fact]
    public async Task Run_pauses_at_submit_memo_and_approve_ends_with_done()
    {
        var chat = new ScriptedChatClient([.. UntilMemo(), _ => [Text("Memo disetujui, terima kasih.")]]);
        var client = Client(chat);
        var runId = await client.StartAsync(Request, default);

        var events = await Drain(client, runId);
        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);

        var finished = events.Where(e => e.Type == AgentEventType.ToolCallFinished).Select(e => e.PayloadAs<ToolCallFinishedPayload>()).ToList();
        Assert.Contains(finished, f => f.ToolName == "idx_quote" && f.Summary.StartsWith("Harga 5.800 (+2,2%)"));
        Assert.Contains(finished, f => f.ToolName == "idx_history" && f.Summary.Contains("66 sesi"));
        var approval = events[^1].PayloadAs<ApprovalRequiredPayload>();
        Assert.Equal(0, approval.Revision);
        Assert.Equal(Memo.StandardDisclaimer, approval.Memo.Disclaimer);
        Assert.Contains(events, e => e.Type == AgentEventType.Token);

        // The model saw the call id it must cite as evidence.
        Assert.Contains(chat.Requests[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => ToolResultSummarizer.ResultText(r.Result).StartsWith("[toolCallId: c2]"));

        await client.ResumeAsync(runId, new ApprovalDecision(approval.ApprovalId, Decision.Approve, "analis"), default);
        events.AddRange(await Drain(client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        var done = events[^1].PayloadAs<DonePayload>();
        Assert.Equal(RunOutcome.Approved, done.Outcome);
        Assert.Equal(3, done.ToolCallsUsed);
        var submitted = events.Where(e => e.Type == AgentEventType.ToolCallFinished)
            .Select(e => e.PayloadAs<ToolCallFinishedPayload>()).Single(f => f.ToolCallId == "m1");
        Assert.Equal(ToolCallStatus.Ok, submitted.Status);
        Assert.Equal("Memo disetujui analis.", submitted.Summary);
        Assert.Equal(1, chat.RemainingTurns); // no closing reply was requested after approval
    }

    [Fact]
    public async Task Reject_ends_without_calling_the_model_again()
    {
        var chat = new ScriptedChatClient(UntilMemo());
        var client = Client(chat);
        var runId = await client.StartAsync(Request, default);
        var events = await Drain(client, runId);
        var requestsBefore = chat.Requests.Count;

        await client.ResumeAsync(runId, new ApprovalDecision(ApprovalId(events), Decision.Reject, "analis", "Bukti belum cukup."), default);
        events.AddRange(await Drain(client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Rejected, events[^1].PayloadAs<DonePayload>().Outcome);
        Assert.Equal(requestsBefore, chat.Requests.Count);
    }

    [Fact]
    public async Task Revise_sends_the_comment_to_the_model_and_produces_a_new_draft()
    {
        var chat = new ScriptedChatClient(
        [
            .. UntilMemo(),
            msgs => [Call("m2", "submit_memo", MemoArgs("c2", "c3"))],
        ]);
        var client = Client(chat);
        var runId = await client.StartAsync(Request, default);
        var events = await Drain(client, runId);

        await client.ResumeAsync(runId, new ApprovalDecision(ApprovalId(events), Decision.Revise, "analis", "Tambahkan riwayat."), default);
        events.AddRange(await Drain(client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        Assert.Equal(1, events[^1].PayloadAs<ApprovalRequiredPayload>().Revision);
        var reply = chat.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "m1");
        Assert.Contains("Analis meminta revisi: Tambahkan riwayat.", ToolResultSummarizer.ResultText(reply.Result));
    }

    [Fact]
    public async Task Invalid_memo_is_returned_to_the_model_and_corrected()
    {
        var chat = new ScriptedChatClient(
            _ => [Call("c2", "idx_quote", new { symbols = "BBCA" })],
            _ => [Call("m1", "submit_memo", MemoArgs("call_tidak_ada"))],
            _ => [Call("m2", "submit_memo", MemoArgs("c2"))]);
        var client = Client(chat);
        var runId = await client.StartAsync(Request, default);

        var events = await Drain(client, runId);

        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        var rejected = events.Select(e => e.Type == AgentEventType.ToolCallFinished ? e.PayloadAs<ToolCallFinishedPayload>() : null)
            .Single(f => f?.ToolCallId == "m1")!;
        Assert.Equal("invalid_memo", rejected.Error!.Code);
        Assert.Contains("call_tidak_ada", rejected.Error.Message);
    }

    [Fact]
    public async Task Failed_tool_is_reported_as_error_and_run_continues()
    {
        var chat = new ScriptedChatClient(
            _ => [Call("c2", "idx_quote"), Call("c9", "idx_bandar", new { symbols = "BBCA" })],
            _ => [Call("m1", "submit_memo", MemoArgs("c2"))]);
        var client = Client(chat);
        var runId = await client.StartAsync(Request, default);

        var events = await Drain(client, runId);

        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        var bandar = events.Where(e => e.Type == AgentEventType.ToolCallFinished)
            .Select(e => e.PayloadAs<ToolCallFinishedPayload>()).Single(f => f.ToolName == "idx_bandar");
        Assert.Equal(ToolCallStatus.Error, bandar.Status);
        Assert.Equal("provider", bandar.Error!.Code);
    }

    [Fact]
    public async Task Step_limit_warns_once_then_ends_with_error()
    {
        var n = 0;
        var chat = new ScriptedChatClient { Fallback = _ => [Call($"q{++n}", "idx_quote")] };
        var client = Client(chat, maxToolCalls: 3);
        var runId = await client.StartAsync(Request, default);

        var events = await Drain(client, runId);

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(AgentErrorCode.StepLimitExceeded, events[^1].PayloadAs<ErrorPayload>().Code);
        var finished = events.Where(e => e.Type == AgentEventType.ToolCallFinished).Select(e => e.PayloadAs<ToolCallFinishedPayload>()).ToList();
        Assert.Equal(3, finished.Count(f => f.Status == ToolCallStatus.Ok));
        Assert.All(finished.Skip(3), f => Assert.Equal("step_limit", f.Error!.Code));
    }

    [Fact]
    public async Task Parallel_calls_past_the_limit_get_a_warning_and_the_model_can_still_submit()
    {
        var chat = new ScriptedChatClient(
            _ => [Call("c1", "idx_quote"), Call("c2", "idx_history"), Call("c3", "idx_stockbit_status"), Call("c4", "idx_quote")],
            _ => [Call("m1", "submit_memo", MemoArgs("c1"))]);
        var client = Client(chat, maxToolCalls: 2);
        var runId = await client.StartAsync(Request, default);

        var events = await Drain(client, runId);

        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        var finished = events.Where(e => e.Type == AgentEventType.ToolCallFinished).Select(e => e.PayloadAs<ToolCallFinishedPayload>()).ToList();
        Assert.Equal(2, finished.Count(f => f.Status == ToolCallStatus.Ok));
        Assert.Equal(2, finished.Count(f => f.Error?.Code == "step_limit"));
    }

    [Fact]
    public async Task Cancel_during_model_stream_ends_cancelled()
    {
        var chat = new ScriptedChatClient(_ => [Text("Satu "), Text("dua "), Text("tiga")]) { Delay = TimeSpan.FromMilliseconds(300) };
        var client = Client(chat);
        var runId = await client.StartAsync(Request, default);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(450));

        var events = await Drain(client, runId, cts.Token);

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    [Fact]
    public async Task Cancel_while_paused_closes_submit_memo()
    {
        var client = Client(new ScriptedChatClient(UntilMemo()));
        var runId = await client.StartAsync(Request, default);
        var events = await Drain(client, runId);

        events.AddRange(await Drain(client, runId, new CancellationToken(canceled: true)));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
        Assert.Equal("cancelled", events[^2].PayloadAs<ToolCallFinishedPayload>().Error!.Code);
    }

    [Fact]
    public async Task Approval_survives_a_new_client_instance()
    {
        var chat = new ScriptedChatClient([.. UntilMemo(), _ => [Text("Selesai.")]]);
        var first = Client(chat);
        var runId = await first.StartAsync(Request, default);
        var events = await Drain(first, runId);

        // A fresh instance reads the run from disk, as after an application restart.
        var second = Client(chat);
        await second.ResumeAsync(runId, new ApprovalDecision(ApprovalId(events), Decision.Approve, "analis"), default);
        events.AddRange(await Drain(second, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Approved, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    [Fact]
    public async Task Stale_approval_id_is_rejected()
    {
        var client = Client(new ScriptedChatClient(UntilMemo()));
        var runId = await client.StartAsync(Request, default);
        await Drain(client, runId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ResumeAsync(runId, new ApprovalDecision("approval_9", Decision.Approve, "analis"), default));
    }

    [Fact]
    public async Task Mcp_unavailable_ends_with_retryable_error()
    {
        var client = Client(new ScriptedChatClient(), tools: new FakeToolSource { Unavailable = true });
        var runId = await client.StartAsync(Request, default);

        var events = await Drain(client, runId);

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        var error = events[^1].PayloadAs<ErrorPayload>();
        Assert.Equal(AgentErrorCode.McpUnavailable, error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task Model_failure_ends_with_model_error()
    {
        var chat = new ScriptedChatClient(_ => throw new ClientResultException("rate limited"));
        var client = Client(chat);
        var runId = await client.StartAsync(Request, default);

        var events = await Drain(client, runId);

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(AgentErrorCode.ModelError, events[^1].PayloadAs<ErrorPayload>().Code);
    }
}
