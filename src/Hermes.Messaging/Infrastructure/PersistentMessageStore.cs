using LiteDB;

using Hermes.Messaging.Domain.Entities;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// LiteDB-based persistent storage for message bus messages.
/// Enables crash recovery and replay of unprocessed messages.
/// </summary>
/// <typeparam name="T">Message payload type.</typeparam>
public sealed class PersistentMessageStore<T> : IMessageStore<T>, IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PersistedMessage<T>> _messages;
    private readonly TimeSpan _completedRetention;
    private readonly TimeProvider _timeProvider;
    private readonly object _writeLock = new();
    private volatile bool _disposed;

    public PersistentMessageStore(
        string databasePath,
        TimeSpan? completedRetention = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _completedRetention = completedRetention ?? TimeSpan.FromDays(7);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _db = new LiteDatabase(databasePath);
        _messages = _db.GetCollection<PersistedMessage<T>>($"messages_{typeof(T).Name}");

        // MessageId is the unique technical identity and durable key.
        // CorrelationId is a logical, NON-unique value — never a unique key.
        _messages.EnsureIndex(x => x.MessageId, unique: true);
        _messages.EnsureIndex(x => x.CorrelationId, unique: false);
        _messages.EnsureIndex(x => x.Status);
        _messages.EnsureIndex(x => x.CreatedAt);
    }

    /// <summary>
    /// Durably persists a new message to the store in the Pending state.
    /// The commit has completed before this method returns.
    /// </summary>
    public void Insert(ChannelMessage<T> message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var persisted = new PersistedMessage<T>
        {
            Id = message.MessageId,
            MessageId = message.MessageId,
            CorrelationId = message.CorrelationId,
            Path = message.Path,
            Body = message.Body,
            Status = MessageStatus.Pending,
            AttemptCount = 0,
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow(),
            NextAttemptAt = null,
            SchemaVersion = PersistedMessageSchema.CurrentVersion
        };

        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _messages.Insert(persisted);
        }
    }

    /// <summary>
    /// Legacy alias for <see cref="Insert"/>.
    /// </summary>
    public void Persist(ChannelMessage<T> message) => Insert(message);

    /// <summary>
    /// Updates the status of a message identified by its unique message id.
    /// </summary>
    public void UpdateStatus(Guid messageId, MessageStatus status, string? error = null)
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            var message = _messages.FindOne(x => x.MessageId == messageId);
            if (message != null)
            {
                message.Status = status;
                message.UpdatedAt = _timeProvider.GetUtcNow();
                message.LastError = error;
                _messages.Update(message);
            }
        }
    }

    /// <summary>
    /// Increments the attempt count for a message identified by its unique message id.
    /// </summary>
    public void IncrementAttempt(Guid messageId)
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            var message = _messages.FindOne(x => x.MessageId == messageId);
            if (message != null)
            {
                message.AttemptCount++;
                message.UpdatedAt = _timeProvider.GetUtcNow();
                _messages.Update(message);
            }
        }
    }

    /// <summary>
    /// Decrements the attempt count (floored at zero) for a message. Used to undo a claim's
    /// attempt increment when no real attempt was made (e.g. an open circuit skipped dispatch).
    /// </summary>
    public void DecrementAttempt(Guid messageId)
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            var message = _messages.FindOne(x => x.MessageId == messageId);
            if (message != null && message.AttemptCount > 0)
            {
                message.AttemptCount--;
                message.UpdatedAt = _timeProvider.GetUtcNow();
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
    /// Atomically claims a claimable message and transitions it to Processing.
    /// </summary>
    public PersistedMessage<T>? TryClaim(Guid messageId)
    {
        lock (_writeLock)
        {
            if (_disposed) return null;

            var message = _messages.FindOne(x => x.MessageId == messageId);
            if (message is null)
            {
                return null;
            }

            var isClaimable =
                message.Status == MessageStatus.Pending ||
                (message.Status == MessageStatus.RetryScheduled &&
                 (message.NextAttemptAt is null || message.NextAttemptAt <= _timeProvider.GetUtcNow()));

            if (!isClaimable)
            {
                return null;
            }

            message.Status = MessageStatus.Processing;
            message.AttemptCount++;
            message.NextAttemptAt = null;
            message.UpdatedAt = _timeProvider.GetUtcNow();
            _messages.Update(message);
            return message;
        }
    }

    /// <summary>
    /// Transitions a message to Completed.
    /// </summary>
    public void MarkCompleted(Guid messageId) => UpdateStatus(messageId, MessageStatus.Completed);

    /// <summary>
    /// Transitions a message to RetryScheduled with a due time.
    /// </summary>
    public void ScheduleRetry(Guid messageId, DateTimeOffset nextAttemptAt, string? error)
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            var message = _messages.FindOne(x => x.MessageId == messageId);
            if (message != null)
            {
                message.Status = MessageStatus.RetryScheduled;
                message.NextAttemptAt = nextAttemptAt;
                message.LastError = error;
                message.UpdatedAt = _timeProvider.GetUtcNow();
                _messages.Update(message);
            }
        }
    }

    /// <summary>
    /// Transitions a message to DeadLettered.
    /// </summary>
    public void MarkDeadLettered(Guid messageId, string? error)
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            var message = _messages.FindOne(x => x.MessageId == messageId);
            if (message != null)
            {
                message.Status = MessageStatus.DeadLettered;
                message.NextAttemptAt = null;
                message.LastError = error;
                message.UpdatedAt = _timeProvider.GetUtcNow();
                _messages.Update(message);
            }
        }
    }

    /// <summary>
    /// Startup recovery: transitions interrupted work (Processing or retired Failed) to Pending.
    /// </summary>
    public int RecoverInterrupted()
    {
        lock (_writeLock)
        {
            if (_disposed) return 0;

#pragma warning disable CS0618 // Failed is retired but old data may still carry it.
            var interrupted = _messages.Query()
                .Where(x => x.Status == MessageStatus.Processing || x.Status == MessageStatus.Failed)
                .ToList();
#pragma warning restore CS0618

            foreach (var message in interrupted)
            {
                message.Status = MessageStatus.Pending;
                message.NextAttemptAt = null;
                message.UpdatedAt = _timeProvider.GetUtcNow();
                _messages.Update(message);
            }

            return interrupted.Count;
        }
    }

    /// <summary>
    /// Returns Pending messages and due RetryScheduled messages, ordered by creation time.
    /// </summary>
    public IEnumerable<PersistedMessage<T>> GetDueMessages(DateTimeOffset now)
    {
        return _messages.Query()
            .Where(x =>
                x.Status == MessageStatus.Pending ||
                (x.Status == MessageStatus.RetryScheduled && x.NextAttemptAt <= now))
            .OrderBy(x => x.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Explicitly replays a dead-lettered message back to Pending.
    /// </summary>
    public bool ReplayDeadLetter(Guid messageId)
    {
        lock (_writeLock)
        {
            if (_disposed) return false;
            var message = _messages.FindOne(x => x.MessageId == messageId);
            if (message is null || message.Status != MessageStatus.DeadLettered)
            {
                return false;
            }

            // Explicit replay starts a fresh processing cycle with a full retry budget (HERMES-006 P4):
            // reset the attempt count and clear the last error so the message is not re-dead-lettered
            // after a single new attempt.
            message.Status = MessageStatus.Pending;
            message.AttemptCount = 0;
            message.NextAttemptAt = null;
            message.LastError = null;
            message.UpdatedAt = _timeProvider.GetUtcNow();
            _messages.Update(message);
            return true;
        }
    }

    /// <summary>
    /// Gets a message by its unique message id.
    /// </summary>
    public PersistedMessage<T>? GetByMessageId(Guid messageId)
    {
        return _messages.FindOne(x => x.MessageId == messageId);
    }

    /// <summary>
    /// Gets the first message matching a (non-unique) correlation id.
    /// Prefer <see cref="GetByMessageId"/> for a unique lookup.
    /// </summary>
    public PersistedMessage<T>? GetByCorrelationId(Guid correlationId)
    {
        return _messages.FindOne(x => x.CorrelationId == correlationId);
    }

    /// <summary>
    /// Cleans up old Completed messages based on retention policy. Dead-lettered messages are
    /// NOT auto-deleted — the durable DLQ is retained until an explicit Delete/Purge.
    /// </summary>
    public int CleanupOldMessages()
    {
        lock (_writeLock)
        {
            if (_disposed) return 0;
            var cutoff = _timeProvider.GetUtcNow() - _completedRetention;

            return _messages.DeleteMany(x =>
                x.Status == MessageStatus.Completed && x.UpdatedAt < cutoff);
        }
    }

    /// <summary>
    /// Lists dead-lettered messages ordered by last-updated time, with paging.
    /// </summary>
    public IReadOnlyList<PersistedMessage<T>> ListDeadLetters(int skip = 0, int take = 100)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;

        return _messages.Query()
            .Where(x => x.Status == MessageStatus.DeadLettered)
            .OrderBy(x => x.UpdatedAt)
            .Skip(skip)
            .Limit(take)
            .ToList();
    }

    /// <summary>
    /// Deletes a single dead-lettered message by id. Returns false if it is not dead-lettered.
    /// </summary>
    public bool DeleteDeadLetter(Guid messageId)
    {
        lock (_writeLock)
        {
            if (_disposed) return false;
            var message = _messages.FindOne(x => x.MessageId == messageId);
            if (message is null || message.Status != MessageStatus.DeadLettered)
            {
                return false;
            }

            return _messages.Delete(message.Id);
        }
    }

    /// <summary>
    /// Deletes all dead-lettered messages. Returns the number removed.
    /// </summary>
    public int PurgeDeadLetters()
    {
        lock (_writeLock)
        {
            if (_disposed) return 0;
            return _messages.DeleteMany(x => x.Status == MessageStatus.DeadLettered);
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
            ProcessingCount = _messages.Count(x => x.Status == MessageStatus.Processing),
            RetryScheduledCount = _messages.Count(x => x.Status == MessageStatus.RetryScheduled),
            CompletedCount = _messages.Count(x => x.Status == MessageStatus.Completed),
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
    public int ProcessingCount { get; init; }
    public int RetryScheduledCount { get; init; }
    public int CompletedCount { get; init; }
    public int DeadLetteredCount { get; init; }
    public int TotalCount { get; init; }
}
