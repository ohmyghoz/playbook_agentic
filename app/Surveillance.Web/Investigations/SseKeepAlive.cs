using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;

namespace Surveillance.Web.Investigations;

/// <summary>
/// Keeps an SSE response alive through proxies that close idle connections (IIS/ARR, nginx,
/// Azure App Service). One <c>ping</c> opens the response right away; after that, while no
/// event arrives for <c>interval</c>, another <c>ping</c> event without
/// an id is written; browsers ignore it because no listener is registered for that type, and
/// Last-Event-ID does not move. Every real event also carries <c>retry:</c>, so EventSource
/// reconnects after a short, known delay. See docs/PROXY.md.
/// </summary>
public static class SseKeepAlive
{
    public const string PingEvent = "ping";

    public static async IAsyncEnumerable<SseItem<string>> WithPings(
        IAsyncEnumerable<SseItem<string>> items, TimeSpan interval, TimeSpan retry,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Write something at once: the response (and EventSource's onopen) starts only with the first bytes,
        // and a paused run may have nothing to send for a long time.
        yield return new SseItem<string>("", PingEvent);

        await using var source = items.GetAsyncEnumerator(ct);
        var next = source.MoveNextAsync().AsTask();
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timer = Task.Delay(interval, idle.Token);
            var first = await Task.WhenAny(next, timer);
            if (ct.IsCancellationRequested)
            {
                // Let the inner stream observe the cancellation before it is disposed.
                try
                {
                    await next;
                }
                catch (OperationCanceledException)
                {
                }

                yield break;
            }

            if (first == timer)
            {
                yield return new SseItem<string>("", PingEvent);
                continue;
            }

            idle.Cancel();
            if (!await next)
            {
                yield break;
            }

            var item = source.Current;
            yield return new SseItem<string>(item.Data, item.EventType) { EventId = item.EventId, ReconnectionInterval = retry };
            next = source.MoveNextAsync().AsTask();
        }
    }
}
