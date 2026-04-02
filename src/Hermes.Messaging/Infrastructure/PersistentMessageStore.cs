using LiteDB;

using Hermes.Messaging.Domain.Entities;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// LiteDB-based persistent storage for message bus messages.
/// Enables crash recovery and replay of unprocessed messages.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
public sealed class PersistentMessageStore<T> : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PersistedMessage<T>> _messages;
    private readonly TimeSpan _completedRetention;
    private readonly object _writeLock = new();
    private volatile bool _disposed;

    public PersistentMessageStore(
        string databasePath,
        TimeSpan? completedRetention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _completedRetention = completedRetention ?? TimeSpan.FromDays(7);
        _db = new LiteDatabase(databasePath);
        _messages = _db.GetCollection<PersistedMessage<T>>($"messages_{typeof(T).Name}");
        
        // Create indexes for efficient queries
        _messages.EnsureIndex(x => x.CorrelationId, unique: true);
        _messages.EnsureIndex(x => x.Status);
        _messages.EnsureIndex(x => x.CreatedAt);
    }

    /// <summary>
    /// Persists a new message to the store.
    /// </summary>
    public void Persist(ChannelMessage<T> message)
    {
        var persisted = new PersistedMessage<T>
        {
            Id = Guid.NewGuid(),
            CorrelationId = message.CorrelationId,
            Path = message.Path,
            Body = message.Body,
            Status = MessageStatus.Pending,
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _messages.Insert(persisted);
        }
    }

    /// <summary>
    /// Updates the status of a message.
    /// </summary>
    public void UpdateStatus(Guid correlationId, MessageStatus status, string? error = null)
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            var message = _messages.FindOne(x => x.CorrelationId == correlationId);
            if (message != null)
            {
                message.Status = status;
                message.UpdatedAt = DateTimeOffset.UtcNow;
                message.LastError = error;
                _messages.Update(message);
            }
        }
    }

    /// <summary>
    /// Increments the attempt count for a message.
    /// </summary>
    public void IncrementAttempt(Guid correlationId)
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            var message = _messages.FindOne(x => x.CorrelationId == correlationId);
            if (message != null)
            {
                message.AttemptCount++;
                message.UpdatedAt = DateTimeOffset.UtcNow;
                _messages.Update(message);
            }
        }
    }

    /// <summary>
    /// Gets all pending messages for replay after crash.
    /// </summary>
    public IEnumerable<PersistedMessage<T>> GetPendingMessages()
    {
        return _messages.Query()
            .Where(x => x.Status == MessageStatus.Pending)
            .OrderBy(x => x.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Gets a message by correlation ID.
    /// </summary>
    public PersistedMessage<T>? GetByCorrelationId(Guid correlationId)
    {
        return _messages.FindOne(x => x.CorrelationId == correlationId);
    }

    /// <summary>
    /// Cleans up old completed messages based on retention policy.
    /// </summary>
    public int CleanupOldMessages()
    {
        lock (_writeLock)
        {
            if (_disposed) return 0;
            var cutoff = DateTimeOffset.UtcNow - _completedRetention;

            return _messages.DeleteMany(x =>
                (x.Status == MessageStatus.Completed || x.Status == MessageStatus.DeadLettered)
                && x.UpdatedAt < cutoff);
        }
    }

    /// <summary>
    /// Gets statistics about messages in the store.
    /// </summary>
    public MessageStoreStats GetStats()
    {
        return new MessageStoreStats
        {
            PendingCount = _messages.Count(x => x.Status == MessageStatus.Pending),
            CompletedCount = _messages.Count(x => x.Status == MessageStatus.Completed),
            FailedCount = _messages.Count(x => x.Status == MessageStatus.Failed),
            DeadLetteredCount = _messages.Count(x => x.Status == MessageStatus.DeadLettered),
            TotalCount = _messages.Count()
        };
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _disposed = true;
        }
        _db?.Dispose();
    }
}

/// <summary>
/// Statistics about messages in the persistent store.
/// </summary>
public sealed record MessageStoreStats
{
    public int PendingCount { get; init; }
    public int CompletedCount { get; init; }
    public int FailedCount { get; init; }
    public int DeadLetteredCount { get; init; }
    public int TotalCount { get; init; }
}
