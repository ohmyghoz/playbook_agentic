using System.Text.Json;
using System.Threading.Channels;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Agents;
using Surveillance.Web.Data;
using Surveillance.Web.Data.Repositories;
using Surveillance.Web.Investigations;

namespace Surveillance.Web.Patterns;

public enum JobStartResult
{
    Queued,
    UnsupportedSolution,
}

/// <param name="CaseId">Set when queued.</param>
public sealed record JobStart(JobStartResult Result, Guid? CaseId = null, string? SolutionName = null);

/// <summary>
/// Runs use case 2 jobs in the background, independent of any browser: the request only creates
/// the case and queues it; this service screens candidates (ordinary code, <see cref="CandidateScreening"/>),
/// stores the job and starts the agent run, which <see cref="RunPump"/> then follows like any other.
/// Jobs still waiting for screening after a restart are queued again at start.
/// </summary>
public sealed class PatternJobRunner(
    InvestigationService investigations,
    AgentSelection selection,
    AgentClientRegistry registry,
    ICaseRepository cases,
    CandidateScreening screening,
    ILogger<PatternJobRunner> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();

    public async Task<JobStart> StartAsync(string user, string displayName, CancellationToken ct)
    {
        var active = await selection.GetActiveAsync(ct);
        var provider = registry.Providers.Single(p => p.Key == active);
        if (!provider.SupportsPatternDetection)
        {
            return new(JobStartResult.UnsupportedSolution, SolutionName: provider.Name);
        }

        var caseId = await investigations.CreateJobAsync(user, displayName, ct);
        await _queue.Writer.WriteAsync(caseId, ct);
        return new(JobStartResult.Queued, caseId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var waiting in await cases.ListJobsWaitingForScreeningAsync(stoppingToken))
        {
            await _queue.Writer.WriteAsync(waiting, stoppingToken);
        }

        await foreach (var caseId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(caseId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Job {CaseId} failed before reaching the agent", caseId);
                await investigations.FailBeforeStartAsync(caseId, AgentErrorCode.Internal,
                    "Job gagal disiapkan. Mulai job baru untuk mencoba lagi.");
            }
        }
    }

    private async Task RunAsync(Guid caseId, CancellationToken ct)
    {
        var @case = await cases.GetAsync(caseId, ct);
        if (@case is null || @case.RunId is not null)
        {
            return;
        }

        ScreeningResult result;
        try
        {
            result = await screening.ScreenAsync(ct);
        }
        catch (MarketDataException ex)
        {
            logger.LogWarning(ex, "Screening failed for job {CaseId}", caseId);
            await investigations.FailBeforeStartAsync(caseId, AgentErrorCode.McpUnavailable,
                $"Penyaringan awal tidak bisa membaca data pasar: {ex.Message} Pastikan server data pasar berjalan, lalu mulai job baru.");
            return;
        }

        if (result.Candidates.Count == 0)
        {
            await investigations.FailBeforeStartAsync(caseId, AgentErrorCode.InvalidInput,
                "Tidak ada saham yang melewati ambang penyaringan pada periode ini, jadi agent tidak dijalankan.");
            return;
        }

        var job = screening.ToJob(result);
        await cases.SetJobAsync(caseId, JsonSerializer.Serialize(job, AgentJson.Options), ct);
        var first = job.Candidates[0];
        var trigger = JsonSerializer.Deserialize<AnomalyTrigger>(@case.TriggerJson, AgentJson.Options)!;
        var request = new InvestigationRequest(caseId.ToString(), first.Symbol, job.WindowEnd, trigger, @case.Notes,
            UseCase.PatternDetection, job);
        await investigations.LaunchAsync(@case, request, ct);
    }
}
