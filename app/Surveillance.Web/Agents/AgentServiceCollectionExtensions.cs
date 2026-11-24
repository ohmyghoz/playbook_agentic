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
                AddHttpSseClient(services, configuration, "LangGraph",
                    (http, sp) => new LangGraphAgentClient(http, sp.GetRequiredService<ILogger<LangGraphAgentClient>>()));
                break;
            case "adk":
                AddHttpSseClient(services, configuration, "Adk",
                    (http, sp) => new AdkAgentClient(http, sp.GetRequiredService<ILogger<AdkAgentClient>>()));
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown Agent:Provider '{provider}'. Use fake, maf, langgraph, or adk.");
        }

        return services;
    }

    /// <summary>Registers a client for a Python agent service configured under <c>Agent:{section}:BaseUrl</c>.</summary>
    private static void AddHttpSseClient(IServiceCollection services, IConfiguration configuration, string section,
        Func<HttpClient, IServiceProvider, HttpSseAgentClient> create)
    {
        var options = new HttpAgentOptions();
        configuration.GetSection($"{AgentOptions.Section}:{section}").Bind(options);
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException($"{AgentOptions.Section}:{section}:BaseUrl is not set.");
        }

        services.AddHttpClient(section, c =>
        {
            c.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = Timeout.InfiniteTimeSpan; // SSE streams stay open while the agent works
        });
        services.AddSingleton<IAgentClient>(sp => create(sp.GetRequiredService<IHttpClientFactory>().CreateClient(section), sp));
    }
}
