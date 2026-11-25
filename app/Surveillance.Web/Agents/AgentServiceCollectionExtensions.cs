using System.Collections.Concurrent;
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

    /// <summary>
    /// Default solution for new investigations: <c>fake</c>, <c>maf</c>, <c>langgraph</c>, or
    /// <c>adk</c>. The settings page overrides it; existing cases keep the solution that started them.
    /// </summary>
    public string Provider { get; set; } = "fake";
}

/// <summary>One agent solution as the settings page shows it.</summary>
/// <param name="SupportsPatternDetection">The solution runs use case 2 (CHANGE-002); jobs are offered only then.</param>
public sealed record AgentProvider(string Key, string Name, string Model, string Description, bool SupportsPatternDetection = false);

/// <summary>Whether a solution can take a new investigation right now.</summary>
public sealed record AgentProviderStatus(bool Ready, string Message);

/// <summary>
/// Holds the four adapters and creates each on first use, so the application starts even
/// when one solution has no API key or its service is down. This is the only place that
/// knows which adapter class belongs to which solution.
/// </summary>
public sealed class AgentClientRegistry(IServiceProvider services, IConfiguration configuration)
{
    public const string Fake = "fake";
    public const string Maf = "maf";
    public const string LangGraph = "langgraph";
    public const string Adk = "adk";

    private readonly ConcurrentDictionary<string, Lazy<IAgentClient>> _clients = new();

    public IReadOnlyList<AgentProvider> Providers { get; } =
    [
        new(Fake, "Agent tiruan", "tanpa model", "Skrip tetap untuk pengembangan dan demo; tidak memanggil model atau MCP.",
            SupportsPatternDetection: true),
        new(Maf, "Microsoft Agent Framework", configuration["Agent:Maf:Model"] ?? "gpt-5-mini",
            "Berjalan di dalam aplikasi; model OpenAI lewat platform.openai.", SupportsPatternDetection: true),
        new(LangGraph, "LangGraph", configuration["Agent:LangGraph:ModelLabel"] ?? "-",
            "Service Python terpisah; model diatur di agents/langgraph/.env."),
        new(Adk, "Google ADK", configuration["Agent:Adk:ModelLabel"] ?? "-",
            "Service Python terpisah; model diatur di agents/adk/.env."),
    ];

    public bool IsKnown(string key) => Providers.Any(p => p.Key == Normalize(key));

    /// <exception cref="InvalidOperationException">The solution is unknown or cannot be created (for example a missing API key).</exception>
    public IAgentClient Get(string key)
    {
        var normalized = Normalize(key);
        if (!IsKnown(normalized))
        {
            throw new InvalidOperationException($"Unknown agent provider '{key}'.");
        }

        var lazy = _clients.GetOrAdd(normalized, k => new Lazy<IAgentClient>(() => Create(k)));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _clients.TryRemove(normalized, out _); // try again next time, e.g. after the key is added
            throw;
        }
    }

    public async Task<AgentProviderStatus> CheckAsync(string key, CancellationToken ct)
    {
        switch (Normalize(key))
        {
            case Fake:
                return new(true, "Siap.");
            case Maf:
                return string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"])
                    ? new(false, "OPENAI_API_KEY belum diisi di user-secrets.")
                    : new(true, "Siap. Butuh server MCP idx saat investigasi berjalan.");
            case LangGraph:
                return await CheckServiceAsync("LangGraph", "agents/langgraph/run.ps1", ct);
            case Adk:
                return await CheckServiceAsync("Adk", "agents/adk/run.ps1", ct);
            default:
                return new(false, "Solusi tidak dikenal.");
        }
    }

    private async Task<AgentProviderStatus> CheckServiceAsync(string section, string script, CancellationToken ct)
    {
        var baseUrl = configuration[$"Agent:{section}:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new(false, $"Agent:{section}:BaseUrl belum diatur.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var http = new HttpClient();
            var response = await http.GetAsync(new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "health"), timeout.Token);
            return response.IsSuccessStatusCode
                ? new(true, $"Siap ({baseUrl}).")
                : new(false, $"Service di {baseUrl} membalas {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, $"Service di {baseUrl} tidak bisa dihubungi. Jalankan {script} atau scripts/start-all.ps1.");
        }
    }

    private IAgentClient Create(string key) => key switch
    {
        Fake => services.GetRequiredService<FakeAgentClient>(),
        Maf => CreateMaf(),
        LangGraph => new LangGraphAgentClient(HttpClientFor("LangGraph"), services.GetRequiredService<ILogger<LangGraphAgentClient>>()),
        Adk => new AdkAgentClient(HttpClientFor("Adk"), services.GetRequiredService<ILogger<AdkAgentClient>>()),
        _ => throw new InvalidOperationException($"Unknown agent provider '{key}'."),
    };

    private MafAgentClient CreateMaf()
    {
        var options = services.GetRequiredService<MafAgentOptions>();
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "OPENAI_API_KEY is not set. Store it with: dotnet user-secrets set OPENAI_API_KEY \"...\"");
        }

        var chatClient = new OpenAIClient(options.ApiKey).GetChatClient(options.Model).AsIChatClient();
        return new MafAgentClient(options, chatClient, services.GetRequiredService<IToolSource>(),
            services.GetRequiredService<ILogger<MafAgentClient>>());
    }

    private HttpClient HttpClientFor(string section)
    {
        if (string.IsNullOrWhiteSpace(configuration[$"Agent:{section}:BaseUrl"]))
        {
            throw new InvalidOperationException($"Agent:{section}:BaseUrl is not set.");
        }

        return services.GetRequiredService<IHttpClientFactory>().CreateClient(section);
    }

    private static string Normalize(string key) => key.Trim().ToLowerInvariant();
}

public static class AgentServiceCollectionExtensions
{
    /// <summary>Registers every adapter behind <see cref="AgentClientRegistry"/>; none is created until it is used.</summary>
    public static IServiceCollection AddAgentClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));

        services.Configure<FakeAgentOptions>(configuration.GetSection(FakeAgentOptions.Section));
        services.AddSingleton<FakeAgentClient>();

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

        foreach (var section in new[] { "LangGraph", "Adk" })
        {
            var baseUrl = configuration[$"{AgentOptions.Section}:{section}:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                continue; // reported by the registry when the solution is chosen
            }

            services.AddHttpClient(section, c =>
            {
                c.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                c.Timeout = Timeout.InfiniteTimeSpan; // SSE streams stay open while the agent works
            });
        }

        services.AddSingleton<AgentClientRegistry>();
        return services;
    }
}
