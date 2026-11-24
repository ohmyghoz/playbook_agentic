using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Surveillance.Agents.Maf;

namespace Surveillance.Web.Tests.Maf;

/// <summary>
/// A model stand-in: each request gets the next scripted turn. The real MAF
/// pipeline (function invocation, approvals, middleware) runs around it.
/// </summary>
public sealed class ScriptedChatClient(params Func<IList<ChatMessage>, IEnumerable<AIContent>>[] turns) : IChatClient
{
    private readonly Queue<Func<IList<ChatMessage>, IEnumerable<AIContent>>> _turns = new(turns);

    public List<IList<ChatMessage>> Requests { get; } = [];

    /// <summary>Used when the script runs out, for open-ended scenarios.</summary>
    public Func<IList<ChatMessage>, IEnumerable<AIContent>>? Fallback { get; init; }

    public TimeSpan Delay { get; init; }

    public int RemainingTurns => _turns.Count;

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Requests.Add(list);
        var turn = _turns.Count > 0 ? _turns.Dequeue() : Fallback ?? throw new InvalidOperationException("Script exhausted.");

        foreach (var content in turn(list))
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
        }
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    public static AIContent Text(string text) => new TextContent(text);

    public static AIContent Call(string id, string name, object? args = null) =>
        new FunctionCallContent(id, name, args is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args)));
}

/// <summary>An MCP tool stand-in that returns a canned idx envelope.</summary>
public sealed class CannedTool(string name, string result) : AIFunction
{
    public int Calls { get; private set; }

    public override string Name => name;

    public override string Description => name;

    public override JsonElement JsonSchema { get; } = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult<object?>(result);
    }
}

public sealed class FakeToolSource(params AITool[] tools) : IToolSource
{
    public bool Unavailable { get; init; }

    public Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct) => Unavailable
        ? throw new McpUnavailableException("down")
        : Task.FromResult<IReadOnlyList<AITool>>(tools);
}
