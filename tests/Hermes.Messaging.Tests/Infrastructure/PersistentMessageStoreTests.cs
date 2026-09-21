using Hermes.Messaging.Domain.Entities;
using Hermes.Messaging.Infrastructure;

namespace Hermes.Messaging.Tests.Infrastructure;

[Collection("PersistentMessageStore")]
public class PersistentMessageStoreTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly PersistentMessageStore<TestMessage> _store;

    public PersistentMessageStoreTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _store = new PersistentMessageStore<TestMessage>(_testDbPath, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Persist_CreatesNewMessage_WithPendingStatus()
    {
        var messageId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test-path", new TestMessage("data"), correlationId, messageId);

        _store.Persist(message);

        var persisted = _store.GetByMessageId(messageId);
        Assert.NotNull(persisted);
        Assert.Equal(messageId, persisted.MessageId);
        Assert.Equal(correlationId, persisted.CorrelationId);
        Assert.Equal("test-path", persisted.Path);
        Assert.Equal("data", persisted.Body.Value);
        Assert.Equal(MessageStatus.Pending, persisted.Status);
        Assert.Equal(0, persisted.AttemptCount);
        Assert.Null(persisted.LastError);
    }

    [Fact]
    public void UpdateStatus_ChangesMessageStatus()
    {
        var messageId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test-path", new TestMessage("data"), Guid.CreateVersion7(DateTimeOffset.UtcNow), messageId);
        _store.Persist(message);

        _store.UpdateStatus(messageId, MessageStatus.Completed);

        var persisted = _store.GetByMessageId(messageId);
        Assert.NotNull(persisted);
        Assert.Equal(MessageStatus.Completed, persisted.Status);
        Assert.Null(persisted.LastError);
    }

    [Fact]
    public void UpdateStatus_WithError_StoresErrorMessage()
    {
        var messageId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test-path", new TestMessage("data"), Guid.CreateVersion7(DateTimeOffset.UtcNow), messageId);
        _store.Persist(message);

        _store.UpdateStatus(messageId, MessageStatus.DeadLettered, "Connection timeout");

        var persisted = _store.GetByMessageId(messageId);
        Assert.NotNull(persisted);
        Assert.Equal(MessageStatus.DeadLettered, persisted.Status);
        Assert.Equal("Connection timeout", persisted.LastError);
    }

    [Fact]
    public void IncrementAttempt_IncreasesAttemptCount()
    {
        var messageId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test-path", new TestMessage("data"), Guid.CreateVersion7(DateTimeOffset.UtcNow), messageId);
        _store.Persist(message);

        _store.IncrementAttempt(messageId);
        _store.IncrementAttempt(messageId);

        var persisted = _store.GetByMessageId(messageId);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted.AttemptCount);
    }

    [Fact]
    public void GetPendingMessages_ReturnsPendingOnly()
    {
        var pending1 = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var pending2 = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var completed = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        _store.Persist(new ChannelMessage<TestMessage>("path1", new TestMessage("data1"), Guid.CreateVersion7(DateTimeOffset.UtcNow), pending1));
        _store.Persist(new ChannelMessage<TestMessage>("path2", new TestMessage("data2"), Guid.CreateVersion7(DateTimeOffset.UtcNow), pending2));
        _store.Persist(new ChannelMessage<TestMessage>("path3", new TestMessage("data3"), Guid.CreateVersion7(DateTimeOffset.UtcNow), completed));
        _store.UpdateStatus(completed, MessageStatus.Completed);

        var pending = _store.GetPendingMessages().ToList();

        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, p => p.MessageId == pending1);
        Assert.Contains(pending, p => p.MessageId == pending2);
        Assert.DoesNotContain(pending, p => p.MessageId == completed);
    }

    [Fact]
    public void GetPendingMessages_OrdersByCreatedAt()
    {
        var first = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Persist(new ChannelMessage<TestMessage>("path1", new TestMessage("data1"), Guid.CreateVersion7(DateTimeOffset.UtcNow), first));
        Thread.Sleep(100);
        var second = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Persist(new ChannelMessage<TestMessage>("path2", new TestMessage("data2"), Guid.CreateVersion7(DateTimeOffset.UtcNow), second));
        Thread.Sleep(100);
        var third = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Persist(new ChannelMessage<TestMessage>("path3", new TestMessage("data3"), Guid.CreateVersion7(DateTimeOffset.UtcNow), third));

        var pending = _store.GetPendingMessages().ToList();

        Assert.Equal(3, pending.Count);
        Assert.Equal(first, pending[0].MessageId);
        Assert.Equal(second, pending[1].MessageId);
        Assert.Equal(third, pending[2].MessageId);
    }

    [Fact]
    public void CleanupOldMessages_RemovesOldCompleted_ButRetainsDeadLettered()
    {
        // HERMES-003: the durable DLQ is retained until explicit Delete/Purge. Cleanup only
        // removes old Completed messages; DeadLettered records must survive retention cleanup.
        var recent = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var oldCompleted = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var oldDeadLettered = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var oldPending = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        _store.Persist(new ChannelMessage<TestMessage>("path1", new TestMessage("data1"), Guid.CreateVersion7(DateTimeOffset.UtcNow), recent));
        _store.UpdateStatus(recent, MessageStatus.Completed);

        _store.Persist(new ChannelMessage<TestMessage>("path2", new TestMessage("data2"), Guid.CreateVersion7(DateTimeOffset.UtcNow), oldCompleted));
        _store.UpdateStatus(oldCompleted, MessageStatus.Completed);

        _store.Persist(new ChannelMessage<TestMessage>("path3", new TestMessage("data3"), Guid.CreateVersion7(DateTimeOffset.UtcNow), oldDeadLettered));
        _store.UpdateStatus(oldDeadLettered, MessageStatus.DeadLettered);

        _store.Persist(new ChannelMessage<TestMessage>("path4", new TestMessage("data4"), Guid.CreateVersion7(DateTimeOffset.UtcNow), oldPending));

        Thread.Sleep(1100);

        var deletedCount = _store.CleanupOldMessages();

        Assert.Equal(2, deletedCount); // only the two Completed
        Assert.Null(_store.GetByMessageId(recent));
        Assert.Null(_store.GetByMessageId(oldCompleted));
        Assert.NotNull(_store.GetByMessageId(oldDeadLettered)); // dead letter retained
        Assert.NotNull(_store.GetByMessageId(oldPending));
    }

    [Fact]
    public void GetStats_ReturnsCorrectCounts()
    {
        var pending1 = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var pending2 = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var completed = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var retryScheduled = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var deadLettered = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        _store.Persist(new ChannelMessage<TestMessage>("path1", new TestMessage("data1"), Guid.CreateVersion7(DateTimeOffset.UtcNow), pending1));
        _store.Persist(new ChannelMessage<TestMessage>("path2", new TestMessage("data2"), Guid.CreateVersion7(DateTimeOffset.UtcNow), pending2));
        _store.Persist(new ChannelMessage<TestMessage>("path3", new TestMessage("data3"), Guid.CreateVersion7(DateTimeOffset.UtcNow), completed));
        _store.UpdateStatus(completed, MessageStatus.Completed);
        _store.Persist(new ChannelMessage<TestMessage>("path4", new TestMessage("data4"), Guid.CreateVersion7(DateTimeOffset.UtcNow), retryScheduled));
        _store.ScheduleRetry(retryScheduled, DateTimeOffset.UtcNow.AddMinutes(1), "boom");
        _store.Persist(new ChannelMessage<TestMessage>("path5", new TestMessage("data5"), Guid.CreateVersion7(DateTimeOffset.UtcNow), deadLettered));
        _store.UpdateStatus(deadLettered, MessageStatus.DeadLettered);

        var stats = _store.GetStats();

        Assert.Equal(2, stats.PendingCount);
        Assert.Equal(1, stats.CompletedCount);
        Assert.Equal(1, stats.RetryScheduledCount);
        Assert.Equal(1, stats.DeadLetteredCount);
        Assert.Equal(5, stats.TotalCount);
    }

    [Fact]
    public void GetByMessageId_NonExistent_ReturnsNull()
    {
        var nonExistent = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        var result = _store.GetByMessageId(nonExistent);

        Assert.Null(result);
    }

    [Fact]
    public void Persist_MultipleMessages_WithUniqueMessageIds()
    {
        var id1 = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var id2 = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        _store.Persist(new ChannelMessage<TestMessage>("path1", new TestMessage("data1"), Guid.CreateVersion7(DateTimeOffset.UtcNow), id1));
        _store.Persist(new ChannelMessage<TestMessage>("path2", new TestMessage("data2"), Guid.CreateVersion7(DateTimeOffset.UtcNow), id2));

        Assert.NotNull(_store.GetByMessageId(id1));
        Assert.NotNull(_store.GetByMessageId(id2));
    }

    [Fact]
    public void UpdateStatus_NonExistentMessage_DoesNotThrow()
    {
        var nonExistent = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        var exception = Record.Exception(() => _store.UpdateStatus(nonExistent, MessageStatus.Completed));

        Assert.Null(exception);
    }

    [Fact]
    public void IncrementAttempt_NonExistentMessage_DoesNotThrow()
    {
        var nonExistent = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        var exception = Record.Exception(() => _store.IncrementAttempt(nonExistent));

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_CreatesDatabase_WithCollectionAndIndexes()
    {
        var messageId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test-path", new TestMessage("data"), Guid.CreateVersion7(DateTimeOffset.UtcNow), messageId);

        _store.Persist(message);
        var retrieved = _store.GetByMessageId(messageId);

        Assert.NotNull(retrieved);
    }

    public void Dispose()
    {
        _store?.Dispose();
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }

    private sealed record TestMessage(string Value);
}
