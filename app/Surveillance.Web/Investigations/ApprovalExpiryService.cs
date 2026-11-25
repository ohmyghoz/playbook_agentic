using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Surveillance.Web.Data;

namespace Surveillance.Web.Investigations;

/// <summary>
/// The application, not the agent, decides when an approval has waited too long (Keputusan
/// kontrak: no contract change). Every Approval:CheckSeconds it looks for drafts past their
/// deadline, marks each case Expired with a recorded reason, then stops the agent run through
/// the same path as the stop button. A decision that lands first wins: the expiry is a
/// conditional update on the same AwaitingSince it read.
/// </summary>
public sealed class ApprovalExpiryService(
    IDbContextFactory<SurveillanceDbContext> dbFactory,
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
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var waiting = await db.Cases.AsNoTracking()
            .Where(c => c.Status == CaseStatus.AwaitingApproval && c.AwaitingSince != null)
            .ToListAsync(ct);

        var expired = new List<Guid>();
        foreach (var c in waiting.Where(c => options.Value.DeadlineFor(c) <= now))
        {
            if (await ExpireAsync(db, c, ct))
            {
                expired.Add(c.Id);
            }
        }

        return expired;
    }

    private async Task<bool> ExpireAsync(SurveillanceDbContext db, InvestigationCase c, CancellationToken ct)
    {
        var timeout = options.Value.TimeoutFor(c);
        var reason = $"Tidak ada keputusan analis dalam {Describe(timeout)} sejak draf diajukan. Aplikasi menghentikan investigasi.";
        var status = CaseStateMachine.Move(CaseStatus.AwaitingApproval, CaseStatus.Expired);

        var done = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.Cases
                .Where(x => x.Id == c.Id && x.Status == CaseStatus.AwaitingApproval && x.AwaitingSince == c.AwaitingSince)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.AwaitingSince, (DateTime?)null)
                    .SetProperty(x => x.ClaimedBy, (string?)null)
                    .SetProperty(x => x.ClaimedByUser, (string?)null)
                    .SetProperty(x => x.ClaimActivityAt, (DateTime?)null)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);
            if (changed == 0)
            {
                return false; // decided, stopped or revised meanwhile
            }

            var approvalId = await db.Events
                .Where(e => e.CaseId == c.Id && e.Type == "approval_required")
                .OrderByDescending(e => e.Sequence)
                .Select(e => e.PayloadJson)
                .FirstAsync(ct);
            db.Decisions.Add(new ApprovalDecisionRecord
            {
                CaseId = c.Id,
                ApprovalId = System.Text.Json.JsonDocument.Parse(approvalId).RootElement.GetProperty("approvalId").GetString()!,
                Decision = "expire",
                Comment = reason,
                DecidedBy = SystemActor,
                DecidedAt = DateTime.UtcNow,
            });
            db.Audit.Add(new CaseAuditRecord { CaseId = c.Id, At = DateTime.UtcNow, Actor = SystemActor, Action = "expire", Detail = reason });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });

        if (done && c.RunId is not null)
        {
            logger.LogInformation("Approval of case {CaseId} expired after {Timeout}", c.Id, timeout);
            _ = pump.Stop(c.Id, c.RunId, c.Provider); // the agent closes the run with done(cancelled)
        }

        return done;
    }

    private static string Describe(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.#} jam" : $"{t.TotalMinutes:0} menit";
}
