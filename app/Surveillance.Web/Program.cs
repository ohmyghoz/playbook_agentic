using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Surveillance.Web.Agents;
using Surveillance.Web.Auth;
using Surveillance.Web.Data;
using Surveillance.Web.Hubs;
using Surveillance.Web.Investigations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Demo authentication: pick an account on /account/login, no password (see DemoUserDirectory).
builder.Services.AddSingleton<DemoUserDirectory>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/account/denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization(options =>
{
    // Every page needs a signed-in user unless it says [AllowAnonymous].
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddDbContextFactory<SurveillanceDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is not set. Store it with: dotnet user-secrets set ConnectionStrings:Default \"...\""),
        // Azure SQL serverless databases take a while to resume from auto-pause.
        sql => sql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null)));

// Data access goes through repositories only (Data/Repositories; checked by ArchitectureTests).
builder.Services.AddSingleton<Surveillance.Web.Data.Repositories.ICaseRepository, Surveillance.Web.Data.Repositories.CaseRepository>();
builder.Services.AddSingleton<Surveillance.Web.Data.Repositories.IEventRepository, Surveillance.Web.Data.Repositories.EventRepository>();
builder.Services.AddSingleton<Surveillance.Web.Data.Repositories.IApprovalRepository, Surveillance.Web.Data.Repositories.ApprovalRepository>();
builder.Services.AddSingleton<Surveillance.Web.Data.Repositories.ISettingsRepository, Surveillance.Web.Data.Repositories.SettingsRepository>();
builder.Services.AddSingleton<DatabaseMigrator>();

// Development only: start the MCP server and the Python agent services with the app.
LocalServicesLauncher.AddTo(builder);

builder.Services.AddAgentClients(builder.Configuration);
builder.Services.AddSingleton<AgentSelection>();
builder.Services.AddSingleton<EventBroadcaster>();
builder.Services.AddSingleton<RunPump>();
builder.Services.AddSingleton<InvestigationService>();
builder.Services.AddHostedService<StartupRecovery>();

// Approval deadlines, claims and the audit timeline (Bab 12).
builder.Services.Configure<ApprovalOptions>(builder.Configuration.GetSection("Approval"));
builder.Services.AddSingleton<CaseClaims>();
builder.Services.AddSingleton<AuditTimeline>();
builder.Services.AddSingleton<ApprovalExpiryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ApprovalExpiryService>());

// Use case 2: screening (ordinary code), background job runner, notifications (CHANGE-002).
builder.Services.Configure<Surveillance.Web.Patterns.PatternDetectionOptions>(builder.Configuration.GetSection("PatternDetection"));
if (builder.Configuration["PatternDetection:MarketData"] == "fake")
{
    builder.Services.AddSingleton<Surveillance.Web.Patterns.IMarketData, Surveillance.Web.Patterns.FakeMarketData>();
}
else
{
    builder.Services.AddSingleton<Surveillance.Web.Patterns.IMarketData, Surveillance.Web.Patterns.McpMarketData>();
}

builder.Services.AddSingleton<Surveillance.Web.Patterns.CandidateScreening>();
builder.Services.AddSingleton<Surveillance.Web.Patterns.PatternJobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Surveillance.Web.Patterns.PatternJobRunner>());
builder.Services.AddHostedService<Surveillance.Web.Patterns.JobNotifier>();

// Retention: old previews and events, token compaction, stale MAF run files (docs/RETENTION.md).
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());

// Event transports to the browser: SSE (controller), polling (events.json) and SignalR (hub).
builder.Services.Configure<TransportOptions>(builder.Configuration.GetSection("Transport"));
builder.Services.AddSignalR();
// The /lab/sync counter-example gives up like a proxy or gateway would.
builder.Services.AddRequestTimeouts(options => options.AddPolicy(Surveillance.Web.Controllers.LabController.SyncTimeoutPolicy,
    TimeSpan.FromSeconds(builder.Configuration.GetValue("Lab:SyncTimeoutSeconds", 20))));
builder.Services.AddHostedService<InvestigationHubRelay>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

// Empty 4xx/5xx responses (NotFound(), Forbid()) render the Indonesian error page; JSON bodies pass through.
app.UseStatusCodePagesWithReExecute("/error/{0}");

app.UseHttpsRedirection();
app.UseRouting();
app.UseRequestTimeouts();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();

app.MapHub<InvestigationHub>(InvestigationHub.Path);

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

public partial class Program;
