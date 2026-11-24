using System.Collections.Concurrent;
using System.Threading.Channels;
using Surveillance.Web.Data;

namespace Surveillance.Web.Investigations;

/// <summary>
/// In-process fan-out of persisted events to open SSE connections and to the SignalR relay.
/// Events are published only after they are saved, so the database stays the source of truth.
/// </summary>
public sealed class EventBroadcaster
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<AgentEventRecord>>> _subscribers = new();
    private readonly ConcurrentDictionary<Guid, Channel<AgentEventRecord>> _all = new();

    /// <summary>Every published event of every case, for a relay such as the SignalR hub.</summary>
    public Subscription SubscribeAll()
    {
        var channel = Channel.CreateUnbounded<AgentEventRecord>(new UnboundedChannelOptions { SingleReader = true });
        var id = Guid.NewGuid();
        _all[id] = channel;
        return new Subscription(channel.Reader, () =>
        {
            _all.TryRemove(id, out _);
            channel.Writer.TryComplete();
        });
    }

    public Subscription Subscribe(Guid caseId)
    {
        var channel = Channel.CreateUnbounded<AgentEventRecord>(new UnboundedChannelOptions { SingleReader = true });
        var id = Guid.NewGuid();
        _subscribers.GetOrAdd(caseId, _ => new())[id] = channel;
        return new Subscription(channel.Reader, () =>
        {
            if (_subscribers.TryGetValue(caseId, out var forCase))
            {
                forCase.TryRemove(id, out _);
            }

            channel.Writer.TryComplete();
        });
    }

    public void Publish(AgentEventRecord record)
    {
        foreach (var channel in _all.Values)
        {
            channel.Writer.TryWrite(record);
        }

        if (_subscribers.TryGetValue(record.CaseId, out var forCase))
        {
            foreach (var channel in forCase.Values)
            {
                channel.Writer.TryWrite(record);
            }
        }
    }

    public sealed class Subscription(ChannelReader<AgentEventRecord> reader, Action unsubscribe) : IDisposable
    {
        public ChannelReader<AgentEventRecord> Reader { get; } = reader;

        public void Dispose() => unsubscribe();
    }
}
