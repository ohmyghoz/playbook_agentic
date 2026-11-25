using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;

namespace Surveillance.Web.Investigations;

/// <param name="Source">agent (an event), analyst (a decision) or application (claim, assign, expire).</param>
public sealed record AuditEntry(DateTime At, string Source, string Actor, string Action, string Detail, long? Sequence = null);

/// <summary>
/// One timeline per case for the question "who decided what, and when": agent events, analyst
/// decisions and the application's own actions (claims, assignments, expiry), ordered by time.
/// Keputusan kontrak: decisions stay out of the event stream; the application merges them here.
/// Consecutive token events become one narration entry so the timeline stays readable.
/// </summary>
public sealed class AuditTimeline(IDbContextFactory<SurveillanceDbContext> dbFactory)
{
    public async Task<List<AuditEntry>?> BuildAsync(Guid caseId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var @case = await db.Cases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == caseId, ct);
        if (@case is null)
        {
            return null;
        }

        var events = await db.Events.AsNoTracking().Where(e => e.CaseId == caseId).OrderBy(e => e.Sequence).ToListAsync(ct);
        var decisions = await db.Decisions.AsNoTracking().Where(d => d.CaseId == caseId).ToListAsync(ct);
        var audit = await db.Audit.AsNoTracking().Where(a => a.CaseId == caseId).ToListAsync(ct);
        var agent = $"Agent ({@case.Provider})";

        var entries = new List<AuditEntry>
        {
            new(@case.CreatedAt, "application", "Aplikasi", "case_created",
                $"Kasus {@case.Symbol} tanggal {@case.AnomalyDate:yyyy-MM-dd} dibuat dengan solusi {@case.Provider}."),
        };

        var narration = new StringBuilder();
        AgentEventRecord? narrationStart = null;
        void FlushNarration()
        {
            if (narrationStart is not null)
            {
                entries.Add(new(narrationStart.Timestamp, "agent", agent, "narration", narration.ToString().Trim(), narrationStart.Sequence));
            }

            narration.Clear();
            narrationStart = null;
        }

        foreach (var record in events)
        {
            var e = EventMapping.ToEvent(record);
            if (e.Type == AgentEventType.Token)
            {
                narrationStart ??= record;
                narration.Append(e.PayloadAs<TokenPayload>().Text);
                continue;
            }

            FlushNarration();
            entries.Add(new(record.Timestamp, "agent", agent, record.Type, Describe(e), record.Sequence));
        }

        FlushNarration();
        entries.AddRange(decisions.Select(d => new AuditEntry(d.DecidedAt,
            d.DecidedBy == ApprovalExpiryService.SystemActor ? "application" : "analyst", d.DecidedBy, "decision_" + d.Decision,
            $"{Models.CaseStatusText.DecisionLabel(d.Decision)} untuk {d.ApprovalId}" + (d.Comment is null ? "." : $": \"{d.Comment}\""))));
        entries.AddRange(audit.Where(a => a.Action is not "expire").Select(a => new AuditEntry(a.At, "application", a.Actor, a.Action, a.Detail ?? Describe(a.Action))));

        return entries.OrderBy(x => x.At).ThenBy(x => x.Sequence ?? long.MaxValue).ToList();
    }

    private static string Describe(string action) => action switch
    {
        "claim" => "Mengambil kasus untuk ditelaah.",
        "release" => "Melepas kasus.",
        _ => action,
    };

    private static string Describe(AgentEvent e) => e.Type switch
    {
        AgentEventType.RunStarted => "Run dimulai.",
        AgentEventType.StepStarted => $"Tahap: {e.PayloadAs<StepStartedPayload>().Label}.",
        AgentEventType.ToolCallStarted => $"Memanggil {e.PayloadAs<ToolCallStartedPayload>().ToolName}.",
        AgentEventType.ToolCallFinished => e.PayloadAs<ToolCallFinishedPayload>() is var f
            ? $"{f.ToolName} {(f.Status == ToolCallStatus.Ok ? "selesai" : "gagal")}: {f.Summary}"
            : "",
        AgentEventType.ApprovalRequired => e.PayloadAs<ApprovalRequiredPayload>() is var a
            ? $"Mengajukan draf memo {a.ApprovalId} (revisi {a.Revision}): {a.Memo.Summary}"
            : "",
        AgentEventType.Done => $"Run selesai: {e.PayloadAs<DonePayload>().Outcome.ToString().ToLowerInvariant()}.",
        AgentEventType.Error => e.PayloadAs<ErrorPayload>() is var err ? $"Galat {EventMapping.EnumName(err.Code)}: {err.Message}" : "",
        _ => e.Payload.GetRawText(),
    };

    public static string ToJson(Guid caseId, IEnumerable<AuditEntry> entries) =>
        JsonSerializer.Serialize(new { caseId, exportedAt = DateTime.UtcNow, entries }, AgentJson.Options);
}
