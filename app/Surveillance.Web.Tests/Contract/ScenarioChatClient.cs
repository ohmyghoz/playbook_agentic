using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Surveillance.Agents.Maf;
using Surveillance.Web.Tests.Maf;

namespace Surveillance.Web.Tests.Contract;

/// <summary>
/// The MAF side of the shared scripted model: reads scenarios.json, picks the scenario from
/// 'scenario=&lt;name&gt;' in the instructions and the first turn whose call ids are not yet in the
/// conversation. Same rules as agents/*/app/scripted.py.
/// </summary>
public sealed partial class ScenarioChatClient : IChatClient
{
    private readonly JsonElement _root = JsonDocument.Parse(File.ReadAllText(PythonService.ScenariosPath)).RootElement.Clone();

    public static IToolSource CannedTools()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(PythonService.ScenariosPath));
        var tools = doc.RootElement.GetProperty("tools").EnumerateObject()
            .Select(t => (AITool)new CannedTool(t.Name, t.Value.GetProperty("text").GetString()!))
            .ToArray();
        return new FakeToolSource(tools);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var system = (options?.Instructions ?? "") + string.Concat(list.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var seen = list.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet();
        var (items, delay) = NextTurn(system, seen);
        if (delay > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
        }

        foreach (var item in items)
        {
            if (item.TryGetProperty("text", out var text))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(text.GetString())]);
            }
            else
            {
                var call = item.GetProperty("call");
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.GetProperty("args").GetRawText());
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(call.GetProperty("id").GetString()!, call.GetProperty("name").GetString()!, args)]);
            }
        }
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private (List<JsonElement> Items, double Delay) NextTurn(string system, HashSet<string> seen)
    {
        var match = ScenarioName().Match(system);
        var scenario = _root.GetProperty("scenarios").GetProperty(match.Success ? match.Groups[1].Value : "approve");
        var delay = scenario.TryGetProperty("delaySeconds", out var d) ? d.GetDouble() : 0;

        foreach (var turn in scenario.GetProperty("turns").EnumerateArray())
        {
            var ids = turn.EnumerateArray().Where(i => i.TryGetProperty("call", out _))
                .Select(i => i.GetProperty("call").GetProperty("id").GetString()!).ToList();
            if (ids.Count == 0 || !ids.All(seen.Contains))
            {
                return (Resolve(turn, 0), delay);
            }
        }

        if (!scenario.TryGetProperty("fallback", out var fallback))
        {
            return ([JsonDocument.Parse("""{"text":"Selesai."}""").RootElement], 0);
        }

        var n = seen.Count(id => id.StartsWith('q')) + 1;
        return (Resolve(fallback, n), delay);
    }

    /// <summary>Replaces "$memo" references and "{n}" placeholders, as scripted.py does.</summary>
    private List<JsonElement> Resolve(JsonElement turn, int n)
    {
        var memos = _root.GetProperty("memos");
        var json = turn.GetRawText().Replace("{n}", n.ToString());
        foreach (var memo in memos.EnumerateObject())
        {
            json = json.Replace($"\"${memo.Name}\"", memo.Value.GetRawText());
        }

        return JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    [GeneratedRegex(@"scenario=(\w+)")]
    private static partial Regex ScenarioName();
}
