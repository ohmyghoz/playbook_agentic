using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Agents;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;
using Xunit.Abstractions;

namespace Surveillance.Web.Tests.Benchmark;

/// <summary>Skipped unless RUN_BENCHMARK=1. Measures against the database in the web app's user-secrets.</summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_BENCHMARK") != "1")
        {
            Skip = "Benchmark: set RUN_BENCHMARK=1 (uses ConnectionStrings:Default from user-secrets).";
        }

        Timeout = 600_000;
    }
}

/// <summary>
/// Time per event in RunPump.PersistAsync against the real database (Azure SQL during
/// development). Results go to docs/NOTES-PERSIST.md as material for chapter 10.
/// </summary>
[Trait("Category", "Benchmark")]
public class PersistBenchmark(ITestOutputHelper output)
{
    private const int Warmup = 10;
    private const int Measured = 200;

    [BenchmarkFact]
    public async Task Persist_time_per_event()
    {
        var config = new ConfigurationBuilder().AddUserSecrets("127fa63b-a132-4b5b-bb3c-a2a4ca332f06").Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(config)
            .AddDbContextFactory<SurveillanceDbContext>(o => o.UseSqlServer(config.GetConnectionString("Default"),
                sql => sql.EnableRetryOnFailure()))
            .BuildServiceProvider();
        var dbFactory = services.GetRequiredService<IDbContextFactory<SurveillanceDbContext>>();
        var pump = new RunPump(new AgentClientRegistry(services, config), dbFactory, new EventBroadcaster(),
            NullLogger<RunPump>.Instance);

        var caseId = Guid.NewGuid();
        var runId = $"bench-{caseId:N}";
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Cases.Add(new InvestigationCase
            {
                Id = caseId, Symbol = "BBCA", AnomalyDate = new DateOnly(2026, 11, 24), TriggerJson = "{}",
                Provider = "fake", RunId = runId, Status = CaseStatus.Running,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Notes = "benchmark",
            });
            await db.SaveChangesAsync();
        }

        try
        {
            var timings = new List<double>();
            for (var seq = 1; seq <= Warmup + Measured; seq++)
            {
                var e = AgentEvent.Create(runId, seq, AgentEventType.Token, new TokenPayload($"potongan teks {seq} "));
                var watch = Stopwatch.StartNew();
                await pump.PersistAsync(caseId, e);
                watch.Stop();
                if (seq > Warmup)
                {
                    timings.Add(watch.Elapsed.TotalMilliseconds);
                }
            }

            timings.Sort();
            var report = $"events={timings.Count} mean={timings.Average():F1} ms median={timings[timings.Count / 2]:F1} ms " +
                         $"p95={timings[(int)(timings.Count * 0.95)]:F1} ms max={timings[^1]:F1} ms";
            output.WriteLine(report);
            await File.AppendAllTextAsync(Path.Combine(Path.GetTempPath(), "persist-benchmark.txt"),
                $"{DateTime.UtcNow:O} {report}{Environment.NewLine}");
        }
        finally
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.Events.Where(x => x.CaseId == caseId).ExecuteDeleteAsync();
            await db.Cases.Where(x => x.Id == caseId).ExecuteDeleteAsync();
        }
    }
}
