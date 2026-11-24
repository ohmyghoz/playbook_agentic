namespace Surveillance.Agents.Maf;

public sealed class MafAgentOptions
{
    public const string Section = "Agent:Maf";

    /// <summary>OpenAI model id on platform.openai.com; must support tool calling.</summary>
    public string Model { get; set; } = "gpt-5-mini";

    /// <summary>Read from the OPENAI_API_KEY secret, never from appsettings.</summary>
    public string? ApiKey { get; set; }

    public string McpEndpoint { get; set; } = "http://127.0.0.1:8765/mcp";

    /// <summary>Read from the IDX_MCP_TOKEN environment variable.</summary>
    public string? McpToken { get; set; }

    /// <summary>Where serialized sessions of paused runs are kept so approvals survive a restart.</summary>
    public string StateDirectory { get; set; } = Path.Combine("App_Data", "maf-runs");

    public int MaxToolCalls { get; set; } = 15;

    public int MaxRevisions { get; set; } = 2;
}
