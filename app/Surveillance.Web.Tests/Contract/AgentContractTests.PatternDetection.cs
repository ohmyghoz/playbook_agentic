using Surveillance.Agents.Abstractions;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests.Contract;

/// <summary>
/// The use case 2 contract (asd-pattern-detection.md, CHANGE-002), run unchanged against every
/// adapter. The scripted models play workers and the report writer by role (scenarios.json,
/// pattern_*): stock workers find broker YP dominant in ADRO and PTBA, the broker worker finds
/// the lead ITMG, and the orchestrator forms one cluster. The fake plays its own script under
/// the same rules. Results are summarized in contracts/COMPATIBILITY.md.
/// </summary>
public abstract partial class AgentContractTests
{
    private const string Orchestrator = "orchestrator";
    private static readonly HashSet<string> NonMcpTools = ["finish_worker", "submit_report"];

    protected static InvestigationRequest PatternRequest(int maxToolCalls = 150, string first = "ADRO", string? notes = null)
    {
        var job = new PatternJob(new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 24),
            [
                new PatternCandidate(first, 21.5, "Harga naik 21,5% dalam 10 hari bursa."),
                new PatternCandidate("PTBA", 17.2, "Harga naik 17,2% dalam 10 hari bursa."),
            ],
            new PatternLimits(MaxDepth: 2, MaxStocks: 15, BrokersPerStock: 3, MaxStepsPerWorker: 12, MaxToolCalls: maxToolCalls,
                MaxMinutes: 20, MinTopBuyerSharePct: 40, MaxBrokerSpreadPct: 30));
        return new InvestigationRequest(Guid.NewGuid().ToString(), first, job.WindowEnd,
            new AnomalyTrigger(TriggerKind.Price, "Perubahan harga 10 hari bursa minimal 15%", 21.5), notes,
            UseCase.PatternDetection, job);
    }

    private async Task<(string RunId, List<AgentEvent> Events)> RunPatternUntilApprovalAsync(int maxToolCalls = 150)
    {
        var runId = await Client.StartAsync(PatternRequest(maxToolCalls), default);
        var events = await Drain(Client, runId);
        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        return (runId, events);
    }

    private static List<WorkerFinishedPayload> FinishedWorkers(List<AgentEvent> events) =>
        events.Where(e => e.Type == AgentEventType.WorkerFinished).Select(e => e.PayloadAs<WorkerFinishedPayload>()).ToList();

    [Fact]
    public async Task Pattern_job_runs_workers_and_pauses_for_the_report()
    {
        var (_, events) = await RunPatternUntilApprovalAsync();

        Assert.Equal(UseCase.PatternDetection, events[0].PayloadAs<RunStartedPayload>().UseCase);
        var started = events.Where(e => e.Type == AgentEventType.WorkerStarted).Select(e => e.PayloadAs<WorkerStartedPayload>()).ToList();
        Assert.Contains(started, w => w is { Role: WorkerRole.Stock, Target: "ADRO", Depth: 0 });
        Assert.Contains(started, w => w is { Role: WorkerRole.Stock, Target: "PTBA", Depth: 0 });
        Assert.Contains(started, w => w is { Role: WorkerRole.Broker, Target: "YP", Depth: 1 });
        Assert.Contains(started, w => w is { Role: WorkerRole.Stock, Depth: 1 }); // the broker worker's lead
        Assert.Contains(started, w => w.Role == WorkerRole.Ownership);
        Assert.All(FinishedWorkers(events), w => Assert.Equal(WorkerStatus.Ok, w.Status));

        // Every MCP call belongs to a worker; the orchestrator only delegates.
        Assert.All(events.Where(e => e.Type == AgentEventType.ToolCallStarted
                                     && !NonMcpTools.Contains(e.PayloadAs<ToolCallStartedPayload>().ToolName)),
            e => Assert.NotEqual(Orchestrator, e.AgentName));

        var approval = events[^1].PayloadAs<ApprovalRequiredPayload>();
        Assert.Null(approval.Memo);
        var report = Assert.IsType<PatternReport>(approval.Report);
        Assert.Equal(0, approval.Revision);
        Assert.Equal(Memo.StandardDisclaimer, report.Disclaimer);
        var cluster = Assert.Single(report.Clusters);
        Assert.Contains("YP", cluster.DominantBrokers);
        Assert.Contains("ADRO", cluster.Stocks);
        Assert.Contains("PTBA", cluster.Stocks);
        Assert.Empty(report.LimitsReached);
    }

    [Fact]
    public async Task Pattern_approve_ends_with_done_approved_and_the_report()
    {
        var (runId, events) = await RunPatternUntilApprovalAsync();

        await Client.ResumeAsync(runId, Decide(events, Decision.Approve), default);
        events.AddRange(await Drain(Client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        var done = events[^1].PayloadAs<DonePayload>();
        Assert.Equal(RunOutcome.Approved, done.Outcome);
        Assert.NotNull(done.Report);
        Assert.Null(done.Memo);
        Assert.Equal(Orchestrator, events[^1].AgentName);
    }

    [Fact]
    public async Task Pattern_reject_ends_with_done_rejected()
    {
        var (runId, events) = await RunPatternUntilApprovalAsync();

        await Client.ResumeAsync(runId, Decide(events, Decision.Reject, "Bukti belum cukup."), default);
        events.AddRange(await Drain(Client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Rejected, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    [Fact]
    public async Task Pattern_revise_produces_a_new_report_without_rerunning_workers()
    {
        var (runId, events) = await RunPatternUntilApprovalAsync();
        var workers = events.Count(e => e.Type == AgentEventType.WorkerStarted);

        await Client.ResumeAsync(runId, Decide(events, Decision.Revise, "Perjelas kronologi."), default);
        events.AddRange(await Drain(Client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        var approval = events[^1].PayloadAs<ApprovalRequiredPayload>();
        Assert.Equal(1, approval.Revision);
        Assert.NotNull(approval.Report);
        Assert.Equal(workers, events.Count(e => e.Type == AgentEventType.WorkerStarted));

        await Client.ResumeAsync(runId, Decide(events, Decision.Approve), default);
        events.AddRange(await Drain(Client, runId));
        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Approved, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    /// <summary>
    /// A job budget of one tool call: the job does not fail. Workers that hit the budget end
    /// limit_reached, the report records the limit, and the run still stops at approval.
    /// </summary>
    [Fact]
    public async Task Pattern_budget_is_kept_and_yields_a_partial_report()
    {
        var (runId, events) = await RunPatternUntilApprovalAsync(maxToolCalls: 1);

        var executed = events.Count(e => e.Type == AgentEventType.ToolCallFinished
                                         && e.PayloadAs<ToolCallFinishedPayload>() is { Status: ToolCallStatus.Ok } f
                                         && !NonMcpTools.Contains(f.ToolName));
        Assert.InRange(executed, 1, 1);

        // A worker that was already running ends limit_reached; one that had not started yet is not
        // run and is listed as not followed. Which happens depends on how the workers interleave.
        var report = events[^1].PayloadAs<ApprovalRequiredPayload>().Report!;
        Assert.Contains(report.LimitsReached, l => l.Contains("1 panggilan data", StringComparison.Ordinal));
        Assert.True(FinishedWorkers(events).Any(w => w.Status == WorkerStatus.LimitReached)
                    || report.LeadsNotFollowed.Any(l => l.Reason.Contains("1 panggilan data", StringComparison.Ordinal)),
            "Neither a limit_reached worker nor a lead skipped for the budget.");

        events.AddRange(await Drain(Client, runId, new CancellationToken(canceled: true)));
        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    /// <summary>Stop while workers run: every open worker is closed as cancelled before done(cancelled).</summary>
    [Fact]
    public async Task Pattern_cancel_while_workers_run_closes_every_worker()
    {
        // 'SLOW' makes the scripted stock worker wait 3 s per turn; 'scenario=slow' slows the fake's tools.
        var runId = await Client.StartAsync(PatternRequest(first: "SLOW", notes: "scenario=slow"), default);
        using var stop = new CancellationTokenSource();
        var events = new List<AgentEvent>();
        await foreach (var e in Client.StreamAsync(runId, stop.Token))
        {
            events.Add(e);
            if (e.Type == AgentEventType.WorkerStarted && !stop.IsCancellationRequested)
            {
                stop.CancelAfter(TimeSpan.FromMilliseconds(300));
            }
        }

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
        Assert.Contains(FinishedWorkers(events), w => w.Status == WorkerStatus.Cancelled);
        Assert.DoesNotContain(events, e => e.Type == AgentEventType.ApprovalRequired);
    }

    [Fact]
    public async Task Pattern_cancel_while_paused_ends_cancelled()
    {
        var (runId, events) = await RunPatternUntilApprovalAsync();

        events.AddRange(await Drain(Client, runId, new CancellationToken(canceled: true)));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
    }
}
