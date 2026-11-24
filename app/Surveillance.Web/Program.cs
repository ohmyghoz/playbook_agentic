using Microsoft.EntityFrameworkCore;
using Surveillance.Web.Agents;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddDbContextFactory<SurveillanceDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is not set. Store it with: dotnet user-secrets set ConnectionStrings:Default \"...\""),
        // Azure SQL serverless databases take a while to resume from auto-pause.
        sql => sql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null)));

builder.Services.AddAgentClients(builder.Configuration);
builder.Services.AddSingleton<AgentSelection>();
builder.Services.AddSingleton<EventBroadcaster>();
builder.Services.AddSingleton<RunPump>();
builder.Services.AddSingleton<InvestigationService>();
builder.Services.AddHostedService<StartupRecovery>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

public partial class Program;
