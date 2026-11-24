using System.Text.Json;
using System.Text.Json.Serialization;

namespace Surveillance.Agents.Abstractions;

public enum AgentEventType
{
    RunStarted,
    Token,
    ToolCallStarted,
    ToolCallFinished,
    ApprovalRequired,
    Done,
    Error,
}

/// <summary>
/// One event from the catalog. <see cref="Payload"/> holds the type-specific
/// object exactly as it appears on the wire.
/// </summary>
/// <param name="Timestamp">UTC; serialized with a trailing Z as the schema requires.</param>
public sealed record AgentEvent(
    string RunId,
    long Sequence,
    DateTime Timestamp,
    AgentEventType Type,
    JsonElement Payload)
{
    public static AgentEvent Create<TPayload>(string runId, long sequence, AgentEventType type, TPayload payload) =>
        new(runId, sequence, DateTime.UtcNow, type, JsonSerializer.SerializeToElement(payload, AgentJson.Options));

    public TPayload PayloadAs<TPayload>() =>
        Payload.Deserialize<TPayload>(AgentJson.Options)
        ?? throw new JsonException($"Payload of {Type} event {Sequence} is null.");

    [JsonIgnore]
    public bool IsTerminal => Type is AgentEventType.Done or AgentEventType.Error;

    public string ToJson() => JsonSerializer.Serialize(this, AgentJson.Options);
}
