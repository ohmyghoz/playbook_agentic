using System.Text.Json;
using System.Text.Json.Serialization;

namespace Surveillance.Agents.Abstractions;

public enum AgentEventType
{
    RunStarted,
    StepStarted,
    Token,
    ToolCallStarted,
    ToolCallFinished,
    ApprovalRequired,
    WorkerStarted,
    WorkerFinished,
    Done,
    Error,
}

/// <summary>
/// One event from the catalog. <see cref="Payload"/> holds the type-specific
/// object exactly as it appears on the wire.
/// </summary>
/// <param name="Timestamp">UTC; serialized with a trailing Z as the schema requires.</param>
/// <param name="AgentName">Which agent of a multi-agent run emitted the event (CHANGE-002); null for UMA.</param>
public sealed record AgentEvent(
    string RunId,
    long Sequence,
    DateTime Timestamp,
    AgentEventType Type,
    JsonElement Payload,
    string? AgentName = null)
{
    public static AgentEvent Create<TPayload>(string runId, long sequence, AgentEventType type, TPayload payload, string? agentName = null) =>
        new(runId, sequence, DateTime.UtcNow, type, JsonSerializer.SerializeToElement(payload, AgentJson.Options), agentName);

    public TPayload PayloadAs<TPayload>() =>
        Payload.Deserialize<TPayload>(AgentJson.Options)
        ?? throw new JsonException($"Payload of {Type} event {Sequence} is null.");

    [JsonIgnore]
    public bool IsTerminal => Type is AgentEventType.Done or AgentEventType.Error;

    public string ToJson() => JsonSerializer.Serialize(this, AgentJson.Options);
}
