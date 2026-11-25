using System.Text.Json;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;

namespace Surveillance.Web.Investigations;

/// <summary>Converts between catalog events and their database rows.</summary>
public static class EventMapping
{
    /// <summary>The snake_case wire name of an enum value, for example <c>tool_call_started</c>.</summary>
    public static string EnumName<T>(T value) where T : struct, Enum =>
        JsonSerializer.Serialize(value, AgentJson.Options).Trim('"');

    public static AgentEventType ParseType(string name) =>
        JsonSerializer.Deserialize<AgentEventType>($"\"{name}\"", AgentJson.Options);

    public static AgentEventRecord ToRecord(Guid caseId, AgentEvent e) => new()
    {
        CaseId = caseId,
        RunId = e.RunId,
        Sequence = e.Sequence,
        Type = EnumName(e.Type),
        AgentName = e.AgentName,
        Timestamp = e.Timestamp,
        PayloadJson = e.Payload.GetRawText(),
        ReceivedAt = DateTime.UtcNow,
    };

    public static AgentEvent ToEvent(AgentEventRecord r)
    {
        using var doc = JsonDocument.Parse(r.PayloadJson);
        return new AgentEvent(r.RunId, r.Sequence, r.Timestamp, ParseType(r.Type), doc.RootElement.Clone(), r.AgentName);
    }

    public static bool IsTerminal(AgentEventRecord r) => r.Type is "done" or "error";
}
