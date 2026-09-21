namespace Hermes.Messaging.Domain.Entities;

/// <summary>
/// Status of a persisted message in the store.
/// </summary>
/// <remarks>
/// State machine (single-process):
/// <code>
/// Pending         -> Processing -> Completed
/// Processing      -> RetryScheduled
/// Processing      -> DeadLettered
/// RetryScheduled  -> Processing        (when NextAttemptAt is due)
/// DeadLettered    -> Pending           (explicit replay)
/// Processing      -> Pending           (startup interrupted-recovery)
/// </code>
/// </remarks>
public enum MessageStatus
{
    /// <summary>
    /// Message is durably accepted and waiting to be claimed for processing.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Message was processed successfully.
    /// </summary>
    Completed = 1,

    /// <summary>
    /// Retired ambiguous state. No longer written by Hermes; retained only so that
    /// databases created before HERMES-002 can still deserialize. Treated as interrupted
    /// work and recovered to <see cref="Pending"/> on startup.
    /// </summary>
    [Obsolete("Retired in HERMES-002. Use RetryScheduled/Processing. Kept only for backward deserialization.")]
    Failed = 2,

    /// <summary>
    /// Message is in the dead letter queue (durable).
    /// </summary>
    DeadLettered = 3,

    /// <summary>
    /// Message has been claimed by a worker and is currently being processed.
    /// Recovered to <see cref="Pending"/> on startup if the process was interrupted.
    /// </summary>
    Processing = 4,

    /// <summary>
    /// Message failed a processing attempt and is scheduled for a future retry at
    /// <see cref="PersistedMessage{T}.NextAttemptAt"/>.
    /// </summary>
    RetryScheduled = 5
}

/// <summary>
/// Represents a message persisted to storage for crash recovery.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
public sealed class PersistedMessage<T>
{
    /// <summary>
    /// Unique identifier for the persisted message. This is the durable primary key
    /// and is the same value as <see cref="MessageId"/>.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Unique technical identity of the message. Used as the durable unique key.
    /// </summary>
    public Guid MessageId { get; set; }

    /// <summary>
    /// Logical correlation ID from the original channel message.
    /// This value is NOT unique — multiple messages may share it.
    /// </summary>
    public Guid CorrelationId { get; set; }
    
    /// <summary>
    /// Route path for the message.
    /// </summary>
    public string Path { get; set; } = string.Empty;
    
    /// <summary>
    /// Message payload.
    /// </summary>
    public T Body { get; set; } = default!;
    
    /// <summary>
    /// Current status of the message.
    /// </summary>
    public MessageStatus Status { get; set; }
    
    /// <summary>
    /// Number of processing attempts.
    /// </summary>
    public int AttemptCount { get; set; }
    
    /// <summary>
    /// When the message was first persisted.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
    
    /// <summary>
    /// When the message status was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The earliest time a <see cref="MessageStatus.RetryScheduled"/> message becomes due
    /// for another attempt. Null when not scheduled for retry.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>
    /// Last error message if a processing attempt failed.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Schema version of this persisted record. Enables forward migration and prevents
    /// silently mishandling records written by an incompatible version.
    /// </summary>
    public int SchemaVersion { get; set; } = PersistedMessageSchema.CurrentVersion;
}

/// <summary>
/// Versioning constants for the persisted message schema.
/// </summary>
public static class PersistedMessageSchema
{
    /// <summary>
    /// Current persisted schema version.
    /// v1: baseline (MessageId identity, state machine, retry, DLQ, schema version field).
    /// </summary>
    public const int CurrentVersion = 1;
}
