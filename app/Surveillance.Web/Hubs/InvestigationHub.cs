using Microsoft.AspNetCore.SignalR;
using Surveillance.Web.Investigations;

namespace Surveillance.Web.Hubs;

/// <summary>
/// SignalR transport for investigation events: one group per case. A client joins with the last
/// sequence it has and gets the stored events after it; live events then arrive as "event"
/// messages from <see cref="InvestigationHubRelay"/>. Joining the group before reading the
/// database means no event falls between the two; the client drops duplicates by sequence.
/// </summary>
public sealed class InvestigationHub(InvestigationService investigations) : Hub
{
    public const string Path = "/hubs/investigations";
    public const string EventMessage = "event";

    public static string Group(Guid caseId) => $"case:{caseId:N}";

    /// <returns>Stored events after <paramref name="after"/>, as catalog JSON, oldest first.</returns>
    public async Task<List<string>> JoinCase(Guid caseId, long after)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(caseId), Context.ConnectionAborted);
        var records = await investigations.GetEventsAsync(caseId, after, Context.ConnectionAborted);
        return records.Select(r => EventMapping.ToEvent(r).ToJson()).ToList();
    }

    public Task LeaveCase(Guid caseId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(caseId), Context.ConnectionAborted);
}
