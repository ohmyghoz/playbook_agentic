using System.Text.Json;
using System.Text.Json.Nodes;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;

namespace Surveillance.Web.Investigations;

/// <summary>
/// Takes <c>resultPreview</c> out of a <c>tool_call_finished</c> event before it is stored and
/// broadcast. The event keeps <c>summary</c> and <c>source</c>; the raw output goes to its own
/// table and is served only when an analyst opens the evidence (CHANGE-001).
/// </summary>
public static class ResultPreviewSplitter
{
    public static (AgentEvent Event, ToolResultPreview? Preview) Split(Guid caseId, AgentEvent e)
    {
        if (e.Type != AgentEventType.ToolCallFinished
            || !e.Payload.TryGetProperty("resultPreview", out var preview)
            || preview.ValueKind != JsonValueKind.String)
        {
            return (e, null);
        }

        var payload = JsonNode.Parse(e.Payload.GetRawText())!.AsObject();
        payload.Remove("resultPreview");
        var trimmed = e with { Payload = JsonSerializer.SerializeToElement(payload, AgentJson.Options) };

        return (trimmed, new ToolResultPreview
        {
            CaseId = caseId,
            RunId = e.RunId,
            ToolCallId = payload["toolCallId"]!.GetValue<string>(),
            ToolName = payload["toolName"]!.GetValue<string>(),
            Preview = preview.GetString()!,
            CreatedAt = DateTime.UtcNow,
        });
    }
}
