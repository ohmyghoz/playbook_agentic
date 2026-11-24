using System.Net;
using System.Text.RegularExpressions;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

/// <summary>The settings page switches the solution for new investigations; existing cases keep theirs.</summary>
public partial class SettingsTests(SurveillanceFactory factory) : IClassFixture<SurveillanceFactory>, IAsyncLifetime
{
    private AgentSelection Selection => (AgentSelection)factory.Services.GetService(typeof(AgentSelection))!;

    private static StartInvestigation Input() =>
        new("BBCA", new DateOnly(2026, 11, 24), TriggerKind.Volume, "Volume di atas 1,1x", null, null, null, null, null);

    public async Task InitializeAsync() => await Selection.SetActiveAsync("fake", default);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Settings_page_lists_every_solution_with_its_status()
    {
        var choices = await Selection.ListAsync(default);

        Assert.Equal(["fake", "maf", "langgraph", "adk"], choices.Select(c => c.Key));
        Assert.Single(choices, c => c.Active);
        Assert.True(choices.Single(c => c.Key == "fake").Ready);
        // No OPENAI_API_KEY and no Python services in the test environment.
        Assert.False(choices.Single(c => c.Key == "maf").Ready);
        Assert.False(choices.Single(c => c.Key == "langgraph").Ready);

        var page = await factory.CreateClient().GetStringAsync("/settings");
        Assert.Contains("Dipakai sekarang", page);
        Assert.Contains("value=\"adk\"", page);
    }

    [Fact]
    public async Task Saving_through_the_form_changes_the_solution_for_new_investigations()
    {
        var client = factory.CreateClient();
        var page = await client.GetStringAsync("/settings");
        var token = AntiforgeryToken().Match(page).Groups[1].Value;

        var saved = await client.PostAsync("/settings/agent", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["provider"] = "langgraph",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode); // followed the redirect back to the page
        Assert.Contains("Investigasi baru sekarang memakai", await saved.Content.ReadAsStringAsync());
        Assert.Equal("langgraph", await Selection.GetActiveAsync(default));

        var unknown = await client.PostAsync("/settings/agent", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["provider"] = "gpt",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task A_running_case_keeps_its_solution_after_the_setting_changes()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);

        await Selection.SetActiveAsync("maf", default);
        var events = await factory.Investigations.GetEventsAsync(caseId, 0, default);
        var approvalId = EventMapping.ToEvent(events[^1]).PayloadAs<ApprovalRequiredPayload>().ApprovalId;
        await factory.Investigations.DecideAsync(caseId, approvalId, Decision.Approve, null, "analis-uji", default);

        var finished = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());
        Assert.Equal("fake", finished.Provider);
        Assert.Equal(CaseStatus.Approved, finished.Status);
    }

    [Fact]
    public async Task A_solution_that_is_not_ready_fails_the_new_case_with_a_clear_message()
    {
        await Selection.SetActiveAsync("maf", default); // no OPENAI_API_KEY in tests

        var caseId = await factory.Investigations.StartAsync(Input(), default);
        var failed = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());

        Assert.Equal("maf", failed.Provider);
        Assert.Equal(CaseStatus.Failed, failed.Status);
        var error = EventMapping.ToEvent((await factory.Investigations.GetEventsAsync(caseId, 0, default))[^1]);
        Assert.Contains("halaman Setting", error.PayloadAs<ErrorPayload>().Message);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
