namespace Hermes.Messaging;

/// <summary>
/// Result of a successful <see cref="IMessageBus.PublishAsync{T}"/> call.
/// </summary>
/// <remarks>
/// A returned <see cref="PublishResult"/> means the message was durably committed
/// before this result was produced. The durable store is the source of truth; the
/// in-process channel signal is only a wake-up/acceleration mechanism.
/// </remarks>
public sealed record PublishResult
{
    /// <summary>
    /// Unique technical identity of the accepted message.
    /// </summary>
    public required Guid MessageId { get; init; }

    /// <summary>
    /// Logical correlation identifier (non-unique).
    /// </summary>
    public required Guid CorrelationId { get; init; }

    /// <summary>
    /// The moment the message became durably accepted.
    /// </summary>
    public required DateTimeOffset AcceptedAt { get; init; }
}
