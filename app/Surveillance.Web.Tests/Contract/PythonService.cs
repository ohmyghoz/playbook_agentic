using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests.Contract;

/// <summary>
/// Runs one of the Python agent services (agents/langgraph or agents/adk) as a real
/// uvicorn process on a free port, with its own data directory. With a scenarios file it
/// uses the scripted model and canned tools; without one it talks to the real model and MCP server.
/// </summary>
public sealed class PythonService : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _dataDir;
    private readonly StringBuilder _log = new();

    private PythonService(Process process, string dataDir, Uri baseUrl)
    {
        _process = process;
        _dataDir = dataDir;
        BaseUrl = baseUrl;
    }

    public Uri BaseUrl { get; }

    public string Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    public static string ScenariosPath => Path.Combine(RepoPaths.Root, "app", "Surveillance.Web.Tests", "Contract", "scenarios.json");

    /// <param name="service">"langgraph" or "adk".</param>
    public static async Task<PythonService> StartAsync(string service, bool scripted, int? maxToolCalls = null)
    {
        var serviceDir = Path.Combine(RepoPaths.Root, "agents", service);
        var python = Path.Combine(serviceDir, ".venv", "Scripts", "python.exe");
        var port = FreePort();
        var dataDir = Path.Combine(Path.GetTempPath(), $"contract-{service}-{Guid.NewGuid():N}");

        var info = new ProcessStartInfo(python, $"-m uvicorn app.main:app --host 127.0.0.1 --port {port}")
        {
            WorkingDirectory = serviceDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment[service == "adk" ? "ADK_DATA_DIR" : "LANGGRAPH_DATA_DIR"] = dataDir;
        info.Environment["PYTHONWARNINGS"] = "ignore";
        if (scripted)
        {
            info.Environment["AGENT_SCRIPT"] = ScenariosPath;
        }
        else
        {
            info.Environment["IDX_MCP_TOKEN"] = Environment.GetEnvironmentVariable("IDX_MCP_TOKEN")
                ?? Environment.GetEnvironmentVariable("IDX_MCP_TOKEN", EnvironmentVariableTarget.User);
        }

        if (maxToolCalls is { } limit)
        {
            info.Environment["MAX_TOOL_CALLS"] = limit.ToString();
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {python}.");
        var service_ = new PythonService(process, dataDir, new Uri($"http://127.0.0.1:{port}/"));
        process.OutputDataReceived += (_, e) => service_.Append(e.Data);
        process.ErrorDataReceived += (_, e) => service_.Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var http = new HttpClient { BaseAddress = service_.BaseUrl, Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"{service} service exited:\n{service_.Log}");
            }

            try
            {
                if ((await http.GetAsync("health")).IsSuccessStatusCode)
                {
                    return service_;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(250);
        }

        await service_.DisposeAsync();
        throw new TimeoutException($"{service} service did not become healthy:\n{service_.Log}");
    }

    public HttpClient CreateHttpClient() => new() { BaseAddress = BaseUrl, Timeout = Timeout.InfiniteTimeSpan };

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // SQLite files may still be locked for a moment; the directory is under %TEMP%.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_log)
        {
            _log.AppendLine(line);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
