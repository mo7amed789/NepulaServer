using System.Collections.Concurrent;
using System.Threading.Channels;
using NebulaServer.Models.Jobs;

namespace NebulaServer.Services;

public sealed class PlaylistEventBus
{
    private readonly ConcurrentDictionary<string, Channel<PlaylistItemResult>> _channels = new(StringComparer.OrdinalIgnoreCase);

    public ChannelReader<PlaylistItemResult> Subscribe(string jobId)
    {
        return _channels.GetOrAdd(jobId, CreateChannel).Reader;
    }

    public void Publish(string jobId, PlaylistItemResult item)
    {
        var channel = _channels.GetOrAdd(jobId, CreateChannel);
        channel.Writer.TryWrite(item);
    }

    public void Complete(string jobId)
    {
        if (_channels.TryRemove(jobId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    private static Channel<PlaylistItemResult> CreateChannel(string _)
    {
        return Channel.CreateUnbounded<PlaylistItemResult>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false
        });
    }
}
