using Microsoft.Extensions.Options;
using Surveillance.Web.Agents;
using Surveillance.Web.Data.Repositories;

namespace Surveillance.Web.Investigations;

/// <summary>A solution as the settings page lists it.</summary>
public sealed record AgentChoice(string Key, string Name, string Model, string Description, bool Ready, string Status, bool Active);

/// <summary>
/// Which agent solution new investigations use. Stored in the database so it survives a
/// restart; <c>Agent:Provider</c> is only the default. Controllers use this class and never
/// see the adapters.
/// </summary>
public sealed class AgentSelection(
    AgentClientRegistry registry,
    ISettingsRepository settings,
    IOptions<AgentOptions> options)
{
    public const string SettingKey = "agent.provider";

    public async Task<string> GetActiveAsync(CancellationToken ct)
    {
        var stored = await settings.GetAsync(SettingKey, ct);
        return stored is not null && registry.IsKnown(stored) ? stored : options.Value.Provider.ToLowerInvariant();
    }

    /// <summary>Demo pages under /lab run only with the scripted agent, so they never call a model.</summary>
    public async Task<bool> IsLabAvailableAsync(CancellationToken ct) =>
        await GetActiveAsync(ct) == AgentClientRegistry.Fake;

    public async Task<bool> SetActiveAsync(string key, CancellationToken ct)
    {
        if (!registry.IsKnown(key))
        {
            return false;
        }

        await settings.SetAsync(SettingKey, key.ToLowerInvariant(), ct);
        return true;
    }

    public async Task<List<AgentChoice>> ListAsync(CancellationToken ct)
    {
        var active = await GetActiveAsync(ct);
        var statuses = await Task.WhenAll(registry.Providers.Select(p => registry.CheckAsync(p.Key, ct)));
        return registry.Providers
            .Select((p, i) => new AgentChoice(p.Key, p.Name, p.Model, p.Description, statuses[i].Ready, statuses[i].Message, p.Key == active))
            .ToList();
    }

    public async Task<string> ActiveNameAsync(CancellationToken ct)
    {
        var active = await GetActiveAsync(ct);
        var provider = registry.Providers.Single(p => p.Key == active);
        return $"{provider.Name} ({provider.Model})";
    }
}
