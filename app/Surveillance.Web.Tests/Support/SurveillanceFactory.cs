using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;

namespace Surveillance.Web.Tests.Support;

/// <summary>
/// Runs the real application with the fake agent and a throwaway SQLite file
/// instead of SQL Server. Requests are signed in by <see cref="TestAuthHandler"/>
/// unless <see cref="UseCookieAuth"/> keeps the application's own cookie sign-in.
/// </summary>
public class SurveillanceFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"surveillance-test-{Guid.NewGuid():N}.db");

    protected virtual int ToolDelayMs => 0;
    protected virtual bool UseCookieAuth => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", "unused-in-tests");
        builder.UseSetting("Agent:Provider", "fake");
        builder.UseSetting("Agent:Fake:Scenario", "Happy");
        builder.UseSetting("Agent:Fake:ToolDelayMs", ToolDelayMs.ToString());
        builder.UseSetting("Agent:Fake:TokenDelayMs", "0");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextFactory<SurveillanceDbContext>>();
            services.RemoveAll<DbContextOptions<SurveillanceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<SurveillanceDbContext>>();
            services.AddDbContextFactory<SurveillanceDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        });

        if (!UseCookieAuth)
        {
            builder.ConfigureTestServices(services => services
                .AddAuthentication(TestAuthHandler.Scheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { }));
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // The schema must exist before the host starts, because StartupRecovery queries it.
        var options = new DbContextOptionsBuilder<SurveillanceDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using (var db = new SurveillanceDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        return base.CreateHost(builder);
    }

    public InvestigationService Investigations => Services.GetRequiredService<InvestigationService>();

    /// <summary>A client signed in as the given demo account (see <see cref="TestAuthHandler"/>).</summary>
    public HttpClient CreateClientAs(string userName)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.Header, userName);
        return client;
    }
    public RunPump Pump => Services.GetRequiredService<RunPump>();

    public async Task<InvestigationCase> WaitForStatusAsync(Guid caseId, Func<CaseStatus, bool> done, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var c = await Investigations.GetCaseAsync(caseId, default);
            if (c is not null && done(c.Status))
            {
                return c;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Case {caseId} stayed {c?.Status}.");
            }

            await Task.Delay(25);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best effort; the file lives in the temp directory.
        }
    }
}

/// <summary>The application's real cookie sign-in, for the login flow.</summary>
public class CookieAuthSurveillanceFactory : SurveillanceFactory
{
    protected override bool UseCookieAuth => true;
}

/// <summary>Slow tools, so a run can be stopped mid-flight.</summary>
public class SlowSurveillanceFactory : SurveillanceFactory
{
    protected override int ToolDelayMs => 400;
}
