using System.Text.Json;
using Microsoft.Extensions.AI;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Agents.Maf;

/// <summary>Checks the memo the model passed to submit_memo before it reaches the analyst.</summary>
internal static class MemoValidator
{
    /// <returns>The memo, or null with a reason the model can act on.</returns>
    public static (Memo? Memo, string? Problem) Validate(FunctionCallContent call, InvestigationRequest request,
        IReadOnlySet<string> knownToolCallIds)
    {
        if (call.Arguments is null || !call.Arguments.TryGetValue("memo", out var raw) || raw is null)
        {
            return (null, "Argumen 'memo' tidak ada.");
        }

        Memo? memo;
        try
        {
            var json = raw is JsonElement e ? e.GetRawText() : JsonSerializer.Serialize(raw, AgentJson.Options);
            memo = JsonSerializer.Deserialize<Memo>(json, AgentJson.Options);
        }
        catch (JsonException ex)
        {
            return (null, $"Memo tidak bisa dibaca: {ex.Message}");
        }

        if (memo is null)
        {
            return (null, "Memo kosong.");
        }

        var problems = new List<string>();
        if (memo.Symbol != request.Symbol) problems.Add($"symbol harus {request.Symbol}");
        if (memo.AnomalyDate != request.AnomalyDate) problems.Add($"anomalyDate harus {request.AnomalyDate:yyyy-MM-dd}");
        if (string.IsNullOrWhiteSpace(memo.Summary)) problems.Add("summary kosong");
        if (memo.Anomaly is null || string.IsNullOrWhiteSpace(memo.Anomaly.Description)) problems.Add("anomaly.description kosong");
        if (memo.Findings is null) problems.Add("findings tidak ada");
        if (memo.UnavailableData is null) problems.Add("unavailableData tidak ada (pakai [] jika tidak ada)");
        if (string.IsNullOrWhiteSpace(memo.EvidenceRationale)) problems.Add("evidenceRationale kosong");
        if (string.IsNullOrWhiteSpace(memo.RecommendationRationale)) problems.Add("recommendationRationale kosong");

        foreach (var finding in memo.Findings ?? [])
        {
            if (finding.Evidence is not { Count: > 0 })
            {
                problems.Add($"temuan \"{finding.Statement}\" tidak punya evidence");
                continue;
            }

            foreach (var ev in finding.Evidence.Where(ev => !knownToolCallIds.Contains(ev.ToolCallId)))
            {
                problems.Add($"toolCallId {ev.ToolCallId} tidak ada di run ini");
            }
        }

        if (problems.Count > 0)
        {
            return (null, "Memo belum sesuai skema: " + string.Join("; ", problems) + ".");
        }

        // The disclaimer is fixed text; set it rather than reject a paraphrase.
        return (memo with { Disclaimer = Memo.StandardDisclaimer }, null);
    }
}
