using Microsoft.Extensions.AI;
using OpenAI;
using Surveillance.Agents.Abstractions;
using Surveillance.Agents.Maf;
using Surveillance.Web.Agents.Fake;
using Surveillance.Web.Agents.Http;

namespace Surveillance.Web.Agents;

public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary><c>fake</c>, <c>maf</c>, <c>langgraph</c>, or <c>adk</c>.</summary>
    public string Provider { get; set; } = "fake";
}

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IAgentClient"/> chosen by <c>Agent:Provider</c>.
    /// This is the only place that knows which adapter is in use.
    /// </summary>
    public static IServiceCollection AddAgentClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        var provider = configuration[$"{AgentOptions.Section}:Provider"] ?? "fake";

        switch (provider.ToLowerInvariant())
        {
            case "fake":
                services.Configure<FakeAgentOptions>(configuration.GetSection(FakeAgentOptions.Section));
                services.AddSingleton<IAgentClient, FakeAgentClient>();
                break;
            case "maf":
                services.AddSingleton(sp =>
                {
                    var options = new MafAgentOptions();
                    configuration.GetSection(MafAgentOptions.Section).Bind(options);
                    options.ApiKey = configuration["OPENAI_API_KEY"];
                    options.McpToken = configuration["IDX_MCP_TOKEN"];
                    options.StateDirectory = Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, options.StateDirectory);
                    return options;
                });
                services.AddSingleton<IToolSource, McpToolSource>();
                services.AddSingleton<IAgentClient>(sp =>
                {
                    var options = sp.GetRequiredService<MafAgentOptions>();
                    if (string.IsNullOrWhiteSpace(options.ApiKey))
                    {
                        throw new InvalidOperationException(
                            "OPENAI_API_KEY is not set. Store it with: dotnet user-secrets set OPENAI_API_KEY \"...\"");
                    }

                    var chatClient = new OpenAIClient(options.ApiKey).GetChatClient(options.Model).AsIChatClient();
                    return new MafAgentClient(options, chatClient, sp.GetRequiredService<IToolSource>(),
                        sp.GetRequiredService<ILogger<MafAgentClient>>());
                });
                break;
            case "langgraph":
                var langGraph = new HttpAgentOptions();
                configuration.GetSection("Agent:LangGraph").Bind(langGraph);
                services.AddHttpClient("langgraph", c =>
                {
                    c.BaseAddress = new Uri(langGraph.BaseUrl.TrimEnd('/') + "/");
                    c.Timeout = Timeout.InfiniteTimeSpan; // SSE streams stay open while the agent works
                });
                services.AddSingleton<IAgentClient>(sp => new LangGraphAgentClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("langgraph"),
                    sp.GetRequiredService<ILogger<LangGraphAgentClient>>()));
                break;
            case "adk":
                throw new NotSupportedException($"Agent provider '{provider}' is not implemented yet.");
            default:
                throw new InvalidOperationException(
                    $"Unknown Agent:Provider '{provider}'. Use fake, maf, langgraph, or adk.");
        }

        return services;
    }
}
