namespace Surveillance.Web.Agents.Fake;

public enum FakeScenario
{
    /// <summary>Full UMA flow with one failing tool (idx_bandar) and an approval pause.</summary>
    Happy,

    /// <summary>The model stops responding after a few tool calls.</summary>
    ModelError,

    /// <summary>The agent keeps calling tools past the 15-call limit.</summary>
    StepLimit,
}

public sealed class FakeAgentOptions
{
    public const string Section = "Agent:Fake";

    public FakeScenario Scenario { get; set; } = FakeScenario.Happy;

    /// <summary>Simulated duration of each tool call.</summary>
    public int ToolDelayMs { get; set; } = 600;

    /// <summary>Delay between streamed text chunks.</summary>
    public int TokenDelayMs { get; set; } = 40;
}
