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
internal interface IMessageStore<T>
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
    /// Decrements the attempt count (floored at zero). Used to undo a claim's attempt increment
    /// when no real attempt was made (e.g. an open circuit skipped dispatch).
    /// </summary>
    void DecrementAttempt(Guid messageId);

    /// <summary>
    /// Gets all pending messages, ordered by creation time, for replay after restart.
    /// </summary>
    IEnumerable<PersistedMessage<T>> GetPendingMessages();

    /// <summary>
    /// Atomically claims a message for processing. Succeeds only if the message currently
    /// exists and is claimable (Pending, or RetryScheduled whose NextAttemptAt is due).
    /// On success it transitions the message to <see cref="MessageStatus.Processing"/>,
    /// increments the attempt count, and returns the claimed record.
    /// </summary>
    /// <returns>The claimed record, or null if the message was not claimable (e.g. already
    /// Processing/Completed/DeadLettered, missing, or a duplicate signal).</returns>
    PersistedMessage<T>? TryClaim(Guid messageId);

    /// <summary>
    /// Transitions a <see cref="MessageStatus.Processing"/> message to
    /// <see cref="MessageStatus.Completed"/>.
    /// </summary>
    void MarkCompleted(Guid messageId);

    /// <summary>
    /// Transitions a message to <see cref="MessageStatus.RetryScheduled"/> with the given
    /// due time and error.
    /// </summary>
    void ScheduleRetry(Guid messageId, DateTimeOffset nextAttemptAt, string? error);

    /// <summary>
    /// Transitions a message to <see cref="MessageStatus.DeadLettered"/>.
    /// </summary>
    void MarkDeadLettered(Guid messageId, string? error);

    /// <summary>
    /// Startup recovery: transitions every interrupted message (Processing, or the retired
    /// Failed state) back to <see cref="MessageStatus.Pending"/> so it can be re-claimed.
    /// </summary>
    /// <returns>The number of messages recovered.</returns>
    int RecoverInterrupted();

    /// <summary>
    /// Returns messages that are due for processing now: all Pending messages plus any
    /// RetryScheduled message whose NextAttemptAt is at or before <paramref name="now"/>.
    /// Ordered by creation time.
    /// </summary>
    IEnumerable<PersistedMessage<T>> GetDueMessages(DateTimeOffset now);

    /// <summary>
    /// Returns at most <paramref name="limit"/> due-message IDs (Pending, or RetryScheduled whose
    /// NextAttemptAt &lt;= <paramref name="now"/>), oldest-first. The limit is enforced at the query
    /// level and only IDs are materialized — full payloads are not loaded. This lets reconciliation
    /// scan only as much of the durable backlog as the wake-up channel can actually accept.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="limit"/> is &lt;= 0.</exception>
    IReadOnlyList<Guid> GetDueMessageIds(DateTimeOffset now, int limit);

    /// <summary>
    /// Explicitly replays a dead-lettered message by returning it to
    /// <see cref="MessageStatus.Pending"/>. Returns false if the message is not dead-lettered.
    /// </summary>
    bool ReplayDeadLetter(Guid messageId);

    /// <summary>
    /// Lists dead-lettered messages ordered by last-updated time, with paging.
    /// </summary>
    IReadOnlyList<PersistedMessage<T>> ListDeadLetters(int skip = 0, int take = 100);

    /// <summary>
    /// Deletes a single dead-lettered message by id. Returns false if it is not dead-lettered.
    /// </summary>
    bool DeleteDeadLetter(Guid messageId);

    /// <summary>
    /// Deletes all dead-lettered messages. Returns the number removed.
    /// </summary>
    int PurgeDeadLetters();

    /// <summary>
    /// Returns durable-state statistics for this store (counts per status).
    /// </summary>
    MessageStoreStats GetStats();

    /// <summary>
    /// Removes old Completed messages per the retention policy. Dead-lettered messages are never
    /// removed here. Returns the number deleted.
    /// </summary>
    int CleanupOldMessages();
}
