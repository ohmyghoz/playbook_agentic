namespace Surveillance.Web.Investigations;

/// <summary>
/// How the browser receives investigation events (configuration section "Transport").
/// The three transports read the same stored events; only the delivery differs.
/// </summary>
public sealed class TransportOptions
{
    public const string Sse = "sse";
    public const string Polling = "polling";
    public const string SignalR = "signalr";

    public static readonly IReadOnlyList<string> All = [Sse, Polling, SignalR];

    /// <summary>Used when the page has no ?transport= parameter.</summary>
    public string Default { get; set; } = Sse;

    /// <summary>Idle time before the SSE endpoint writes a ping; below common proxy idle limits.</summary>
    public int SsePingSeconds { get; set; } = 15;

    /// <summary>The <c>retry:</c> value sent with SSE events: how long EventSource waits before reconnecting.</summary>
    public int SseRetryMilliseconds { get; set; } = 3000;

    /// <summary>How often the polling transport asks for new events.</summary>
    public int PollingIntervalMilliseconds { get; set; } = 2000;

    public string Resolve(string? requested) =>
        requested is not null && All.Contains(requested) ? requested : All.Contains(Default) ? Default : Sse;
}
