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

        AssertStepsAndEvidence(events);
    }

    /// <summary>
    /// CHANGE-001 rules: step_started right after run_started and only forward, assess before the
    /// first submit_memo, memo before the first approval_required, source on every MCP result,
    /// and resultPreview within 8192 UTF-8 bytes.
    /// </summary>
    public static void AssertStepsAndEvidence(IReadOnlyList<AgentEvent> events)
    {
        if (IsPatternDetection(events))
        {
            AssertWorkers(events);
            return; // the five UMA stages do not apply; workers do (asd-pattern-detection.md)
        }

        if (events.Count > 1)
        {
            Assert.Equal(AgentEventType.StepStarted, events[1].Type);
            Assert.Equal(InvestigationStep.Measure, events[1].PayloadAs<StepStartedPayload>().Step);
        }

        InvestigationStep? current = null;
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case AgentEventType.StepStarted:
                    var step = e.PayloadAs<StepStartedPayload>();
                    Assert.True(current is null || step.Step > current, $"Step {step.Step} at {e.Sequence} does not move forward from {current}.");
                    Assert.Equal(InvestigationSteps.Label(step.Step), step.Label);
                    current = step.Step;
                    break;
                case AgentEventType.ToolCallStarted when e.PayloadAs<ToolCallStartedPayload>().ToolName == InvestigationSteps.SubmitMemo:
                    Assert.True(current >= InvestigationStep.Assess, $"submit_memo at {e.Sequence} before step assess.");
                    break;
                case AgentEventType.ApprovalRequired:
                    Assert.Equal(InvestigationStep.Memo, current);
                    break;
                case AgentEventType.ToolCallFinished:
                    var finished = e.PayloadAs<ToolCallFinishedPayload>();
                    if (finished.ResultPreview is { } preview)
                    {
                        Assert.True(System.Text.Encoding.UTF8.GetByteCount(preview) <= ToolEvidence.MaxPreviewBytes,
                            $"resultPreview of {finished.ToolCallId} exceeds {ToolEvidence.MaxPreviewBytes} bytes.");
                    }

                    if (finished.ToolName != InvestigationSteps.SubmitMemo && finished.Status == ToolCallStatus.Ok)
                    {
                        Assert.True(finished.Source is { Server: ToolEvidence.Server }, $"MCP result {finished.ToolCallId} ({finished.ToolName}) has no source.");
                    }

                    break;
            }
        }
    }

    public static bool IsPatternDetection(IReadOnlyList<AgentEvent> events) =>
        events.Count > 0 && events[0].Type == AgentEventType.RunStarted
                         && events[0].PayloadAs<RunStartedPayload>().UseCase == UseCase.PatternDetection;

    /// <summary>
    /// CHANGE-002 rules: every event names its agent; each worker_started has one worker_finished;
    /// a worker's tool calls fall between the two; no worker is open at approval or at the end.
    /// </summary>
    public static void AssertWorkers(IReadOnlyList<AgentEvent> events)
    {
        var open = new Dictionary<string, WorkerStartedPayload>();
        var seen = new HashSet<string>();
        foreach (var e in events)
        {
            Assert.False(string.IsNullOrEmpty(e.AgentName), $"Event {e.Sequence} ({e.Type}) has no agentName.");
            switch (e.Type)
            {
                case AgentEventType.WorkerStarted:
                    var started = e.PayloadAs<WorkerStartedPayload>();
                    Assert.Equal(started.WorkerId, e.AgentName);
                    Assert.True(seen.Add(started.WorkerId), $"Worker {started.WorkerId} started twice.");
                    open[started.WorkerId] = started;
                    break;
                case AgentEventType.WorkerFinished:
                    var finished = e.PayloadAs<WorkerFinishedPayload>();
                    Assert.True(open.Remove(finished.WorkerId, out var start), $"Worker {finished.WorkerId} finished without starting.");
                    Assert.Equal(start.Role, finished.Role);
                    break;
                case AgentEventType.ToolCallStarted or AgentEventType.ToolCallFinished when e.AgentName != "orchestrator":
                    Assert.True(open.ContainsKey(e.AgentName!), $"Tool event {e.Sequence} of {e.AgentName} outside its worker.");
                    break;
                case AgentEventType.ApprovalRequired or AgentEventType.Done or AgentEventType.Error:
                    Assert.Empty(open);
                    Assert.Equal("orchestrator", e.AgentName);
                    break;
            }
        }
    }

    /// <summary>Events straight from an adapter carry resultPreview for every MCP result (the application strips it later).</summary>
    public static void AssertPreviewsPresent(IReadOnlyList<AgentEvent> events)
    {
        foreach (var finished in events.Where(e => e.Type == AgentEventType.ToolCallFinished).Select(e => e.PayloadAs<ToolCallFinishedPayload>()))
        {
            if (finished.ToolName != InvestigationSteps.SubmitMemo && finished.Status == ToolCallStatus.Ok)
            {
                Assert.False(string.IsNullOrEmpty(finished.ResultPreview), $"MCP result {finished.ToolCallId} ({finished.ToolName}) has no resultPreview.");
            }
        }
    }
}
