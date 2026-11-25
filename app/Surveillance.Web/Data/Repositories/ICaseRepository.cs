namespace Surveillance.Web.Data.Repositories;

/// <summary>A run the application was streaming, to re-attach after a restart.</summary>
public sealed record ActiveRun(Guid CaseId, string RunId, string Provider);

/// <summary>The case's status and last stored sequence, as RunPump needs them per event.</summary>
public sealed record CaseCursor(CaseStatus Status, long LastSequence);

/// <summary>Investigation cases: the application's source of truth for what was investigated.</summary>
public interface ICaseRepository
{
    Task AddAsync(InvestigationCase @case, CancellationToken ct);

    /// <summary>The case with its decisions (oldest first), or null.</summary>
    Task<InvestigationCase?> GetAsync(Guid caseId, CancellationToken ct);

    Task<List<InvestigationCase>> ListRecentAsync(int take, CancellationToken ct);

    /// <summary>Cases waiting for an analyst, the longest waiting first.</summary>
    Task<List<InvestigationCase>> ListAwaitingApprovalAsync(CancellationToken ct);

    /// <summary>Runs that were starting or streaming, which StartupRecovery re-attaches.</summary>
    Task<List<ActiveRun>> ListActiveRunsAsync(CancellationToken ct);

    /// <summary>Stores the agent's run id and moves the case from Starting to Running.</summary>
    Task SetRunIdAsync(Guid caseId, string runId, CancellationToken ct);

    Task<CaseCursor> GetCursorAsync(Guid caseId, CancellationToken ct);

    // ---- use case 2 jobs ----

    /// <summary>UMA cases only (UseCase null), newest first.</summary>
    Task<List<InvestigationCase>> ListInvestigationsAsync(int take, CancellationToken ct);

    /// <summary>Pattern-detection jobs, newest first.</summary>
    Task<List<InvestigationCase>> ListJobsAsync(int take, CancellationToken ct);

    /// <summary>Jobs created but not yet started at the agent (screening pending, e.g. after a restart).</summary>
    Task<List<Guid>> ListJobsWaitingForScreeningAsync(CancellationToken ct);

    Task SetJobAsync(Guid caseId, string jobJson, CancellationToken ct);

    /// <summary>The user's jobs that wait for an analyst decision.</summary>
    Task<int> CountJobsAwaitingAsync(string user, CancellationToken ct);
}
