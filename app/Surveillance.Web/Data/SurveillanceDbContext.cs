using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Surveillance.Web.Data;

public class SurveillanceDbContext(DbContextOptions<SurveillanceDbContext> options) : DbContext(options)
{
    public DbSet<InvestigationCase> Cases => Set<InvestigationCase>();
    public DbSet<AgentEventRecord> Events => Set<AgentEventRecord>();
    public DbSet<ApprovalDecisionRecord> Decisions => Set<ApprovalDecisionRecord>();

    /// <summary>All timestamps are stored as UTC and read back with <see cref="DateTimeKind.Utc"/>.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InvestigationCase>(e =>
        {
            e.Property(x => x.Symbol).HasMaxLength(8);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.RunId).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.CreatedAt);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.CaseId);
            e.HasMany(x => x.Decisions).WithOne().HasForeignKey(x => x.CaseId);
        });

        modelBuilder.Entity<AgentEventRecord>(e =>
        {
            e.Property(x => x.RunId).HasMaxLength(128);
            e.Property(x => x.Type).HasMaxLength(32);
            e.HasIndex(x => new { x.RunId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.CaseId, x.Sequence });
        });

        modelBuilder.Entity<ApprovalDecisionRecord>(e =>
        {
            e.Property(x => x.ApprovalId).HasMaxLength(128);
            e.Property(x => x.Decision).HasMaxLength(16);
            e.Property(x => x.DecidedBy).HasMaxLength(128);
        });
    }
}
