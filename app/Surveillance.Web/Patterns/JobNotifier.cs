using Microsoft.AspNetCore.SignalR;
using Surveillance.Web.Data.Repositories;
using Surveillance.Web.Hubs;
using Surveillance.Web.Investigations;

namespace Surveillance.Web.Patterns;

/// <summary>What the navigation badge and the toast show (message "jobUpdate").</summary>
public sealed record JobNotification(Guid CaseId, string Status, string Text, string Url, int AwaitingCount);

/// <summary>
/// Tells the analyst who started a job when it needs them or has ended, on whatever page they
/// are: a SignalR message to that user (Clients.User, keyed by the account's NameIdentifier).
/// The source is <see cref="EventBroadcaster"/>, like every other live view.
/// </summary>
public sealed class JobNotifier(
    EventBroadcaster broadcaster,
    ICaseRepository cases,
    IHubContext<InvestigationHub> hub,
    ILogger<JobNotifier> logger) : BackgroundService
{
    public const string Message = "jobUpdate";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = broadcaster.SubscribeAll();
        try
        {
            await foreach (var record in subscription.Reader.ReadAllAsync(stoppingToken))
            {
                if (record.Type is not ("approval_required" or "done" or "error"))
                {
                    continue;
                }

                try
                {
                    var @case = await cases.GetAsync(record.CaseId, stoppingToken);
                    if (@case is not { IsJob: true, StartedByUser: { } user })
                    {
                        continue;
                    }

                    var text = record.Type switch
                    {
                        "approval_required" => "Laporan job deteksi pola siap ditelaah.",
                        "done" => "Job deteksi pola selesai.",
                        _ => "Job deteksi pola berhenti karena galat.",
                    };
                    var awaiting = await cases.CountJobsAwaitingAsync(user, stoppingToken);
                    await hub.Clients.User(user).SendAsync(Message,
                        new JobNotification(@case.Id, record.Type, text, $"/jobs/{@case.Id}", awaiting), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Job notification failed for case {CaseId}", record.CaseId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Application stopping.
        }
    }
}
