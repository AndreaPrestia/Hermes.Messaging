namespace Hermes.Messaging.Infrastructure;

public sealed class InMemoryMessageBus : IMessageBus
{
    private readonly ChannelRegistry _registry;

    public InMemoryMessageBus(ChannelRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public ValueTask PublishAsync<T>(string path, T message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(message);

        var channel = _registry.GetOrCreate<T>();
        return channel.PublishAsync(path, message, cancellationToken);
    }
}
