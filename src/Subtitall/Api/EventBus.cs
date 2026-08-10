using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Subtitall.Api;

/// <summary>Broadcasts app events to all connected UI clients over Server-Sent Events.</summary>
public sealed class EventBus
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    public void Publish(string type, object? data = null)
    {
        var json = JsonSerializer.Serialize(new { type, data }, Json);
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(json);
    }
}
