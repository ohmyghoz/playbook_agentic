using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

public partial class InvestigationFlowTests(SurveillanceFactory factory) : IClassFixture<SurveillanceFactory>
{
    private static StartInvestigation Input(string symbol = "BBCA") =>
        new(symbol, new DateOnly(2026, 11, 24), TriggerKind.Volume, "Volume di atas 1,1x rata-rata 3 bulan",
            2.2, 235_635_300, 210_424_313, 1.12, null);

    private async Task<List<AgentEvent>> StoredEvents(Guid caseId) =>
        (await factory.Investigations.GetEventsAsync(caseId, 0, default)).Select(EventMapping.ToEvent).ToList();

    [Fact]
    public async Task Run_is_persisted_until_approval_then_done_after_approve()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        var paused = await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);

        var events = await StoredEvents(caseId);
        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
        Assert.Equal(events[^1].Sequence, paused.LastSequence);
        Assert.Equal("fake", paused.Provider);

        var approvalId = events[^1].PayloadAs<ApprovalRequiredPayload>().ApprovalId;
        var result = await factory.Investigations.DecideAsync(caseId, approvalId, Decision.Approve, null, "analis-uji", default);
        Assert.Equal(DecisionResult.Accepted, result);

        var finished = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());
        Assert.Equal(CaseStatus.Approved, finished.Status);
        events = await StoredEvents(caseId);
        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);

        var decision = Assert.Single(finished.Decisions);
        Assert.Equal("approve", decision.Decision);
        Assert.Equal(approvalId, decision.ApprovalId);
    }

    [Fact]
    public async Task Reject_without_comment_is_refused_and_with_comment_is_recorded()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);
        var approvalId = (await StoredEvents(caseId))[^1].PayloadAs<ApprovalRequiredPayload>().ApprovalId;

        Assert.Equal(DecisionResult.CommentRequired,
            await factory.Investigations.DecideAsync(caseId, approvalId, Decision.Reject, " ", "analis-uji", default));
        Assert.Equal(DecisionResult.StaleApproval,
            await factory.Investigations.DecideAsync(caseId, "approval_x", Decision.Approve, null, "analis-uji", default));

        await factory.Investigations.DecideAsync(caseId, approvalId, Decision.Reject, "Bukti belum cukup.", "analis-uji", default);
        var finished = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());

        Assert.Equal(CaseStatus.Rejected, finished.Status);
        Assert.Equal("Bukti belum cukup.", Assert.Single(finished.Decisions).Comment);
        Assert.Equal(RunOutcome.Rejected, (await StoredEvents(caseId))[^1].PayloadAs<DonePayload>().Outcome);
    }

    [Fact]
    public async Task Persisting_the_same_event_twice_stores_it_once()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);
        var events = await StoredEvents(caseId);

        await factory.Pump.PersistAsync(caseId, events[2]);

        Assert.Equal(events.Count, (await StoredEvents(caseId)).Count);
    }

    [Fact]
    public async Task Sse_replays_from_after_and_ends_for_finished_run()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);
        var approvalId = (await StoredEvents(caseId))[^1].PayloadAs<ApprovalRequiredPayload>().ApprovalId;
        await factory.Investigations.DecideAsync(caseId, approvalId, Decision.Approve, null, "analis-uji", default);
        await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());
        var stored = await StoredEvents(caseId);

        var client = factory.CreateClient();
        var body = await client.GetStringAsync($"/investigations/{caseId}/events?after=3");
        var frames = ParseSse(body);

        Assert.Equal(stored.Skip(3).Select(e => e.Sequence), frames.Select(f => f.Id));
        Assert.Equal("done", frames[^1].Event);
        foreach (var f in frames)
        {
            EventAssertions.AssertMatchesSchema(f.Data);
        }

        // Last-Event-ID takes precedence, as sent by EventSource on reconnect.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/investigations/{caseId}/events");
        request.Headers.Add("Last-Event-ID", (stored.Count - 2).ToString());
        var resumed = ParseSse(await (await client.SendAsync(request)).Content.ReadAsStringAsync());
        Assert.Equal([stored.Count - 1, stored.Count], resumed.Select(f => f.Id));
    }

    [Fact]
    public async Task Sse_follows_live_events_while_run_is_active()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);

        var client = factory.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync($"/investigations/{caseId}/events",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var seen = new List<string>();
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                seen.Add(line[7..]);
                if (line == "event: approval_required")
                {
                    break;
                }
            }
        }

        Assert.Equal("run_started", seen[0]);
        Assert.Equal("approval_required", seen[^1]);
    }

    [Fact]
    public async Task Approval_endpoint_requires_antiforgery_and_accepts_form_post()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);
        var approvalId = (await StoredEvents(caseId))[^1].PayloadAs<ApprovalRequiredPayload>().ApprovalId;

        var client = factory.CreateClient();
        var page = await client.GetStringAsync($"/investigations/{caseId}");
        Assert.Contains("data-mode=\"live\"", page);
        var token = AntiforgeryToken().Match(page).Groups[1].Value;

        var form = new Dictionary<string, string>
        {
            ["ApprovalId"] = approvalId,
            ["Decision"] = "revise",
            ["Comment"] = "Tambahkan pola asing 20 sesi.",
            ["DecidedBy"] = "analis-uji",
        };

        var withoutToken = await client.PostAsync($"/investigations/{caseId}/approval", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);

        form["__RequestVerificationToken"] = token;
        var accepted = await client.PostAsync($"/investigations/{caseId}/approval", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval
            && StoredEvents(caseId).Result[^1].PayloadAs<ApprovalRequiredPayload>().Revision == 1);
    }

    [Fact]
    public async Task Stop_while_awaiting_approval_ends_cancelled()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);

        await factory.Investigations.StopAsync(caseId, default);
        var finished = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());

        Assert.Equal(CaseStatus.Cancelled, finished.Status);
        EventAssertions.AssertWellFormedRun(await StoredEvents(caseId), expectTerminal: true);
    }

    [Fact]
    public async Task Pages_render_and_history_page_uses_replay_mode()
    {
        var caseId = await factory.Investigations.StartAsync(Input("TLKM"), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);
        var client = factory.CreateClient();

        var index = await client.GetStringAsync("/investigations");
        Assert.Contains("TLKM", index);
        Assert.Contains("Menunggu keputusan analis", index);

        var history = await client.GetStringAsync($"/investigations/{caseId}/history");
        Assert.Contains("data-mode=\"replay\"", history);

        var json = await client.GetStringAsync($"/investigations/{caseId}/events.json");
        Assert.StartsWith("[{\"runId\"", json);
    }

    private sealed record SseFrame(long Id, string Event, string Data);

    private static List<SseFrame> ParseSse(string body)
    {
        var frames = new List<SseFrame>();
        foreach (var block in body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            long id = 0;
            string type = "", data = "";
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("id: ", StringComparison.Ordinal)) id = long.Parse(line[4..]);
                else if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data += line[6..];
            }

            frames.Add(new SseFrame(id, type, data));
        }

        return frames;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}

public class StopDuringRunTests(SlowSurveillanceFactory factory) : IClassFixture<SlowSurveillanceFactory>
{
    [Fact]
    public async Task Stop_during_tool_call_closes_call_and_ends_cancelled()
    {
        var input = new StartInvestigation("BBRI", new DateOnly(2026, 11, 24), TriggerKind.Price, "Harga naik di atas 5%",
            6.1, null, null, null, null);
        var caseId = await factory.Investigations.StartAsync(input, default);

        // Wait until a tool call is in flight.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await factory.Investigations.GetEventsAsync(caseId, 0, default)).All(e => e.Type != "tool_call_started"))
        {
            Assert.True(DateTime.UtcNow < deadline, "No tool call started.");
            await Task.Delay(20);
        }

        await factory.Investigations.StopAsync(caseId, default);
        var finished = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());

        Assert.Equal(CaseStatus.Cancelled, finished.Status);
        var events = (await factory.Investigations.GetEventsAsync(caseId, 0, default)).Select(EventMapping.ToEvent).ToList();
        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Contains(events, e => e.Type == AgentEventType.ToolCallFinished
                                     && e.PayloadAs<ToolCallFinishedPayload>().Error?.Code == "cancelled");
    }
}
