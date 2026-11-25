using Microsoft.Extensions.Options;
using Surveillance.Agents.Maf;
using Surveillance.Web.Data.Repositories;

namespace Surveillance.Web.Investigations;

/// <summary>How long data is kept (configuration section "Retention"). See docs/RETENTION.md.</summary>
public sealed class RetentionOptions
{
    /// <summary>Events of a finished case are deleted this long after it finished; the case, decisions and audit stay.</summary>
    public int EventDays { get; set; } = 90;

    /// <summary>Raw tool output (ToolResultPreviews) is deleted this long after it was stored.</summary>
    public int PreviewDays { get; set; } = 30;

    /// <summary>Run state files of the MAF adapter (App_Data/maf-runs) not touched for this long are deleted.</summary>
    public int MafRunDays { get; set; } = 30;

    /// <summary>Token events of a finished case are merged this long after it finished.</summary>
    public int CompactAfterMinutes { get; set; } = 60;

    public int CheckMinutes { get; set; } = 360;
}

public sealed record RetentionReport(int PreviewsDeleted, int CasesCompacted, int TokensMerged, int CasesPurged, int EventsDeleted, int MafFilesDeleted);

/// <summary>
/// Applies the retention policy every Retention:CheckMinutes: deletes old raw previews, merges
/// the token events of finished runs into whole narration texts, deletes the events of old
/// finished cases, and removes stale MAF run files. Running it twice changes nothing more.
/// </summary>
public sealed class RetentionService(
    IEventRepository events,
    IOptions<RetentionOptions> options,
    MafAgentOptions maf,
    ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.CheckMinutes));
        do
        {
            try
            {
                var report = await RunOnceAsync(DateTime.UtcNow, stoppingToken);
                logger.LogInformation("Retention: {Report}", report);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<RetentionReport> RunOnceAsync(DateTime now, CancellationToken ct)
    {
        var o = options.Value;
        var previews = await events.DeletePreviewsOlderThanAsync(now.AddDays(-o.PreviewDays), ct);
        var (compacted, merged) = await events.CompactTokensAsync(now.AddMinutes(-o.CompactAfterMinutes), ct);
        var (purged, deleted) = await events.PurgeEventsAsync(now.AddDays(-o.EventDays), ct);
        return new RetentionReport(previews, compacted, merged, purged, deleted, DeleteMafRuns(now.AddDays(-o.MafRunDays)));
    }

    /// <summary>A paused MAF run older than this has long expired in the application (Approval:ExpiryHours).</summary>
    private int DeleteMafRuns(DateTime cutoff)
    {
        if (!Directory.Exists(maf.StateDirectory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(maf.StateDirectory, "*.json"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                File.Delete(file);
                deleted++;
            }
        }

        return deleted;
    }
}
