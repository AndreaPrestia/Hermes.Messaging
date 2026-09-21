using Hermes.Messaging.Domain.Entities;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Durable store for messages of a given payload type. This is the source of truth
/// for accepted messages; it does not expose any underlying storage engine types.
/// </summary>
/// <remarks>
/// This is the smallest useful surface for the durable publish boundary (Phase 1).
/// It is expected to grow (TryClaim, ScheduleRetry, RecoverInterrupted, etc.) in later phases.
/// </remarks>
/// <typeparam name="T">Message payload type.</typeparam>
public interface IMessageStore<T>
{
    /// <summary>
    /// Durably persists a new message in the <see cref="MessageStatus.Pending"/> state.
    /// Must have committed before it returns. The message's <see cref="ChannelMessage{T}.MessageId"/>
    /// is used as the unique durable key.
    /// </summary>
    void Insert(ChannelMessage<T> message);

    /// <summary>
    /// Gets a persisted message by its unique <see cref="ChannelMessage{T}.MessageId"/>.
    /// </summary>
    PersistedMessage<T>? GetByMessageId(Guid messageId);

    /// <summary>
    /// Updates the status of a message identified by its unique message id.
    /// </summary>
    void UpdateStatus(Guid messageId, MessageStatus status, string? error = null);

    /// <summary>
    /// Increments the attempt count for a message identified by its unique message id.
    /// </summary>
    void IncrementAttempt(Guid messageId);

    /// <summary>
    /// Gets all pending messages, ordered by creation time, for replay after restart.
    /// </summary>
    IEnumerable<PersistedMessage<T>> GetPendingMessages();
}
