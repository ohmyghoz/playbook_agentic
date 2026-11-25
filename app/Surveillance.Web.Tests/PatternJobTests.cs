using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;
using Surveillance.Web.Hubs;
using Surveillance.Web.Investigations;
using Surveillance.Web.Patterns;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

/// <summary>Screening is ordinary code: tested on its own with canned market data.</summary>
public class CandidateScreeningTests
{
    private static CandidateScreening Screening(PatternDetectionOptions? options = null) =>
        new(new FakeMarketData(), Options.Create(options ?? new PatternDetectionOptions()), NullLogger<CandidateScreening>.Instance);

    [Fact]
    public async Task Keeps_moves_of_at_least_the_threshold_either_way_largest_first()
    {
        var result = await Screening().ScreenAsync(default);

        // ARII moved 11.7% in ten sessions: below 15%. BUMI fell 26.7%: kept.
        Assert.Equal(["VOKS", "PSKT", "BUMI"], result.Candidates.Select(c => c.Symbol));
        Assert.Equal(32.35, result.Candidates[0].PriceChangePct);
        Assert.True(result.Candidates[2].PriceChangePct < -15);
        Assert.Equal(new DateOnly(2026, 11, 24), result.WindowEnd);
        Assert.Equal(new DateOnly(2026, 11, 14), result.WindowStart);
    }

    [Fact]
    public async Task Caps_candidates_and_says_how_many_were_left_out()
    {
        var result = await Screening(new PatternDetectionOptions { MaxCandidates = 1 }).ScreenAsync(default);

        Assert.Equal("VOKS", Assert.Single(result.Candidates).Symbol);
        Assert.Contains(result.Notes, n => n.StartsWith("2 saham lain melewati ambang", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Job_carries_candidates_and_limits_from_configuration()
    {
        var screening = Screening(new PatternDetectionOptions { MaxDepth = 1, MaxCandidates = 3 });
        var job = screening.ToJob(await screening.ScreenAsync(default));

        Assert.Equal(3, job.Candidates.Count);
        Assert.Equal(1, job.Limits.MaxDepth);
        Assert.Equal(150, job.Limits.MaxToolCalls);
        Assert.StartsWith("Harga naik 32,4% dalam 10 hari bursa", job.Candidates[0].Reason);
    }
}

/// <summary>Use case 2 end to end with the fake agent: background job, workers, report approval, notification.</summary>
public partial class PatternJobTests(SurveillanceFactory factory) : IClassFixture<SurveillanceFactory>
{
    private HttpClient Client(string user = "analis1") =>
        factory.CreateClientAs(user) is var c ? c : throw new InvalidOperationException();

    /// <summary>Starts a job the way the page does: GET for the antiforgery token, POST, follow nothing.</summary>
    private async Task<Guid> StartJobAsync(string user = "analis1")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.Header, user);
        var token = AntiforgeryToken().Match(await client.GetStringAsync("/jobs")).Groups[1].Value;
        var response = await client.PostAsync("/jobs/start", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Guid.Parse(response.Headers.Location!.OriginalString.Split('/')[^1]);
    }

    private async Task<List<AgentEvent>> EventsAsync(Guid caseId) =>
        (await factory.Investigations.GetEventsAsync(caseId, 0, default)).Select(EventMapping.ToEvent).ToList();

    [Fact]
    public async Task A_job_runs_in_the_background_to_report_approval_and_done()
    {
        // No browser follows the job: only the background runner and RunPump move it.
        var caseId = await StartJobAsync();
        var paused = await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval, 20_000);
        Assert.True(paused.IsJob);
        Assert.Equal("Ni Kadek Ayu Lestari", paused.StartedBy);
        Assert.NotNull(paused.JobJson);

        var events = await EventsAsync(caseId);
        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        var started = events[0].PayloadAs<RunStartedPayload>();
        Assert.Equal(UseCase.PatternDetection, started.UseCase);
        Assert.Null(started.Symbol);

        // Three kinds of worker, interleaved, and one lead followed at depth 1.
        var workers = events.Where(e => e.Type == AgentEventType.WorkerStarted).Select(e => e.PayloadAs<WorkerStartedPayload>()).ToList();
        Assert.Equal([WorkerRole.Stock, WorkerRole.Broker, WorkerRole.Ownership], workers.Select(w => w.Role).Distinct());
        Assert.Contains(workers, w => w is { Role: WorkerRole.Stock, Depth: 1, Target: "BRMS" });
        var firstFinish = events.FindIndex(e => e.Type == AgentEventType.WorkerFinished);
        Assert.True(events.FindIndex(e => e.Type == AgentEventType.WorkerStarted && e.AgentName == "saham-PSKT") < firstFinish,
            "The first two stock workers run side by side.");

        var report = events[^1].PayloadAs<ApprovalRequiredPayload>().Report!;
        Assert.Equal(["VOKS", "PSKT", "BRMS"], report.Clusters.Single().Stocks);
        Assert.Equal(Memo.StandardDisclaimer, report.Disclaimer);
        var toolCallIds = events.Where(e => e.Type == AgentEventType.ToolCallStarted).Select(e => e.PayloadAs<ToolCallStartedPayload>().ToolCallId).ToHashSet();
        Assert.All(report.Clusters.SelectMany(c => c.Findings).SelectMany(f => f.Evidence), ev => Assert.Contains(ev.ToolCallId, toolCallIds));

        var page = await Client().GetStringAsync($"/jobs/{caseId}");
        Assert.Contains("data-pattern=\"worker-panels\"", page);
        Assert.Contains("VOKS", page); // screening result

        await factory.Investigations.DecideAsync(caseId, events[^1].PayloadAs<ApprovalRequiredPayload>().ApprovalId,
            Decision.Approve, null, "Ni Kadek Ayu Lestari", default);
        var finished = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());
        Assert.Equal(CaseStatus.Approved, finished.Status);
        events = await EventsAsync(caseId);
        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.NotNull(events[^1].PayloadAs<DonePayload>().Report);

        // Jobs live on /jobs, not in the investigation list.
        Assert.Contains($"/jobs/{caseId}", await Client().GetStringAsync("/jobs"));
        Assert.DoesNotContain(caseId.ToString(), await Client().GetStringAsync("/investigations"));
    }

    [Fact]
    public async Task The_user_who_started_a_job_is_notified_on_another_page()
    {
        var received = new ConcurrentQueue<JobNotification>();
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, InvestigationHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[TestAuthHandler.Header] = "analis2";
            })
            .Build();
        connection.On<JobNotification>(JobNotifier.Message, received.Enqueue);
        await connection.StartAsync();

        var caseId = await StartJobAsync("analis2");
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval, 20_000);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!received.Any(n => n.CaseId == caseId) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var notice = Assert.Single(received, n => n.CaseId == caseId);
        Assert.Equal("approval_required", notice.Status);
        Assert.Equal($"/jobs/{caseId}", notice.Url);
        Assert.True(notice.AwaitingCount >= 1);
        using var badge = JsonDocument.Parse(await Client("analis2").GetStringAsync("/jobs/badge"));
        Assert.True(badge.RootElement.GetProperty("awaiting").GetInt32() >= 1);
        await factory.Investigations.StopAsync(caseId, default);
    }

    [Fact]
    public async Task A_solution_without_pattern_detection_is_refused_with_a_clear_message()
    {
        var selection = factory.Services.GetRequiredService<AgentSelection>();
        try
        {
            await selection.SetActiveAsync("langgraph", default);
            var client = Client();
            var token = AntiforgeryToken().Match(await client.GetStringAsync("/jobs")).Groups[1].Value;
            var response = await client.PostAsync("/jobs/start", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));
            Assert.Contains("Solusi LangGraph belum mendukung deteksi pola.", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await selection.SetActiveAsync("fake", default);
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}

/// <summary>Slow tools, so a job can be stopped while workers run.</summary>
public partial class PatternJobStopTests(SlowSurveillanceFactory factory) : IClassFixture<SlowSurveillanceFactory>
{
    [Fact]
    public async Task Stopping_a_job_closes_every_running_worker()
    {
        var runner = factory.Services.GetRequiredService<PatternJobRunner>();
        var started = await runner.StartAsync("analis1", "Ni Kadek Ayu Lestari", default);
        var caseId = started.CaseId!.Value;

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await factory.Investigations.GetEventsAsync(caseId, 0, default)).Count(e => e.Type == "worker_started") < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "Workers did not start.");
            await Task.Delay(50);
        }

        await factory.Investigations.StopAsync(caseId, default);
        var stopped = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal(), 20_000);
        Assert.Equal(CaseStatus.Cancelled, stopped.Status);

        var events = (await factory.Investigations.GetEventsAsync(caseId, 0, default)).Select(EventMapping.ToEvent).ToList();
        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Contains(events, e => e.Type == AgentEventType.WorkerFinished && e.PayloadAs<WorkerFinishedPayload>().Status == WorkerStatus.Cancelled);
    }
}
