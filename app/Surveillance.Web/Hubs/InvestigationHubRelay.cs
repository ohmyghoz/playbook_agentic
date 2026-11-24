using Microsoft.AspNetCore.SignalR;
using Surveillance.Web.Investigations;

namespace Surveillance.Web.Hubs;

/// <summary>
/// Forwards every persisted event from <see cref="EventBroadcaster"/> to the SignalR group of its
/// case. The same source feeds SSE, so both transports see the same events in the same order.
/// </summary>
public sealed class InvestigationHubRelay(
    EventBroadcaster broadcaster,
    IHubContext<InvestigationHub> hub,
    ILogger<InvestigationHubRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = broadcaster.SubscribeAll();
        try
        {
            await foreach (var record in subscription.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await hub.Clients.Group(InvestigationHub.Group(record.CaseId))
                        .SendAsync(InvestigationHub.EventMessage, EventMapping.ToEvent(record).ToJson(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A client that missed this event catches up with JoinCase on reconnect.
                    logger.LogWarning(ex, "SignalR relay failed for case {CaseId}, sequence {Sequence}", record.CaseId, record.Sequence);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Application stopping.
        }
    }
}
