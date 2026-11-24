namespace Surveillance.Web.Tests.Contract;

/// <summary>
/// A test against a real model and the real idx MCP server. Skipped unless
/// RUN_LIVE_TESTS=1; run them with <c>dotnet test --filter Category=Live</c>
/// after scripts/start-mcp.ps1 is running.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_LIVE_TESTS") != "1")
        {
            Skip = "Live test: set RUN_LIVE_TESTS=1 and start the idx MCP server.";
        }

        Timeout = 600_000;
    }
}
