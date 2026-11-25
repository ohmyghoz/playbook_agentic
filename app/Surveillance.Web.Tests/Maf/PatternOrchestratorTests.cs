using Surveillance.Agents.Abstractions;
using Surveillance.Agents.Maf;

namespace Surveillance.Web.Tests.Maf;

/// <summary>The orchestrator rules of asd-pattern-detection.md, with scripted workers instead of a model.</summary>
public class PatternOrchestratorTests
{
    private static PatternJob Job(int maxDepth = 2, int maxStocks = 15, int maxToolCalls = 150, params string[] candidates) => new(
        new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 24),
        (candidates.Length > 0 ? candidates : ["AAAA", "BBBB", "CCCC"]).Select(s => new PatternCandidate(s, 20)).ToList(),
        new PatternLimits(maxDepth, maxStocks, 3, 12, maxToolCalls, 20, 40, 30));

    /// <summary>
    /// AAAA and BBBB are bought mostly by YP (45% of AAAA: above the threshold, so followed although YP
    /// is dominant in two of three candidates); CC is dominant everywhere with small shares.
    /// Broker YP leads to DDDD; DDDD's buyer ZZ leads to EEEE.
    /// </summary>
    private static Task<WorkerOutcome> Script(WorkerStartedPayload w, CancellationToken _)
    {
        WorkerReport report = (w.Role, w.Target) switch
        {
            (WorkerRole.Stock, "AAAA") => new("A", [new("YP", 45), new("CC", 12)]),
            (WorkerRole.Stock, "BBBB") => new("B", [new("YP", 28), new("CC", 13)]),
            (WorkerRole.Stock, "CCCC") => new("C", [new("CC", 15), new("KK", 5)]),
            (WorkerRole.Stock, "DDDD") => new("D", [new("ZZ", 45)]),
            (WorkerRole.Stock, _) => new("x", []),
            (WorkerRole.Broker, "YP") => new("YP", Leads: [new Lead(LeadKind.Stock, "DDDD", "YP teratas")]),
            (WorkerRole.Broker, "ZZ") => new("ZZ", Leads: [new Lead(LeadKind.Stock, "EEEE", "ZZ teratas")]),
            (WorkerRole.Broker, _) => new("none"),
            _ => new("Tidak ada pemegang yang sama."),
        };
        return Task.FromResult(new WorkerOutcome(w, WorkerStatus.Ok, report));
    }

    private static async Task<(PatternFacts Facts, List<WorkerStartedPayload> Ran)> RunAsync(PatternJob job, JobBudget? budget = null)
    {
        var ran = new List<WorkerStartedPayload>();
        var orchestrator = new PatternOrchestrator(job, budget ?? new JobBudget(1000, DateTime.UtcNow.AddHours(1), () => 0, () => { }),
            (w, ct) => { lock (ran) ran.Add(w); return Script(w, ct); }, _ => { });
        return (await orchestrator.RunAsync(default), ran);
    }

    [Fact]
    public async Task Follows_dominant_brokers_and_their_leads_and_skips_widespread_small_brokers()
    {
        var (facts, ran) = await RunAsync(Job());

        Assert.Contains(ran, w => w is { Role: WorkerRole.Broker, Target: "YP", Depth: 1 });
        Assert.Contains(ran, w => w is { Role: WorkerRole.Stock, Target: "DDDD", Depth: 1 });
        Assert.Contains(ran, w => w is { Role: WorkerRole.Broker, Target: "ZZ", Depth: 2 });
        Assert.Contains(ran, w => w is { Role: WorkerRole.Stock, Target: "EEEE", Depth: 2 });

        // CC is dominant in all three candidates (100% > 30%) with shares under 40%: recorded, not followed.
        Assert.DoesNotContain(ran, w => w.Target == "CC");
        Assert.Contains(facts.LeadsNotFollowed, l => l is { Kind: LeadKind.Broker, Target: "CC" });

        var cluster = Assert.Single(facts.Clusters, c => c.DominantBrokers.Contains("YP"));
        Assert.Equal(["AAAA", "BBBB", "DDDD"], cluster.Stocks);
        Assert.Contains(ran, w => w.Role == WorkerRole.Ownership && w.Target == "AAAA, BBBB, DDDD");
        Assert.Equal("Tidak ada pemegang yang sama.", cluster.OwnershipSummary);
    }

    [Fact]
    public async Task Depth_and_stock_limits_end_the_chase_with_a_recorded_reason()
    {
        var (facts, ran) = await RunAsync(Job(maxDepth: 1, maxStocks: 4));

        Assert.DoesNotContain(ran, w => w.Target is "ZZ" or "EEEE");
        Assert.Contains(facts.LeadsNotFollowed, l => l is { Kind: LeadKind.Broker, Target: "ZZ" } && l.Reason.Contains("kedalaman"));
        Assert.Contains(facts.LimitsReached, l => l.Contains("kedalaman 1"));
    }

    [Fact]
    public async Task A_spent_budget_stops_new_workers_but_still_produces_facts()
    {
        var used = 0;
        var budget = new JobBudget(2, DateTime.UtcNow.AddHours(1), () => used, () => used++);
        var ran = new List<WorkerStartedPayload>();
        var orchestrator = new PatternOrchestrator(Job(), budget, async (w, ct) =>
        {
            lock (ran) ran.Add(w);
            budget.TryUse(); // each worker spends one call
            return await Script(w, ct);
        }, _ => { });

        var facts = await orchestrator.RunAsync(default);

        Assert.True(ran.Count <= 3);
        Assert.Contains(facts.LimitsReached, l => l.Contains("Batas 2 panggilan data"));
        Assert.Contains(facts.LeadsNotFollowed, l => l.Reason.StartsWith("Tidak dijalankan", StringComparison.Ordinal));
    }
}
