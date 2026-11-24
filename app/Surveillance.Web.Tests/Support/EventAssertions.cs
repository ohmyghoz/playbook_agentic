using System.Text.Json;
using Json.Schema;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Web.Tests.Support;

public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Contracts(string file) => Path.Combine(Root, "contracts", file);

    // Search from the build output and, for builds outside the repo (--artifacts-path), from this source file.
    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) ?? "" })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Surveillance.slnx")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException("Repository root (Surveillance.slnx) not found.");
    }
}

/// <summary>Checks a run's events against contracts/events.schema.json and the ordering rules of the ASD.</summary>
public static class EventAssertions
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromFile(RepoPaths.Contracts("events.schema.json")));

    public static void AssertMatchesSchema(AgentEvent e) => AssertMatchesSchema(e.ToJson());

    public static void AssertMatchesSchema(string eventJson)
    {
        using var doc = JsonDocument.Parse(eventJson);
        var result = Schema.Value.Evaluate(doc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
        });
        if (!result.IsValid)
        {
            var errors = (result.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(kv => $"{d.InstanceLocation} {kv.Key}: {kv.Value}"));
            Assert.Fail($"Event does not match schema: {eventJson}\n{string.Join("\n", errors)}");
        }
    }

    /// <summary>
    /// Validates a complete or paused run: schema, contiguous sequence from 1,
    /// run_started first, tool calls paired, nothing after the terminal event.
    /// </summary>
    public static void AssertWellFormedRun(IReadOnlyList<AgentEvent> events, bool expectTerminal)
    {
        Assert.NotEmpty(events);
        Assert.Equal(AgentEventType.RunStarted, events[0].Type);

        var open = new Dictionary<string, string>();
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            AssertMatchesSchema(e);
            Assert.Equal(i + 1, e.Sequence);
            Assert.Equal(events[0].RunId, e.RunId);
            Assert.Equal(DateTimeKind.Utc, e.Timestamp.Kind);

            switch (e.Type)
            {
                case AgentEventType.ToolCallStarted:
                    var started = e.PayloadAs<ToolCallStartedPayload>();
                    Assert.True(open.TryAdd(started.ToolCallId, started.ToolName), $"Duplicate tool call {started.ToolCallId}");
                    break;
                case AgentEventType.ToolCallFinished:
                    var finished = e.PayloadAs<ToolCallFinishedPayload>();
                    Assert.True(open.Remove(finished.ToolCallId, out var name), $"Finished unknown tool call {finished.ToolCallId}");
                    Assert.Equal(name, finished.ToolName);
                    break;
                case AgentEventType.Done or AgentEventType.Error:
                    Assert.Equal(events.Count - 1, i);
                    Assert.Empty(open);
                    break;
            }
        }

        Assert.Equal(expectTerminal, events[^1].IsTerminal);
        if (!expectTerminal)
        {
            Assert.Equal(AgentEventType.ApprovalRequired, events[^1].Type);
        }
    }
}
