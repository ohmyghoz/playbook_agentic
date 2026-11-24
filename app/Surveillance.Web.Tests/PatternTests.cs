using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

/// <summary>The investigation page is assembled from one partial per UI pattern (docs/PATTERNS.md).</summary>
public partial class PatternTests(SurveillanceFactory factory) : IClassFixture<SurveillanceFactory>
{
    private static readonly string[] Patterns =
    [
        "narrative", "thinking-indicator", "tool-calls", "step-progress", "evidence", "stop-button", "error-banner", "approval-card",
    ];

    private static StartInvestigation Input(string? notes = null) =>
        new("BMRI", new DateOnly(2026, 11, 24), TriggerKind.Volume, "Volume di atas 1,1x", null, null, null, null, notes);

    [Fact]
    public async Task Live_page_holds_every_pattern_and_its_module()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        var page = await factory.CreateClient().GetStringAsync($"/investigations/{caseId}");

        foreach (var pattern in Patterns)
        {
            Assert.Contains($"data-pattern=\"{pattern}\"", page);
            Assert.Contains($"/js/patterns/{pattern}.js", page);
        }

        Assert.Contains("data-agent-ui", page);
        Assert.Contains($"/investigations/{caseId}/tools/__call__/preview", page);
        Assert.Contains("Data yang sudah masuk tetap tersimpan.", page);
    }

    [Fact]
    public async Task Finished_case_has_no_stop_button()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);
        await factory.Investigations.StopAsync(caseId, default);
        await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());

        var page = await factory.CreateClient().GetStringAsync($"/investigations/{caseId}");
        Assert.DoesNotContain("data-pattern=\"stop-button\"", page);
        Assert.Contains("data-terminal=\"true\"", page);
    }

    [Fact]
    public async Task Lab_patterns_page_has_a_section_per_pattern_and_starts_scenarios()
    {
        var client = factory.CreateClient();
        var page = await client.GetStringAsync("/lab/patterns");
        foreach (var pattern in Patterns)
        {
            Assert.Contains($"id=\"pattern-{pattern}\"", page);
        }

        Assert.Contains("data-play-scenario=\"silent_30s\"", page);
        var token = AntiforgeryToken().Match(page).Groups[1].Value;

        var started = await client.PostAsync("/lab/patterns/start", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["scenario"] = "model_error",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var body = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var caseId = body.RootElement.GetProperty("caseId").GetGuid();
        Assert.Equal($"/investigations/{caseId}/events", body.RootElement.GetProperty("eventsUrl").GetString());
        Assert.Equal($"/investigations/{caseId}/retry", body.RootElement.GetProperty("retryUrl").GetString());

        var failed = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());
        Assert.Equal(CaseStatus.Failed, failed.Status);

        var unknown = await client.PostAsync("/lab/patterns/start", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["scenario"] = "drop_tables",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Silent_scenario_says_nothing_after_it_starts_and_can_be_stopped()
    {
        var caseId = await factory.Investigations.StartAsync(Input("scenario=silent_30s"), default);
        await Task.Delay(1500);

        var events = (await factory.Investigations.GetEventsAsync(caseId, 0, default)).Select(EventMapping.ToEvent).ToList();
        Assert.Equal([AgentEventType.RunStarted, AgentEventType.StepStarted], events.Select(e => e.Type));

        await factory.Investigations.StopAsync(caseId, default);
        var stopped = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());
        Assert.Equal(CaseStatus.Cancelled, stopped.Status);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
