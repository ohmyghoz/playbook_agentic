using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Surveillance.Web.Investigations;

namespace Surveillance.Web.Data.Repositories;

/// <summary>EF Core implementation of <see cref="ICaseRepository"/>.</summary>
public sealed class CaseRepository(IDbContextFactory<SurveillanceDbContext> dbFactory) : ICaseRepository
{
    public async Task AddAsync(InvestigationCase @case, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Cases.Add(@case);
        await db.SaveChangesAsync(ct);
    }

    public async Task<InvestigationCase?> GetAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Cases.AsNoTracking()
            .Include(c => c.Decisions.OrderBy(d => d.DecidedAt))
            .SingleOrDefaultAsync(c => c.Id == caseId, ct);
    }

    public async Task<List<InvestigationCase>> ListRecentAsync(int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Cases.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(take).ToListAsync(ct);
    }

    public async Task<List<InvestigationCase>> ListAwaitingApprovalAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Cases.AsNoTracking()
            .Where(c => c.Status == CaseStatus.AwaitingApproval && c.AwaitingSince != null)
            .OrderBy(c => c.AwaitingSince)
            .ToListAsync(ct);
    }

    public async Task<List<ActiveRun>> ListActiveRunsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Cases.AsNoTracking()
            .Where(c => c.RunId != null && (c.Status == CaseStatus.Running || c.Status == CaseStatus.Starting))
            .Select(c => new ActiveRun(c.Id, c.RunId!, c.Provider))
            .ToListAsync(ct);
    }

    public async Task SetRunIdAsync(Guid caseId, string runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var @case = await db.Cases.SingleAsync(c => c.Id == caseId, ct);
        @case.RunId = runId;
        @case.Status = CaseStateMachine.Move(@case.Status, CaseStatus.Running);
        @case.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<CaseCursor> GetCursorAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Cases.AsNoTracking().Where(c => c.Id == caseId)
            .Select(c => new CaseCursor(c.Status, c.LastSequence)).SingleAsync(ct);
    }
}

/// <summary>EF Core implementation of <see cref="IEventRepository"/>.</summary>
public sealed class EventRepository(IDbContextFactory<SurveillanceDbContext> dbFactory) : IEventRepository
{
    private static readonly CaseStatus[] Final =
        [CaseStatus.Approved, CaseStatus.Rejected, CaseStatus.Cancelled, CaseStatus.Failed, CaseStatus.Expired];

    public async Task<bool> AppendAsync(AgentEventRecord record, ToolResultPreview? preview, CaseProgress progress, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Events.Add(record);
        if (preview is not null)
        {
            db.Previews.Add(preview);
        }

        // Update only the columns that change, without reading the row first (docs/NOTES-PERSIST.md).
        var stub = new InvestigationCase { Id = record.CaseId, Symbol = "", TriggerJson = "", Provider = "" };
        db.Cases.Attach(stub);
        stub.Status = progress.Status;
        stub.LastSequence = progress.LastSequence;
        stub.UpdatedAt = DateTime.UtcNow;
        var entry = db.Entry(stub);
        entry.Property(x => x.Status).IsModified = true;
        entry.Property(x => x.LastSequence).IsModified = true;
        entry.Property(x => x.UpdatedAt).IsModified = true;
        if (progress.AwaitingSinceChanged)
        {
            stub.AwaitingSince = progress.AwaitingSince;
            entry.Property(x => x.AwaitingSince).IsModified = true;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            await using var check = await dbFactory.CreateDbContextAsync(ct);
            if (await check.Events.AnyAsync(x => x.RunId == record.RunId && x.Sequence == record.Sequence, ct))
            {
                return false; // another writer stored this sequence first (unique index)
            }

            throw;
        }
    }

    public async Task<List<AgentEventRecord>> GetAfterAsync(Guid caseId, long after, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Events.AsNoTracking()
            .Where(e => e.CaseId == caseId && e.Sequence > after)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
    }

    public async Task<string?> LatestApprovalPayloadAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Events
            .Where(e => e.CaseId == caseId && e.Type == "approval_required")
            .OrderByDescending(e => e.Sequence)
            .Select(e => e.PayloadJson)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ToolResultPreview?> GetPreviewAsync(Guid caseId, string toolCallId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Previews.AsNoTracking()
            .Where(p => p.CaseId == caseId && p.ToolCallId == toolCallId)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int> DeletePreviewsOlderThanAsync(DateTime cutoff, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Previews.Where(p => p.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    public async Task<(int Cases, int EventsRemoved)> CompactTokensAsync(DateTime finishedBefore, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var caseIds = await db.Cases
            .Where(c => Final.Contains(c.Status) && c.UpdatedAt < finishedBefore && c.TokensCompactedAt == null)
            .Select(c => c.Id).ToListAsync(ct);

        var removed = 0;
        foreach (var caseId in caseIds)
        {
            var events = await db.Events.Where(e => e.CaseId == caseId).OrderBy(e => e.Sequence).ToListAsync(ct);
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].Type != "token")
                {
                    continue;
                }

                // Fold the following consecutive tokens into this one; their sequences are freed.
                var text = new System.Text.StringBuilder(TokenText(events[i]));
                var j = i + 1;
                while (j < events.Count && events[j].Type == "token" && events[j].Sequence == events[j - 1].Sequence + 1)
                {
                    text.Append(TokenText(events[j]));
                    db.Events.Remove(events[j]);
                    removed++;
                    j++;
                }

                if (j > i + 1)
                {
                    events[i].PayloadJson = JsonSerializer.Serialize(new { text = text.ToString() });
                }

                i = j - 1;
            }

            var @case = await db.Cases.SingleAsync(c => c.Id == caseId, ct);
            @case.TokensCompactedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return (caseIds.Count, removed);
    }

    private static string TokenText(AgentEventRecord e) =>
        JsonDocument.Parse(e.PayloadJson).RootElement.GetProperty("text").GetString() ?? "";

    public async Task<(int Cases, int EventsRemoved)> PurgeEventsAsync(DateTime finishedBefore, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var caseIds = await db.Cases
            .Where(c => Final.Contains(c.Status) && c.UpdatedAt < finishedBefore && c.EventsPurgedAt == null)
            .Select(c => c.Id).ToListAsync(ct);

        var removed = 0;
        foreach (var caseId in caseIds)
        {
            removed += await db.Events.Where(e => e.CaseId == caseId).ExecuteDeleteAsync(ct);
            await db.Previews.Where(p => p.CaseId == caseId).ExecuteDeleteAsync(ct);
            await db.Cases.Where(c => c.Id == caseId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.EventsPurgedAt, DateTime.UtcNow), ct);
        }

        return (caseIds.Count, removed);
    }
}

/// <summary>EF Core implementation of <see cref="IApprovalRepository"/>.</summary>
public sealed class ApprovalRepository(IDbContextFactory<SurveillanceDbContext> dbFactory) : IApprovalRepository
{
    public async Task<bool> TryDecideAsync(ApprovalDecisionRecord decision, CaseStatus running, DateTime claimCutoff, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var who = decision.DecidedBy;
        // A user transaction must run inside the execution strategy when retry on failure is on.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.Cases
                .Where(c => c.Id == decision.CaseId && c.Status == CaseStatus.AwaitingApproval
                            && (c.ClaimedBy == null || c.ClaimedBy == who || c.ClaimActivityAt < claimCutoff))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, running)
                    .SetProperty(c => c.AwaitingSince, (DateTime?)null)
                    .SetProperty(c => c.ClaimedBy, (string?)null)
                    .SetProperty(c => c.ClaimedByUser, (string?)null)
                    .SetProperty(c => c.ClaimActivityAt, (DateTime?)null)
                    .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), ct);
            if (changed == 0)
            {
                return false; // someone else decided or holds the case
            }

            db.Decisions.Add(decision);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
    }

    public async Task<string?> WinnerOfAsync(Guid caseId, string approvalId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Decisions.AsNoTracking()
            .Where(d => d.CaseId == caseId && d.ApprovalId == approvalId)
            .Select(d => d.DecidedBy)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> TryClaimAsync(Guid caseId, string user, string displayName, DateTime claimCutoff, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Cases
            .Where(c => c.Id == caseId && c.Status == CaseStatus.AwaitingApproval
                        && (c.ClaimedBy == null || c.ClaimedBy == displayName || c.ClaimActivityAt < claimCutoff))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ClaimedBy, displayName)
                .SetProperty(c => c.ClaimedByUser, user)
                .SetProperty(c => c.ClaimActivityAt, DateTime.UtcNow), ct) == 1;
    }

    public async Task<bool> TryReleaseAsync(Guid caseId, string displayName, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Cases
            .Where(c => c.Id == caseId && c.ClaimedBy == displayName)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ClaimedBy, (string?)null)
                .SetProperty(c => c.ClaimedByUser, (string?)null)
                .SetProperty(c => c.ClaimActivityAt, (DateTime?)null), ct) == 1;
    }

    public async Task<bool> TryAssignAsync(Guid caseId, string user, string displayName, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Cases
            .Where(c => c.Id == caseId && c.Status == CaseStatus.AwaitingApproval)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ClaimedBy, displayName)
                .SetProperty(c => c.ClaimedByUser, user)
                .SetProperty(c => c.ClaimActivityAt, DateTime.UtcNow), ct) == 1;
    }

    public async Task<bool> TryExpireAsync(Guid caseId, DateTime awaitingSince, CaseStatus expired, string actor, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.Cases
                .Where(x => x.Id == caseId && x.Status == CaseStatus.AwaitingApproval && x.AwaitingSince == awaitingSince)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, expired)
                    .SetProperty(x => x.AwaitingSince, (DateTime?)null)
                    .SetProperty(x => x.ClaimedBy, (string?)null)
                    .SetProperty(x => x.ClaimedByUser, (string?)null)
                    .SetProperty(x => x.ClaimActivityAt, (DateTime?)null)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);
            if (changed == 0)
            {
                return false; // decided, stopped or revised meanwhile
            }

            var payload = await db.Events
                .Where(e => e.CaseId == caseId && e.Type == "approval_required")
                .OrderByDescending(e => e.Sequence)
                .Select(e => e.PayloadJson)
                .FirstAsync(ct);
            db.Decisions.Add(new ApprovalDecisionRecord
            {
                CaseId = caseId,
                ApprovalId = JsonDocument.Parse(payload).RootElement.GetProperty("approvalId").GetString()!,
                Decision = "expire",
                Comment = reason,
                DecidedBy = actor,
                DecidedAt = DateTime.UtcNow,
            });
            db.Audit.Add(new CaseAuditRecord { CaseId = caseId, At = DateTime.UtcNow, Actor = actor, Action = "expire", Detail = reason });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
    }

    public async Task AddAuditAsync(CaseAuditRecord record, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Audit.Add(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ApprovalDecisionRecord>> ListDecisionsAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Decisions.AsNoTracking().Where(d => d.CaseId == caseId).OrderBy(d => d.DecidedAt).ToListAsync(ct);
    }

    public async Task<List<CaseAuditRecord>> ListAuditAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Audit.AsNoTracking().Where(a => a.CaseId == caseId).OrderBy(a => a.At).ToListAsync(ct);
    }
}

/// <summary>EF Core implementation of <see cref="ISettingsRepository"/>.</summary>
public sealed class SettingsRepository(IDbContextFactory<SurveillanceDbContext> dbFactory) : ISettingsRepository
{
    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(ct);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }
}
