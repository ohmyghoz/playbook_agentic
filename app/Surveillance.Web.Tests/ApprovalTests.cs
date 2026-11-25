using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

/// <summary>Bab 12: claims, reassignment, approval expiry and the audit timeline.</summary>
public partial class ApprovalTests(SurveillanceFactory factory) : IClassFixture<SurveillanceFactory>
{
    private static StartInvestigation Input(string symbol = "ICBP") =>
        new(symbol, new DateOnly(2026, 11, 24), TriggerKind.Volume, "Volume di atas 1,1x", null, null, null, null, null);

    private async Task<(Guid CaseId, string ApprovalId)> PausedCaseAsync(string symbol = "ICBP")
    {
        var caseId = await factory.Investigations.StartAsync(Input(symbol), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);
        var events = await factory.Investigations.GetEventsAsync(caseId, 0, default);
        return (caseId, EventMapping.ToEvent(events[^1]).PayloadAs<ApprovalRequiredPayload>().ApprovalId);
    }

    private async Task UpdateCaseAsync(Guid caseId, Action<InvestigationCase> change)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<SurveillanceDbContext>>().CreateDbContextAsync();
        var c = await db.Cases.SingleAsync(x => x.Id == caseId);
        change(c);
        await db.SaveChangesAsync();
    }

    private static async Task<string> TokenAsync(HttpClient client, string url) =>
        AntiforgeryToken().Match(await client.GetStringAsync(url)).Groups[1].Value;

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
            .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token)));

    private static async Task<string> MessageAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString()!;

    [Fact]
    public async Task Two_analysts_claim_and_a_supervisor_reassigns()
    {
        var (caseId, approvalId) = await PausedCaseAsync();
        var ayu = factory.CreateClientAs("analis1");
        var marthen = factory.CreateClientAs("analis2");
        var siti = factory.CreateClientAs("supervisor");
        var ayuToken = await TokenAsync(ayu, $"/investigations/{caseId}");
        var marthenToken = await TokenAsync(marthen, $"/investigations/{caseId}");

        // Ni Kadek Ayu Lestari takes the case; Marthen Rumbiak sees it held and cannot decide.
        Assert.Equal(HttpStatusCode.OK, (await ayu.PostAsync($"/investigations/{caseId}/claim", Form(ayuToken))).StatusCode);
        var taken = await marthen.PostAsync($"/investigations/{caseId}/claim", Form(marthenToken));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("Kasus ini sedang dipegang oleh Ni Kadek Ayu Lestari.", await MessageAsync(taken));
        using (var state = JsonDocument.Parse(await marthen.GetStringAsync($"/investigations/{caseId}/claim")))
        {
            Assert.Equal("Ni Kadek Ayu Lestari", state.RootElement.GetProperty("holder").GetString());
            Assert.False(state.RootElement.GetProperty("mine").GetBoolean());
        }

        var refused = await marthen.PostAsync($"/investigations/{caseId}/approval",
            Form(marthenToken, ("ApprovalId", approvalId), ("Decision", "approve")));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.StartsWith("Kasus ini sedang dipegang oleh Ni Kadek Ayu Lestari.", await MessageAsync(refused));

        // An analyst cannot reassign; the supervisor can, from the queue.
        var queueToken = await TokenAsync(siti, "/approvals");
        Assert.Contains("Ni Kadek Ayu Lestari", await siti.GetStringAsync("/approvals"));
        var analystAssign = await marthen.PostAsync($"/approvals/{caseId}/assign", Form(await TokenAsync(marthen, "/approvals"), ("userName", "analis2")));
        Assert.NotEqual(HttpStatusCode.OK, analystAssign.StatusCode);
        var assigned = await siti.PostAsync($"/approvals/{caseId}/assign", Form(queueToken, ("userName", "analis2")));
        Assert.Contains("Kasus dialihkan ke Marthen Rumbiak.", await assigned.Content.ReadAsStringAsync());

        var decided = await marthen.PostAsync($"/investigations/{caseId}/approval",
            Form(marthenToken, ("ApprovalId", approvalId), ("Decision", "approve")));
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        var finished = await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());
        Assert.Equal(CaseStatus.Approved, finished.Status);
        Assert.Equal("Marthen Rumbiak", Assert.Single(finished.Decisions).DecidedBy);
        Assert.Null(finished.ClaimedBy);

        // The audit timeline answers who did what, and when.
        var audit = JsonDocument.Parse(await siti.GetStringAsync($"/investigations/{caseId}/audit.json")).RootElement.GetProperty("entries")
            .EnumerateArray().Select(e => (Actor: e.GetProperty("actor").GetString(), Action: e.GetProperty("action").GetString(),
                At: e.GetProperty("at").GetDateTime())).ToList();
        Assert.Contains(("Ni Kadek Ayu Lestari", "claim"), audit.Select(a => (a.Actor, a.Action)));
        Assert.Contains(("Siti Rahmah Siregar", "assign"), audit.Select(a => (a.Actor, a.Action)));
        Assert.Contains(("Marthen Rumbiak", "decision_approve"), audit.Select(a => (a.Actor, a.Action)));
        Assert.Equal(audit.Select(a => a.At).Order(), audit.Select(a => a.At));
        var page = await siti.GetStringAsync($"/investigations/{caseId}/audit");
        Assert.Contains("Dialihkan dari Ni Kadek Ayu Lestari ke Marthen Rumbiak.", page);
    }

    [Fact]
    public async Task A_claim_lapses_after_inactivity()
    {
        var (caseId, _) = await PausedCaseAsync("KLBF");
        var claims = factory.Services.GetRequiredService<CaseClaims>();
        Assert.Equal(ClaimResult.Claimed, (await claims.ClaimAsync(caseId, "analis1", "Ni Kadek Ayu Lestari", default)).Result);
        Assert.Equal(ClaimResult.HeldByOther, (await claims.ClaimAsync(caseId, "analis2", "Marthen Rumbiak", default)).Result);

        await UpdateCaseAsync(caseId, c => c.ClaimActivityAt = DateTime.UtcNow.AddMinutes(-16));

        Assert.Null((await claims.GetAsync(caseId, default))!.Holder);
        Assert.Equal(ClaimResult.Claimed, (await claims.ClaimAsync(caseId, "analis2", "Marthen Rumbiak", default)).Result);
        await factory.Investigations.StopAsync(caseId, default);
    }

    [Fact]
    public async Task An_approval_past_its_deadline_expires_with_a_recorded_reason()
    {
        var (caseId, approvalId) = await PausedCaseAsync("INDF");
        var paused = (await factory.Investigations.GetCaseAsync(caseId, default))!;
        Assert.NotNull(paused.AwaitingSince);

        await UpdateCaseAsync(caseId, c => c.AwaitingSince = DateTime.UtcNow.AddHours(-25));
        var expired = await factory.Services.GetRequiredService<ApprovalExpiryService>().ExpireDueAsync(default);
        Assert.Contains(caseId, expired);

        // The run is closed by the agent (done cancelled), the case stays Expired.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        List<AgentEvent> events;
        do
        {
            await Task.Delay(50);
            events = (await factory.Investigations.GetEventsAsync(caseId, 0, default)).Select(EventMapping.ToEvent).ToList();
        }
        while (!events[^1].IsTerminal && DateTime.UtcNow < deadline);

        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
        var c = (await factory.Investigations.GetCaseAsync(caseId, default))!;
        Assert.Equal(CaseStatus.Expired, c.Status);
        var decision = Assert.Single(c.Decisions);
        Assert.Equal(("expire", "Sistem"), (decision.Decision, decision.DecidedBy));
        Assert.StartsWith("Tidak ada keputusan analis dalam 24 jam", decision.Comment);

        // A late decision is told what happened.
        var late = await factory.Investigations.DecideAsync(caseId, approvalId, Decision.Approve, null, "Marthen Rumbiak", default);
        Assert.Equal(new DecisionOutcome(DecisionResult.AlreadyDecided, "Sistem"), late);
        Assert.Contains("Kedaluwarsa", await factory.CreateClient().GetStringAsync($"/investigations/{caseId}"));

        // The page asks this after done(cancelled) to tell an expiry from a stop.
        using var state = JsonDocument.Parse(await factory.CreateClient().GetStringAsync($"/investigations/{caseId}/claim"));
        Assert.Equal("expired", state.RootElement.GetProperty("status").GetString());
        Assert.Equal("expire", state.RootElement.GetProperty("decision").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Queue_lists_the_longest_waiting_first_and_marks_near_expiry()
    {
        var (older, _) = await PausedCaseAsync("UNTR");
        var (newer, _) = await PausedCaseAsync("PGAS");
        await UpdateCaseAsync(older, c => c.AwaitingSince = DateTime.UtcNow.AddHours(-21)); // 3 hours left: within the 4-hour warning

        var page = await factory.CreateClient().GetStringAsync("/approvals");
        Assert.True(page.IndexOf("UNTR", StringComparison.Ordinal) < page.IndexOf("PGAS", StringComparison.Ordinal));
        Assert.Contains("Hampir kedaluwarsa", page);

        await factory.Investigations.StopAsync(older, default);
        await factory.Investigations.StopAsync(newer, default);
    }

    [Fact]
    public async Task Revision_comments_stay_with_the_draft_they_replaced()
    {
        var (caseId, approvalId) = await PausedCaseAsync("SMGR");
        await factory.Investigations.DecideAsync(caseId, approvalId, Decision.Revise, "Tambahkan data asing 20 sesi.", "Ni Kadek Ayu Lestari", default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval
            && factory.Investigations.GetEventsAsync(caseId, 0, default).Result.Count(e => e.Type == "approval_required") == 2);

        var page = await factory.CreateClient().GetStringAsync($"/investigations/{caseId}");
        Assert.Contains("Tambahkan data asing 20 sesi.", page); // data-decisions for the replaced draft's card
        await factory.Investigations.StopAsync(caseId, default);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
