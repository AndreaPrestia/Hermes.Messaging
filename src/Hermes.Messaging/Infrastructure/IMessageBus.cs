namespace Hermes.Messaging.Infrastructure
{
    public interface IMessageBus
    {
        ValueTask PublishAsync<T>(string path, T message, CancellationToken cancellationToken = default);
    }
}
