using Microsoft.Extensions.Options;
using Surveillance.Web.Data;
using Surveillance.Web.Data.Repositories;

namespace Surveillance.Web.Investigations;

/// <summary>
/// The application, not the agent, decides when an approval has waited too long (Keputusan
/// kontrak: no contract change). Every Approval:CheckSeconds it looks for drafts past their
/// deadline, marks each case Expired with a recorded reason, then stops the agent run through
/// the same path as the stop button. A decision that lands first wins: the expiry is a
/// conditional update on the same AwaitingSince it read (<see cref="IApprovalRepository.TryExpireAsync"/>).
/// </summary>
public sealed class ApprovalExpiryService(
    ICaseRepository cases,
    IApprovalRepository approvals,
    RunPump pump,
    IOptions<ApprovalOptions> options,
    ILogger<ApprovalExpiryService> logger) : BackgroundService
{
    public const string SystemActor = "Sistem";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.CheckSeconds));
        do
        {
            try
            {
                await ExpireDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Approval expiry check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <returns>The cases that expired in this pass.</returns>
    public async Task<List<Guid>> ExpireDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expired = new List<Guid>();
        foreach (var c in (await cases.ListAwaitingApprovalAsync(ct)).Where(c => options.Value.DeadlineFor(c) <= now))
        {
            var timeout = options.Value.TimeoutFor(c);
            var reason = $"Tidak ada keputusan analis dalam {Describe(timeout)} sejak draf diajukan. Aplikasi menghentikan investigasi.";
            var status = CaseStateMachine.Move(CaseStatus.AwaitingApproval, CaseStatus.Expired);
            if (!await approvals.TryExpireAsync(c.Id, c.AwaitingSince!.Value, status, SystemActor, reason, ct))
            {
                continue; // decided, stopped or revised meanwhile
            }

            expired.Add(c.Id);
            logger.LogInformation("Approval of case {CaseId} expired after {Timeout}", c.Id, timeout);
            if (c.RunId is not null)
            {
                _ = pump.Stop(c.Id, c.RunId, c.Provider); // the agent closes the run with done(cancelled)
            }
        }

        return expired;
    }

    private static string Describe(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.#} jam" : $"{t.TotalMinutes:0} menit";
}
