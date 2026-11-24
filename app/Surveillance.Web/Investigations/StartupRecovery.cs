using Microsoft.EntityFrameworkCore;
using Surveillance.Web.Data;

namespace Surveillance.Web.Investigations;

/// <summary>
/// Applies migrations in Development and re-attaches pumps to runs that were
/// streaming when the application last stopped.
/// </summary>
public sealed class StartupRecovery(
    IDbContextFactory<SurveillanceDbContext> dbFactory,
    InvestigationService investigations,
    IHostEnvironment environment) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (environment.IsDevelopment())
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Database.MigrateAsync(ct);
        }

        await investigations.RecoverAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
