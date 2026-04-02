using System.Collections.Concurrent;
using System.Threading.Channels;

using Hermes.Messaging.Domain.Entities;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Instance-based channel registry. Registered as a singleton in DI.
/// Each channel is keyed by message type <typeparamref name="T"/>.
/// </summary>
public sealed class ChannelRegistry
{
    private readonly int _defaultCapacity;
    private readonly ConcurrentDictionary<Type, ChannelRegistration> _channels = new();

    public ChannelRegistry() : this(10_000) { }

    public ChannelRegistry(int defaultCapacity)
    {
        _defaultCapacity = defaultCapacity > 0 ? defaultCapacity : 10_000;
    }

    public Channel<ChannelMessage<T>> GetOrCreate<T>(Action<BoundedChannelOptions>? configure = null)
    {
        while (true)
        {
            if (_channels.TryGetValue(typeof(T), out var existing))
            {
                ValidateConfiguration<T>(existing.Snapshot, configure);
                return (Channel<ChannelMessage<T>>)existing.Channel;
            }

            var options = CreateOptions(_defaultCapacity, configure, out var snapshot);
            var channel = Channel.CreateBounded<ChannelMessage<T>>(options);
            var registration = new ChannelRegistration(channel, snapshot);

            if (_channels.TryAdd(typeof(T), registration))
            {
                return channel;
            }

            // Another registration raced us; loop to validate and return the existing instance.
        }
    }

    /// <summary>
    /// Removes all channels. Intended for testing only.
    /// </summary>
    public void Clear() => _channels.Clear();

    private static BoundedChannelOptions CreateOptions(int defaultCapacity, Action<BoundedChannelOptions>? configure, out ChannelOptionsSnapshot snapshot)
    {
        var options = new BoundedChannelOptions(defaultCapacity)
        {
            // SingleReader=true is a performance optimization.
            // The PersistentChannelRouterSubscriber guarantees a single reader loop
            // even with MaxConcurrency > 1 (worker tasks receive already-dequeued items).
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        };

        configure?.Invoke(options);

        snapshot = new ChannelOptionsSnapshot(options.Capacity, options.SingleReader, options.SingleWriter, options.FullMode);
        return options;
    }

    private static void ValidateConfiguration<T>(ChannelOptionsSnapshot existing, Action<BoundedChannelOptions>? configure)
    {
        if (configure is null)
        {
            return;
        }

        // Use a dummy capacity — the configure action will override it if needed
        CreateOptions(existing.Capacity, configure, out var requested);

        if (!existing.Equals(requested))
        {
            throw new InvalidOperationException($"Channel '{typeof(T).Name}' already registered with a different configuration.");
        }
    }

    private sealed record ChannelRegistration(object Channel, ChannelOptionsSnapshot Snapshot);

    private readonly record struct ChannelOptionsSnapshot(int Capacity, bool SingleReader, bool SingleWriter, BoundedChannelFullMode FullMode);
}
