namespace Hermes.Messaging.Domain.Entities;

/// <summary>
/// Status of a persisted message in the store.
/// </summary>
public enum MessageStatus
{
    /// <summary>
    /// Message is waiting to be processed or being processed.
    /// </summary>
    Pending = 0,
    
    /// <summary>
    /// Message was processed successfully.
    /// </summary>
    Completed = 1,
    
    /// <summary>
    /// Message failed after all retry attempts.
    /// </summary>
    Failed = 2,
    
    /// <summary>
    /// Message is in the dead letter queue.
    /// </summary>
    DeadLettered = 3
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
    /// Last error message if failed.
    /// </summary>
    public string? LastError { get; set; }
}
