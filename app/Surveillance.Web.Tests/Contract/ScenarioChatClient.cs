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

    /// <summary>UMA: 'scenario=&lt;name&gt;' in the notes. Pattern detection: the agent's role, then role_&lt;target&gt; if present.</summary>
    private (JsonElement Scenario, string Target) Scenario(string system)
    {
        var scenarios = _root.GetProperty("scenarios");
        var match = ScenarioName().Match(system);
        if (match.Success)
        {
            return (scenarios.GetProperty(match.Groups[1].Value), "");
        }

        var role = PatternRole().Match(system);
        if (!role.Success)
        {
            return (scenarios.GetProperty("approve"), "");
        }

        var name = role.Groups[2].Value switch
        {
            "saham" => "pattern_stock",
            "broker" => "pattern_broker",
            "kepemilikan" => "pattern_ownership",
            _ => "pattern_report",
        };
        var found = PatternTarget().Match(system);
        var target = found.Success ? NonAlphanumeric().Replace(found.Groups[1].Value, "") : "";
        return (scenarios.TryGetProperty($"{name}_{target}", out var specific) ? specific : scenarios.GetProperty(name), target);
    }

    private (List<JsonElement> Items, double Delay) NextTurn(string system, HashSet<string> seen)
    {
        var (scenario, target) = Scenario(system);
        var delay = scenario.TryGetProperty("delaySeconds", out var d) ? d.GetDouble() : 0;

        foreach (var turn in scenario.GetProperty("turns").EnumerateArray())
        {
            var resolved = Resolve(turn, 0, target);
            var ids = resolved.Where(i => i.TryGetProperty("call", out _))
                .Select(i => i.GetProperty("call").GetProperty("id").GetString()!).ToList();
            if (ids.Count == 0 || !ids.All(seen.Contains))
            {
                return (resolved, delay);
            }
        }

        if (!scenario.TryGetProperty("fallback", out var fallback))
        {
            return ([JsonDocument.Parse("""{"text":"Selesai."}""").RootElement], 0);
        }

        var n = seen.Count(id => id.StartsWith('q')) + 1;
        return (Resolve(fallback, n, target), delay);
    }

    /// <summary>Replaces "$memo" and "$report" references and "{n}" and "{target}" placeholders, as scripted.py does.</summary>
    private List<JsonElement> Resolve(JsonElement turn, int n, string target)
    {
        var json = turn.GetRawText();
        foreach (var section in new[] { "memos", "reports" })
        {
            if (!_root.TryGetProperty(section, out var objects))
            {
                continue;
            }

            foreach (var obj in objects.EnumerateObject())
            {
                json = json.Replace($"\"${obj.Name}\"", obj.Value.GetRawText());
            }
        }

        json = json.Replace("{n}", n.ToString()).Replace("{target}", target);
        return JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    [GeneratedRegex(@"scenario=(\w+)")]
    private static partial Regex ScenarioName();

    [GeneratedRegex("Anda (worker (saham|broker|kepemilikan)|penyusun laporan)")]
    private static partial Regex PatternRole();

    [GeneratedRegex(@"Target Anda: (.+?)\. Jendela")]
    private static partial Regex PatternTarget();

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
