using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

/// <summary>Two analysts deciding the same approval at once: exactly one decision wins.</summary>
public partial class DecisionConcurrencyTests(SurveillanceFactory factory) : IClassFixture<SurveillanceFactory>
{
    private static StartInvestigation Input() =>
        new("ANTM", new DateOnly(2026, 11, 24), TriggerKind.Price, "Harga naik di atas 5%", 6.4, null, null, null, null);

    private async Task<(Guid CaseId, string ApprovalId)> PausedCaseAsync()
    {
        var caseId = await factory.Investigations.StartAsync(Input(), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);
        var events = await factory.Investigations.GetEventsAsync(caseId, 0, default);
        return (caseId, EventMapping.ToEvent(events[^1]).PayloadAs<ApprovalRequiredPayload>().ApprovalId);
    }

    [Fact]
    public async Task Simultaneous_decisions_accept_exactly_one()
    {
        for (var round = 0; round < 5; round++)
        {
            var (caseId, approvalId) = await PausedCaseAsync();

            // Start both together so both pass the status check before either claims the case.
            using var gate = new SemaphoreSlim(0);
            var approve = Task.Run(async () =>
            {
                await gate.WaitAsync();
                return await factory.Investigations.DecideAsync(caseId, approvalId, Decision.Approve, null, "Ni Kadek Ayu Lestari", default);
            });
            var reject = Task.Run(async () =>
            {
                await gate.WaitAsync();
                return await factory.Investigations.DecideAsync(caseId, approvalId, Decision.Reject, "Bukti kurang.", "Marthen Rumbiak", default);
            });
            gate.Release(2);
            var outcomes = await Task.WhenAll(approve, reject);

            var winner = Assert.Single(outcomes, o => o.Result == DecisionResult.Accepted);
            var loser = Assert.Single(outcomes, o => o.Result != DecisionResult.Accepted);
            Assert.Equal(DecisionResult.AlreadyDecided, loser.Result);

            var finished = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());
            var decision = Assert.Single(finished.Decisions);
            Assert.Equal(decision.DecidedBy, loser.DecidedBy);
            Assert.Equal(winner == outcomes[0] ? CaseStatus.Approved : CaseStatus.Rejected, finished.Status);

            // The loser never reached the agent, so the run ended cleanly instead of with an error.
            var events = (await factory.Investigations.GetEventsAsync(caseId, 0, default)).Select(EventMapping.ToEvent).ToList();
            EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
            Assert.Equal(AgentEventType.Done, events[^1].Type);
        }
    }

    [Fact]
    public async Task Second_analyst_gets_409_naming_who_decided()
    {
        var (caseId, approvalId) = await PausedCaseAsync();
        var first = factory.CreateClientAs("analis1");
        var second = factory.CreateClientAs("analis2");
        var firstToken = await TokenAsync(first, caseId);
        var secondToken = await TokenAsync(second, caseId); // page loaded while the case still waited

        var accepted = await first.PostAsync($"/investigations/{caseId}/approval", Form(approvalId, firstToken));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var refused = await second.PostAsync($"/investigations/{caseId}/approval", Form(approvalId, secondToken));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString();
        Assert.Equal("Kasus ini sudah diputuskan oleh Ni Kadek Ayu Lestari. Muat ulang halaman untuk melihat keputusannya.", message);

        var finished = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());
        Assert.Equal("Ni Kadek Ayu Lestari", Assert.Single(finished.Decisions).DecidedBy);
    }

    private static async Task<string> TokenAsync(HttpClient client, Guid caseId) =>
        AntiforgeryToken().Match(await client.GetStringAsync($"/investigations/{caseId}")).Groups[1].Value;

    private static FormUrlEncodedContent Form(string approvalId, string token) => new(new Dictionary<string, string>
    {
        ["ApprovalId"] = approvalId,
        ["Decision"] = "approve",
        ["__RequestVerificationToken"] = token,
    });

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
