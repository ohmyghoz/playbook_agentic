using Microsoft.EntityFrameworkCore;

namespace Surveillance.Web.Data;

/// <summary>Applies pending EF Core migrations; StartupRecovery calls it in Development.</summary>
public sealed class DatabaseMigrator(IDbContextFactory<SurveillanceDbContext> dbFactory)
{
    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}
