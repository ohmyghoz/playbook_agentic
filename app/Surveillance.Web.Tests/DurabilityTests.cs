using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Surveillance.Agents.Abstractions;
using Surveillance.Agents.Maf;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

/// <summary>Bab 13: restart behaviour and the retention policy, with artificially aged data.</summary>
public class DurabilityTests(SurveillanceFactory factory) : IClassFixture<SurveillanceFactory>
{
    private static StartInvestigation Input(string symbol) =>
        new(symbol, new DateOnly(2026, 11, 24), TriggerKind.Volume, "Volume di atas 1,1x", null, null, null, null, null);

    private IDbContextFactory<SurveillanceDbContext> Db => factory.Services.GetRequiredService<IDbContextFactory<SurveillanceDbContext>>();

    private async Task<Guid> FinishedCaseAsync(string symbol)
    {
        var caseId = await factory.Investigations.StartAsync(Input(symbol), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);
        var events = await factory.Investigations.GetEventsAsync(caseId, 0, default);
        var approvalId = EventMapping.ToEvent(events[^1]).PayloadAs<ApprovalRequiredPayload>().ApprovalId;
        await factory.Investigations.DecideAsync(caseId, approvalId, Decision.Approve, null, "Ni Kadek Ayu Lestari", default);
        await factory.WaitForStatusAsync(caseId, s => s.IsTerminal());
        return caseId;
    }

    private async Task AgeAsync(Guid caseId, TimeSpan age)
    {
        await using var db = await Db.CreateDbContextAsync();
        await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTime.UtcNow - age));
        await db.Previews.Where(p => p.CaseId == caseId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, DateTime.UtcNow - age));
    }

    [Fact]
    public async Task A_run_lost_in_a_restart_ends_with_a_retryable_run_interrupted()
    {
        var caseId = await factory.Investigations.StartAsync(Input("TOWR"), default);
        await factory.WaitForStatusAsync(caseId, s => s == CaseStatus.AwaitingApproval);

        // As after a restart: the pump re-attaches, but the agent (here the in-memory fake) no longer knows the run.
        await factory.Pump.Start(caseId, "fake-lost-in-restart", "fake", afterRestart: true);

        var last = EventMapping.ToEvent((await factory.Investigations.GetEventsAsync(caseId, 0, default))[^1]);
        EventAssertions.AssertMatchesSchema(last);
        var error = last.PayloadAs<ErrorPayload>();
        Assert.Equal(AgentErrorCode.RunInterrupted, error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(RunPump.InterruptedMessage, error.Message);
        Assert.Equal(CaseStatus.Failed, (await factory.Investigations.GetCaseAsync(caseId, default))!.Status);
    }

    [Fact]
    public async Task Retention_compacts_tokens_deletes_old_data_and_is_idempotent()
    {
        var recent = await FinishedCaseAsync("JSMR");
        var old = await FinishedCaseAsync("EXCL");
        await AgeAsync(recent, TimeSpan.FromHours(2));   // finished: compact tokens, keep events
        await AgeAsync(old, TimeSpan.FromDays(91));      // past 90 days: events and previews go

        var narrationBefore = string.Concat((await factory.Investigations.GetEventsAsync(recent, 0, default))
            .Where(e => e.Type == "token").Select(e => EventMapping.ToEvent(e).PayloadAs<TokenPayload>().Text));
        var countBefore = (await factory.Investigations.GetEventsAsync(recent, 0, default)).Count;

        var maf = factory.Services.GetRequiredService<MafAgentOptions>();
        Directory.CreateDirectory(maf.StateDirectory);
        var staleFile = Path.Combine(maf.StateDirectory, $"maf-test-{Guid.NewGuid():N}.json");
        var freshFile = Path.Combine(maf.StateDirectory, $"maf-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(staleFile, "{}");
        await File.WriteAllTextAsync(freshFile, "{}");
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddDays(-31));

        var retention = factory.Services.GetRequiredService<RetentionService>();
        var report = await retention.RunOnceAsync(DateTime.UtcNow, default);

        // Recent case: consecutive tokens merged, same narration, every non-token event kept.
        var after = (await factory.Investigations.GetEventsAsync(recent, 0, default)).Select(EventMapping.ToEvent).ToList();
        Assert.True(after.Count < countBefore);
        Assert.Equal(narrationBefore, string.Concat(after.Where(e => e.Type == AgentEventType.Token).Select(e => e.PayloadAs<TokenPayload>().Text)));
        Assert.All(after, EventAssertions.AssertMatchesSchema);
        Assert.Equal(AgentEventType.Done, after[^1].Type);

        // Old case: events and previews gone; the case, its decision and the audit trail stay.
        Assert.Empty(await factory.Investigations.GetEventsAsync(old, 0, default));
        var oldCase = (await factory.Investigations.GetCaseAsync(old, default))!;
        Assert.Single(oldCase.Decisions);
        Assert.NotNull(oldCase.EventsPurgedAt);
        await using (var db = await Db.CreateDbContextAsync())
        {
            Assert.False(await db.Previews.AnyAsync(p => p.CaseId == old));
            Assert.True(await db.Previews.AnyAsync(p => p.CaseId == recent)); // 2 hours old: within 30 days
        }

        Assert.False(File.Exists(staleFile));
        Assert.True(File.Exists(freshFile));
        File.Delete(freshFile);
        Assert.True(report.TokensMerged > 0 && report.CasesPurged >= 1 && report.MafFilesDeleted >= 1);

        // A second pass finds nothing more to do for these cases.
        var again = await retention.RunOnceAsync(DateTime.UtcNow, default);
        Assert.Equal(0, again.TokensMerged);
        Assert.Equal(0, again.EventsDeleted);
    }
}
