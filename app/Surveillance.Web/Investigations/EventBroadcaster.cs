using System.Collections.Concurrent;
using System.Threading.Channels;
using Surveillance.Web.Data;

namespace Surveillance.Web.Investigations;

/// <summary>
/// In-process fan-out of persisted events to open SSE connections. Events are
/// published only after they are saved, so the database stays the source of truth.
/// </summary>
public sealed class EventBroadcaster
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<AgentEventRecord>>> _subscribers = new();

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
