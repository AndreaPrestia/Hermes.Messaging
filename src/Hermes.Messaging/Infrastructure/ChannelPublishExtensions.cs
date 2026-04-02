using System.Threading.Channels;

using Hermes.Messaging.Domain.Entities;

namespace Hermes.Messaging.Infrastructure;

public static class ChannelPublishExtensions
{
    public static ValueTask PublishAsync<T>(
        this Channel<ChannelMessage<T>> channel,
        string path,
        T message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var envelope = new ChannelMessage<T>(path, message, correlationId);

        if (TryWrite(channel, envelope, path))
        {
            return ValueTask.CompletedTask;
        }

        return WriteWithBackpressureAsync(channel, envelope, path, cancellationToken);
    }

    private static bool TryWrite<T>(Channel<ChannelMessage<T>> channel, ChannelMessage<T> envelope, string path)
    {
        if (channel.Writer.TryWrite(envelope))
        {
            ChannelMetrics.RecordEnqueued(typeof(T), path);
            return true;
        }

        return false;
    }

    private static async ValueTask WriteWithBackpressureAsync<T>(Channel<ChannelMessage<T>> channel, ChannelMessage<T> envelope, string path, CancellationToken cancellationToken)
    {
        while (await channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            if (channel.Writer.TryWrite(envelope))
            {
                ChannelMetrics.RecordEnqueued(typeof(T), path);
                return;
            }
        }

        ChannelMetrics.RecordDropped(typeof(T), path);
    }
}
