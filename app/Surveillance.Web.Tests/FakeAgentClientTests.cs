using Microsoft.Extensions.Options;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Agents.Fake;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

public class FakeAgentClientTests
{
    private static readonly InvestigationRequest Request = new(
        "case-1", "BBCA", new DateOnly(2026, 11, 24),
        new AnomalyTrigger(TriggerKind.Volume, "Volume di atas 1,1x rata-rata 3 bulan", 2.2, 235_635_300, 210_424_313, 1.12));

    private static FakeAgentClient CreateClient(FakeScenario scenario = FakeScenario.Happy, int toolDelayMs = 0) =>
        new(Options.Create(new FakeAgentOptions { Scenario = scenario, ToolDelayMs = toolDelayMs, TokenDelayMs = 0 }));

    private static async Task<List<AgentEvent>> Drain(IAgentClient client, string runId, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in client.StreamAsync(runId, ct))
        {
            events.Add(e);
        }

        return events;
    }

    private static ApprovalDecision Decide(AgentEvent approval, Decision decision, string? comment = null) =>
        new(approval.PayloadAs<ApprovalRequiredPayload>().ApprovalId, decision, "analis-uji", comment);

    [Fact]
    public async Task Happy_path_pauses_at_approval_then_approve_ends_with_done()
    {
        var client = CreateClient();
        var runId = await client.StartAsync(Request, default);

        var events = await Drain(client, runId);
        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        Assert.Empty(await Drain(client, runId)); // paused: nothing new until resumed

        var started = events[0].PayloadAs<RunStartedPayload>();
        Assert.Equal(15, started.MaxToolCalls);
        Assert.Contains(events, e => e.Type == AgentEventType.Token);
        Assert.Contains(events, e => e.Type == AgentEventType.ToolCallFinished
                                     && e.PayloadAs<ToolCallFinishedPayload>().Status == ToolCallStatus.Error);

        await client.ResumeAsync(runId, Decide(events[^1], Decision.Approve), default);
        events.AddRange(await Drain(client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        var done = events[^1].PayloadAs<DonePayload>();
        Assert.Equal(RunOutcome.Approved, done.Outcome);
        Assert.NotNull(done.Memo);
        Assert.Equal(Memo.StandardDisclaimer, done.Memo!.Disclaimer);
        Assert.InRange(done.ToolCallsUsed, 1, FakeAgentClient.MaxToolCalls);
    }

    [Fact]
    public async Task Reject_ends_with_rejected_outcome()
    {
        var client = CreateClient();
        var runId = await client.StartAsync(Request, default);
        var events = await Drain(client, runId);

        await client.ResumeAsync(runId, Decide(events[^1], Decision.Reject, "Bukti belum cukup."), default);
        events.AddRange(await Drain(client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Rejected, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    [Fact]
    public async Task Revise_produces_new_draft_and_third_revision_fails()
    {
        var client = CreateClient();
        var runId = await client.StartAsync(Request, default);
        var events = await Drain(client, runId);

        for (var expected = 1; expected <= FakeAgentClient.MaxRevisions; expected++)
        {
            await client.ResumeAsync(runId, Decide(events[^1], Decision.Revise, "Tambahkan pola asing 20 sesi."), default);
            events.AddRange(await Drain(client, runId));
            EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
            Assert.Equal(expected, events[^1].PayloadAs<ApprovalRequiredPayload>().Revision);
        }

        await client.ResumeAsync(runId, Decide(events[^1], Decision.Revise, "Sekali lagi."), default);
        events.AddRange(await Drain(client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(AgentErrorCode.RevisionLimitExceeded, events[^1].PayloadAs<ErrorPayload>().Code);
    }

    [Fact]
    public async Task Resume_with_stale_approval_id_is_rejected()
    {
        var client = CreateClient();
        var runId = await client.StartAsync(Request, default);
        await Drain(client, runId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ResumeAsync(runId, new ApprovalDecision("approval_old", Decision.Approve, "analis-uji"), default));
    }

    [Fact]
    public async Task Cancel_during_tool_call_closes_the_call_and_ends_cancelled()
    {
        var client = CreateClient(toolDelayMs: 5_000);
        var runId = await client.StartAsync(Request, default);
        using var cts = new CancellationTokenSource();

        var events = new List<AgentEvent>();
        await foreach (var e in client.StreamAsync(runId, cts.Token))
        {
            events.Add(e);
            if (e.Type == AgentEventType.ToolCallStarted)
            {
                cts.Cancel();
            }
        }

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
        var closed = events[^2].PayloadAs<ToolCallFinishedPayload>();
        Assert.Equal(ToolCallStatus.Error, closed.Status);
        Assert.Equal("cancelled", closed.Error!.Code);
    }

    [Fact]
    public async Task Cancel_while_paused_closes_submit_memo_and_ends_cancelled()
    {
        var client = CreateClient();
        var runId = await client.StartAsync(Request, default);
        var events = await Drain(client, runId);

        events.AddRange(await Drain(client, runId, new CancellationToken(canceled: true)));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
        Assert.Equal("submit_memo", events[^2].PayloadAs<ToolCallFinishedPayload>().ToolName);
    }

    [Fact]
    public async Task Model_error_scenario_ends_with_retryable_error()
    {
        var client = CreateClient(FakeScenario.ModelError);
        var runId = await client.StartAsync(Request, default);
        var events = await Drain(client, runId);

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        var error = events[^1].PayloadAs<ErrorPayload>();
        Assert.Equal(AgentErrorCode.ModelError, error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task Step_limit_scenario_stops_at_fifteen_tool_calls()
    {
        var client = CreateClient(FakeScenario.StepLimit);
        var runId = await client.StartAsync(Request, default);
        var events = await Drain(client, runId);

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(AgentErrorCode.StepLimitExceeded, events[^1].PayloadAs<ErrorPayload>().Code);
        Assert.Equal(FakeAgentClient.MaxToolCalls, events.Count(e => e.Type == AgentEventType.ToolCallStarted));
    }
}
