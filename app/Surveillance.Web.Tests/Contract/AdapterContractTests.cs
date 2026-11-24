using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using Surveillance.Agents.Abstractions;
using Surveillance.Agents.Maf;
using Surveillance.Web.Agents.Fake;
using Surveillance.Web.Agents.Http;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests.Contract;

// ---- offline: scripted models, every adapter ----------------------------------------

public class FakeContractTests : AgentContractTests
{
    protected override IAgentClient Client { get; } =
        new FakeAgentClient(Options.Create(new FakeAgentOptions { ToolDelayMs = 0, TokenDelayMs = 0 }));
}

public sealed class MafContractTests : AgentContractTests, IDisposable
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), $"contract-maf-{Guid.NewGuid():N}");

    public MafContractTests() =>
        Client = new MafAgentClient(new MafAgentOptions { StateDirectory = _stateDir }, new ScenarioChatClient(),
            ScenarioChatClient.CannedTools());

    protected override IAgentClient Client { get; }

    public void Dispose()
    {
        if (Directory.Exists(_stateDir))
        {
            Directory.Delete(_stateDir, recursive: true);
        }
    }
}

public sealed class ScriptedLangGraphService : IAsyncLifetime
{
    public PythonService Service { get; private set; } = null!;
    public async Task InitializeAsync() => Service = await PythonService.StartAsync("langgraph", scripted: true);
    public async Task DisposeAsync() => await Service.DisposeAsync();
}

public sealed class ScriptedAdkService : IAsyncLifetime
{
    public PythonService Service { get; private set; } = null!;
    public async Task InitializeAsync() => Service = await PythonService.StartAsync("adk", scripted: true);
    public async Task DisposeAsync() => await Service.DisposeAsync();
}

/// <summary>Real LangGraph service over HTTP + SSE, scripted model.</summary>
public class LangGraphContractTests(ScriptedLangGraphService fixture) : AgentContractTests, IClassFixture<ScriptedLangGraphService>
{
    protected override IAgentClient Client { get; } =
        new LangGraphAgentClient(fixture.Service.CreateHttpClient(), NullLogger<LangGraphAgentClient>.Instance);
}

/// <summary>Real ADK service over HTTP + SSE, scripted model.</summary>
public class AdkContractTests(ScriptedAdkService fixture) : AgentContractTests, IClassFixture<ScriptedAdkService>
{
    protected override IAgentClient Client { get; } =
        new AdkAgentClient(fixture.Service.CreateHttpClient(), NullLogger<AdkAgentClient>.Instance);
}

// ---- live: real models and the real idx MCP server ----------------------------------

/// <summary>
/// The parts of the contract that a real model can be steered into. Each test builds its
/// own client so the step-limit test can use a limit of 3.
/// </summary>
[Trait("Category", "Live")]
public abstract class LiveAgentContractTests
{
    protected abstract Task<(IAgentClient Client, IAsyncDisposable? Owner)> CreateAsync(int maxToolCalls = 15);

    [LiveFact]
    public async Task Live_approve_flow()
    {
        var (client, owner) = await CreateAsync();
        await using var _ = owner;
        var runId = await client.StartAsync(LiveRequest(), default);
        var events = await DrainLive(client, runId);
        AssertPaused(events);
        Assert.Contains(events, e => e.Type == AgentEventType.ToolCallStarted);
        EventAssertions.AssertPreviewsPresent(events);
        Assert.Contains(events, e => e.Type == AgentEventType.StepStarted
                                     && e.PayloadAs<StepStartedPayload>().Step == InvestigationStep.Assess);

        await client.ResumeAsync(runId, new ApprovalDecision(
            events[^1].PayloadAs<ApprovalRequiredPayload>().ApprovalId, Decision.Approve, "live-test"), default);
        events.AddRange(await DrainLive(client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Approved, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    [LiveFact]
    public async Task Live_reject_flow()
    {
        var (client, owner) = await CreateAsync();
        await using var _ = owner;
        var runId = await client.StartAsync(LiveRequest(), default);
        var events = await DrainLive(client, runId);
        AssertPaused(events);

        await client.ResumeAsync(runId, new ApprovalDecision(
            events[^1].PayloadAs<ApprovalRequiredPayload>().ApprovalId, Decision.Reject, "live-test", "Uji penolakan."), default);
        events.AddRange(await DrainLive(client, runId));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Rejected, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    [LiveFact]
    public async Task Live_cancel_while_paused()
    {
        var (client, owner) = await CreateAsync();
        await using var _ = owner;
        var runId = await client.StartAsync(LiveRequest(), default);
        var events = await DrainLive(client, runId);
        AssertPaused(events);

        events.AddRange(await DrainLive(client, runId, new CancellationToken(canceled: true)));

        EventAssertions.AssertWellFormedRun(events, expectTerminal: true);
        Assert.Equal(RunOutcome.Cancelled, events[^1].PayloadAs<DonePayload>().Outcome);
    }

    /// <summary>
    /// With a limit of 3 a real model either asks for more tools after the warning
    /// (step_limit_exceeded) or obeys it and submits a memo. Both are correct; the
    /// executed tool calls must never exceed the limit.
    /// </summary>
    [LiveFact]
    public async Task Live_step_limit_is_never_exceeded()
    {
        var (client, owner) = await CreateAsync(maxToolCalls: 3);
        await using var _ = owner;
        var runId = await client.StartAsync(LiveRequest(), default);
        var events = await DrainLive(client, runId);

        var last = events[^1];
        Assert.True(last.Type == AgentEventType.ApprovalRequired
                    || last.Type == AgentEventType.Error && last.PayloadAs<ErrorPayload>().Code == AgentErrorCode.StepLimitExceeded,
            $"Unexpected end: {last.ToJson()}");
        var executed = events.Count(e => e.Type == AgentEventType.ToolCallFinished
                                         && e.PayloadAs<ToolCallFinishedPayload>() is { Status: ToolCallStatus.Ok, ToolName: not "submit_memo" });
        Assert.InRange(executed, 1, 3);

        if (last.Type == AgentEventType.ApprovalRequired)
        {
            await DrainLive(client, runId, new CancellationToken(canceled: true));
        }
    }

    /// <summary>A real model can end a run before the memo; show why instead of a bare boolean failure.</summary>
    private static void AssertPaused(List<AgentEvent> events)
    {
        Assert.True(events.Count > 0 && events[^1].Type == AgentEventType.ApprovalRequired,
            $"Run did not reach approval; last event: {(events.Count > 0 ? events[^1].ToJson() : "(none)")}");
        EventAssertions.AssertWellFormedRun(events, expectTerminal: false);
    }

    private static InvestigationRequest LiveRequest() => new(
        Guid.NewGuid().ToString(), "BBCA", new DateOnly(2026, 11, 24),
        new AnomalyTrigger(TriggerKind.Volume, "Volume di atas 1,1x rata-rata 3 bulan", 2.2, 235_635_300, 210_424_313, 1.12));

    private static async Task<List<AgentEvent>> DrainLive(IAgentClient client, string runId, CancellationToken stop = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var events = new List<AgentEvent>();
        var stream = client.StreamAsync(runId, stop).GetAsyncEnumerator();
        try
        {
            while (await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
            {
                events.Add(stream.Current);
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }

        return events;
    }
}

public sealed class MafLiveContractTests : LiveAgentContractTests
{
    protected override Task<(IAgentClient Client, IAsyncDisposable? Owner)> CreateAsync(int maxToolCalls = 15)
    {
        // The same secrets the web app reads: OPENAI_API_KEY in its user-secrets, IDX_MCP_TOKEN in the environment.
        var config = new ConfigurationBuilder().AddUserSecrets("127fa63b-a132-4b5b-bb3c-a2a4ca332f06").Build();
        var stateDir = Path.Combine(Path.GetTempPath(), $"live-maf-{Guid.NewGuid():N}");
        var options = new MafAgentOptions
        {
            ApiKey = config["OPENAI_API_KEY"],
            McpToken = Environment.GetEnvironmentVariable("IDX_MCP_TOKEN")
                       ?? Environment.GetEnvironmentVariable("IDX_MCP_TOKEN", EnvironmentVariableTarget.User),
            StateDirectory = stateDir,
            MaxToolCalls = maxToolCalls,
        };
        var tools = new McpToolSource(options);
        var chat = new OpenAIClient(options.ApiKey).GetChatClient(options.Model).AsIChatClient();
        IAgentClient client = new MafAgentClient(options, chat, tools);
        return Task.FromResult<(IAgentClient, IAsyncDisposable?)>((client, tools));
    }
}

public sealed class LangGraphLiveContractTests : LiveAgentContractTests
{
    protected override async Task<(IAgentClient Client, IAsyncDisposable? Owner)> CreateAsync(int maxToolCalls = 15)
    {
        var service = await PythonService.StartAsync("langgraph", scripted: false, maxToolCalls);
        return (new LangGraphAgentClient(service.CreateHttpClient(), NullLogger<LangGraphAgentClient>.Instance), service);
    }
}

public sealed class AdkLiveContractTests : LiveAgentContractTests
{
    protected override async Task<(IAgentClient Client, IAsyncDisposable? Owner)> CreateAsync(int maxToolCalls = 15)
    {
        var service = await PythonService.StartAsync("adk", scripted: false, maxToolCalls);
        return (new AdkAgentClient(service.CreateHttpClient(), NullLogger<AdkAgentClient>.Instance), service);
    }
}
