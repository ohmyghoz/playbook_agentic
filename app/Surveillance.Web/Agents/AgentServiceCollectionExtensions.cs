using Surveillance.Agents.Abstractions;
using Surveillance.Web.Agents.Fake;

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
            case "langgraph":
            case "adk":
                throw new NotSupportedException($"Agent provider '{provider}' is not implemented yet.");
            default:
                throw new InvalidOperationException(
                    $"Unknown Agent:Provider '{provider}'. Use fake, maf, langgraph, or adk.");
        }

        return services;
    }
}
