using System.Diagnostics;

namespace Surveillance.Web.Agents;

/// <summary>
/// Development convenience: when the app starts, runs scripts/start-all.ps1 so the idx MCP server and
/// the LangGraph and ADK services are up without a separate step. The script skips services that
/// already answer their health check, so restarting the app does not start them twice. The services
/// run in their own windows and keep running after the app stops; close a window to stop one.
/// Enabled by LocalServices:AutoStart (appsettings.Development.json), Windows only.
/// </summary>
public sealed class LocalServicesLauncher(IHostEnvironment environment, IConfiguration configuration,
    ILogger<LocalServicesLauncher> logger) : BackgroundService
{
    public const string Section = "LocalServices";
    private const string TokenName = "IDX_MCP_TOKEN";

    /// <summary>
    /// Registers the launcher and fills IDX_MCP_TOKEN from the User environment when the process lacks
    /// it: a terminal opened before the token was saved does not pass it on, and the screening code
    /// and the MAF adapter would then refuse to call the MCP server.
    /// </summary>
    public static void AddTo(WebApplicationBuilder builder)
    {
        if (!OperatingSystem.IsWindows() || !builder.Configuration.GetValue<bool>($"{Section}:AutoStart"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(builder.Configuration[TokenName]))
        {
            var token = Environment.GetEnvironmentVariable(TokenName, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(token))
            {
                // The process copy is inherited by the script and the service windows it opens.
                Environment.SetEnvironmentVariable(TokenName, token);
                builder.Configuration.AddInMemoryCollection([new(TokenName, token)]);
            }
        }

        builder.Services.AddHostedService<LocalServicesLauncher>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.GetFullPath(configuration[$"{Section}:RepositoryRoot"]
                                    ?? Path.Combine(environment.ContentRootPath, "..", ".."));
        var script = Path.Combine(root, "scripts", "start-all.ps1");
        if (!File.Exists(script))
        {
            logger.LogWarning("Local services not started: {Script} not found.", script);
            return;
        }

        var start = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script })
        {
            start.ArgumentList.Add(arg);
        }

        logger.LogInformation("Starting local services with {Script}.", script);
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(stoppingToken);
            var error = process.StandardError.ReadToEndAsync(stoppingToken);
            await process.WaitForExitAsync(stoppingToken);

            // The script prints a table of each service and its status (siap, sudah berjalan, dilewati).
            logger.LogInformation("Local services:{NewLine}{Output}", Environment.NewLine, (await output).TrimEnd());
            var errors = (await error).Trim();
            if (process.ExitCode != 0 || errors.Length > 0)
            {
                logger.LogWarning("start-all.ps1 exited with {ExitCode}: {Errors}", process.ExitCode, errors);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local services could not be started; run scripts/start-all.ps1 yourself.");
        }
    }
}
