using Surveillance.Agents.Abstractions;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests.Contract;

/// <summary>
/// The IAgentClient contract, run unchanged against every adapter. Scenarios come from
/// scenarios.json through 'scenario=&lt;name&gt;' in the analyst notes, so each adapter's scripted
/// model plays the same turns. Results are summarized in contracts/COMPATIBILITY.md.
/// </summary>
public abstract class AgentContractTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    protected abstract IAgentClient Client { get; }

    protected static InvestigationRequest Request(string? scenario) => new(
        Guid.NewGuid().ToString(), "BBCA", new DateOnly(2026, 11, 24),
        new AnomalyTrigger(TriggerKind.Volume, "Volume di atas 1,1x rata-rata 3 bulan", 2.2, 235_635_300, 210_424_313, 1.12),
        scenario is null ? null : $"scenario={scenario}");

    protected static async Task<List<AgentEvent>> Drain(IAgentClient client, string runId, CancellationToken stop = default)
    {
        using var timeout = new CancellationTokenSource(RunTimeout);
        var events = new List<AgentEvent>();
        var stream = client.StreamAsync(runId, stop).GetAsyncEnumerator();
        try
        {
            while (await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
            {
                events.Add(stream.Current);
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }

        return events;
    }

    protected async Task<(string RunId, List<AgentEvent> Events)> RunUntilApprovalAsync(string scenario = "approve")
    {
        var runId = await Client.StartAsync(Request(scenario), default);
        var events = await Drain(Client, runId);
        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        return (runId, events);
    }

    protected static ApprovalDecision Decide(List<AgentEvent> events, Decision decision, string? comment = null) =>
        new(events[^1].PayloadAs<ApprovalRequiredPayload>().ApprovalId, decision, "contract-test", comment);

    [Fact]
    public async Task Approve_pauses_then_ends_with_done_approved()
    {
        var (runId, events) = await RunUntilApprovalAsync();
        var approval = events[^1].PayloadAs<ApprovalRequiredPayload>();
        Assert.Equal(0, approval.Revision);
        Assert.Equal(Memo.StandardDisclaimer, approval.Memo.Disclaimer);
        Assert.Contains(events, e => e.Type == AgentEventType.ToolCallFinished
                                     && e.PayloadAs<ToolCallFinishedPayload>().Status == ToolCallStatus.Error);

        await Client.ResumeAsync(runId, Decide(events, Decision.Approve), default);
        events.AddRange(await Drain(Client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        var done = events[^1].PayloadAs<DonePayload>();
        Assert.Equal(RunOutcome.Approved, done.Outcome);
        Assert.NotNull(done.Memo);
        Assert.True(done.ToolCallsUsed > 0);
    }

    [Fact]
    public async Task Reject_ends_with_done_rejected()
    {
        var (runId, events) = await RunUntilApprovalAsync();

        await Client.ResumeAsync(runId, Decide(events, Decision.Reject, "Bukti belum cukup."), default);
        events.AddRange(await Drain(Client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Rejected, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    [Fact]
    public async Task Revise_produces_a_new_draft_that_can_be_approved()
    {
        var (runId, events) = await RunUntilApprovalAsync();

        await Client.ResumeAsync(runId, Decide(events, Decision.Revise, "Tambahkan riwayat."), default);
        events.AddRange(await Drain(Client, runId));
        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        Assert.Equal(1, events[^1].PayloadAs<ApprovalRequiredPayload>().Revision);

        await Client.ResumeAsync(runId, Decide(events, Decision.Approve), default);
        events.AddRange(await Drain(Client, runId));
        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Approved, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    [Fact]
    public async Task Third_revision_ends_with_revision_limit()
    {
        var (runId, events) = await RunUntilApprovalAsync();
        for (var i = 0; i < 3; i++)
        {
            await Client.ResumeAsync(runId, Decide(events, Decision.Revise, "Perjelas lagi."), default);
            events.AddRange(await Drain(Client, runId));
        }

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(AgentErrorCode.RevisionLimitExceeded, events[^1].PayloadAs<ErrorPayload>().Code);
    }

    [Fact]
    public async Task Cancel_while_paused_closes_submit_memo_and_ends_cancelled()
    {
        var (runId, events) = await RunUntilApprovalAsync();

        events.AddRange(await Drain(Client, runId, new CancellationToken(canceled: true)));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
        Assert.Equal("cancelled", events[^2].PayloadAs<ToolCallFinishedPayload>().Error!.Code);
    }

    [Fact]
    public async Task Cancel_while_running_ends_cancelled()
    {
        var runId = await Client.StartAsync(Request("slow"), default);
        using var stop = new CancellationTokenSource();
        var events = new List<AgentEvent>();
        await foreach (var e in Client.StreamAsync(runId, stop.Token))
        {
            events.Add(e);
            if (!stop.IsCancellationRequested)
            {
                stop.CancelAfter(TimeSpan.FromMilliseconds(300));
            }
        }

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    [Fact]
    public async Task Step_limit_ends_with_error()
    {
        var runId = await Client.StartAsync(Request("step_limit"), default);
        var events = await Drain(Client, runId);

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(AgentErrorCode.StepLimitExceeded, events[^1].PayloadAs<ErrorPayload>().Code);
        var executed = events.Count(e => e.Type == AgentEventType.ToolCallFinished
                                         && e.PayloadAs<ToolCallFinishedPayload>().Status == ToolCallStatus.Ok);
        Assert.Equal(events[0].PayloadAs<RunStartedPayload>().MaxToolCalls, executed);
    }

    [Fact]
    public async Task Stale_approval_is_rejected()
    {
        var (runId, _) = await RunUntilApprovalAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client.ResumeAsync(runId, new ApprovalDecision("approval_99", Decision.Approve, "contract-test"), default));
    }

    [Fact]
    public async Task Streaming_again_while_paused_yields_nothing_new()
    {
        var (runId, _) = await RunUntilApprovalAsync();

        Assert.Empty(await Drain(Client, runId));
    }
}
