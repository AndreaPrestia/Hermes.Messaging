using Hermes.Messaging;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// Test helper: an <see cref="IMessageStore{T}"/> that forwards every call to an inner store.
/// Override individual members to observe or intercept behavior.
/// </summary>
internal abstract class DelegatingStore<T>(IMessageStore<T> inner) : IMessageStore<T>
{
    protected IMessageStore<T> Inner { get; } = inner;

    public virtual void Insert(ChannelMessage<T> message) => Inner.Insert(message);
    public virtual PersistedMessage<T>? GetByMessageId(Guid messageId) => Inner.GetByMessageId(messageId);
    public virtual void UpdateStatus(Guid messageId, MessageStatus status, string? error = null) => Inner.UpdateStatus(messageId, status, error);
    public virtual void IncrementAttempt(Guid messageId) => Inner.IncrementAttempt(messageId);
    public virtual void DecrementAttempt(Guid messageId) => Inner.DecrementAttempt(messageId);
    public virtual IEnumerable<PersistedMessage<T>> GetPendingMessages() => Inner.GetPendingMessages();
    public virtual PersistedMessage<T>? TryClaim(Guid messageId) => Inner.TryClaim(messageId);
    public virtual void MarkCompleted(Guid messageId) => Inner.MarkCompleted(messageId);
    public virtual void ScheduleRetry(Guid messageId, DateTimeOffset nextAttemptAt, string? error) => Inner.ScheduleRetry(messageId, nextAttemptAt, error);
    public virtual void MarkDeadLettered(Guid messageId, string? error) => Inner.MarkDeadLettered(messageId, error);
    public virtual int RecoverInterrupted() => Inner.RecoverInterrupted();
    public virtual IEnumerable<PersistedMessage<T>> GetDueMessages(DateTimeOffset now) => Inner.GetDueMessages(now);
    public virtual IReadOnlyList<Guid> GetDueMessageIds(DateTimeOffset now, int limit) => Inner.GetDueMessageIds(now, limit);
    public virtual bool ReplayDeadLetter(Guid messageId) => Inner.ReplayDeadLetter(messageId);
    public virtual IReadOnlyList<PersistedMessage<T>> ListDeadLetters(int skip = 0, int take = 100) => Inner.ListDeadLetters(skip, take);
    public virtual bool DeleteDeadLetter(Guid messageId) => Inner.DeleteDeadLetter(messageId);
    public virtual int PurgeDeadLetters() => Inner.PurgeDeadLetters();
    public virtual MessageStoreStats GetStats() => Inner.GetStats();
    public virtual int CleanupOldMessages() => Inner.CleanupOldMessages();
}
