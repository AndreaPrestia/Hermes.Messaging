namespace Hermes.Messaging.Infrastructure
{
    public interface IMessageBus
    {
        /// <summary>
        /// Publishes a message to a route. The returned <see cref="PublishResult"/> means the
        /// message was durably committed before this call returned (persist-before-signal).
        /// </summary>
        /// <remarks>
        /// The route is validated before durable acceptance; an unknown route is rejected and
        /// nothing is persisted. Once the durable commit succeeds, cancellation is not surfaced
        /// as a failure — the accepted result is returned even if the channel signal is skipped.
        /// </remarks>
        ValueTask<PublishResult> PublishAsync<T>(
            string route,
            T message,
            PublishOptions? options = null,
            CancellationToken cancellationToken = default);
    }
}
