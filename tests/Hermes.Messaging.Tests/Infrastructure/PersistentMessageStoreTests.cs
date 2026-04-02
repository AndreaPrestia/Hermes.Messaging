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
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test-path", new TestMessage("data"), correlationId);

        _store.Persist(message);

        var persisted = _store.GetByCorrelationId(correlationId);
        Assert.NotNull(persisted);
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
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test-path", new TestMessage("data"), correlationId);
        _store.Persist(message);

        _store.UpdateStatus(correlationId, MessageStatus.Completed);

        var persisted = _store.GetByCorrelationId(correlationId);
        Assert.NotNull(persisted);
        Assert.Equal(MessageStatus.Completed, persisted.Status);
        Assert.Null(persisted.LastError);
    }

    [Fact]
    public void UpdateStatus_WithError_StoresErrorMessage()
    {
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test-path", new TestMessage("data"), correlationId);
        _store.Persist(message);

        _store.UpdateStatus(correlationId, MessageStatus.Failed, "Connection timeout");

        var persisted = _store.GetByCorrelationId(correlationId);
        Assert.NotNull(persisted);
        Assert.Equal(MessageStatus.Failed, persisted.Status);
        Assert.Equal("Connection timeout", persisted.LastError);
    }

    [Fact]
    public void IncrementAttempt_IncreasesAttemptCount()
    {
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test-path", new TestMessage("data"), correlationId);
        _store.Persist(message);

        _store.IncrementAttempt(correlationId);
        _store.IncrementAttempt(correlationId);

        var persisted = _store.GetByCorrelationId(correlationId);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted.AttemptCount);
    }

    [Fact]
    public void GetPendingMessages_ReturnsPendingOnly()
    {
        var pending1 = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var pending2 = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var completed = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        _store.Persist(new ChannelMessage<TestMessage>("path1", new TestMessage("data1"), pending1));
        _store.Persist(new ChannelMessage<TestMessage>("path2", new TestMessage("data2"), pending2));
        _store.Persist(new ChannelMessage<TestMessage>("path3", new TestMessage("data3"), completed));
        _store.UpdateStatus(completed, MessageStatus.Completed);

        var pending = _store.GetPendingMessages().ToList();

        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, p => p.CorrelationId == pending1);
        Assert.Contains(pending, p => p.CorrelationId == pending2);
        Assert.DoesNotContain(pending, p => p.CorrelationId == completed);
    }

    [Fact]
    public void GetPendingMessages_OrdersByCreatedAt()
    {
        var first = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Persist(new ChannelMessage<TestMessage>("path1", new TestMessage("data1"), first));
        Thread.Sleep(100);
        var second = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Persist(new ChannelMessage<TestMessage>("path2", new TestMessage("data2"), second));
        Thread.Sleep(100);
        var third = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Persist(new ChannelMessage<TestMessage>("path3", new TestMessage("data3"), third));

        var pending = _store.GetPendingMessages().ToList();

        Assert.Equal(3, pending.Count);
        Assert.Equal(first, pending[0].CorrelationId);
        Assert.Equal(second, pending[1].CorrelationId);
        Assert.Equal(third, pending[2].CorrelationId);
    }

    [Fact]
    public void CleanupOldMessages_RemovesOldCompletedAndDeadLettered()
    {
        var recent = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var oldCompleted = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var oldDeadLettered = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var oldPending = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        _store.Persist(new ChannelMessage<TestMessage>("path1", new TestMessage("data1"), recent));
        _store.UpdateStatus(recent, MessageStatus.Completed);

        _store.Persist(new ChannelMessage<TestMessage>("path2", new TestMessage("data2"), oldCompleted));
        _store.UpdateStatus(oldCompleted, MessageStatus.Completed);

        _store.Persist(new ChannelMessage<TestMessage>("path3", new TestMessage("data3"), oldDeadLettered));
        _store.UpdateStatus(oldDeadLettered, MessageStatus.DeadLettered);

        _store.Persist(new ChannelMessage<TestMessage>("path4", new TestMessage("data4"), oldPending));

        Thread.Sleep(1100);

        var deletedCount = _store.CleanupOldMessages();

        Assert.Equal(3, deletedCount);
        Assert.Null(_store.GetByCorrelationId(recent));
        Assert.Null(_store.GetByCorrelationId(oldCompleted));
        Assert.Null(_store.GetByCorrelationId(oldDeadLettered));
        Assert.NotNull(_store.GetByCorrelationId(oldPending));
    }

    [Fact]
    public void GetStats_ReturnsCorrectCounts()
    {
        var pending1 = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var pending2 = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var completed = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var failed = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var deadLettered = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        _store.Persist(new ChannelMessage<TestMessage>("path1", new TestMessage("data1"), pending1));
        _store.Persist(new ChannelMessage<TestMessage>("path2", new TestMessage("data2"), pending2));
        _store.Persist(new ChannelMessage<TestMessage>("path3", new TestMessage("data3"), completed));
        _store.UpdateStatus(completed, MessageStatus.Completed);
        _store.Persist(new ChannelMessage<TestMessage>("path4", new TestMessage("data4"), failed));
        _store.UpdateStatus(failed, MessageStatus.Failed);
        _store.Persist(new ChannelMessage<TestMessage>("path5", new TestMessage("data5"), deadLettered));
        _store.UpdateStatus(deadLettered, MessageStatus.DeadLettered);

        var stats = _store.GetStats();

        Assert.Equal(2, stats.PendingCount);
        Assert.Equal(1, stats.CompletedCount);
        Assert.Equal(1, stats.FailedCount);
        Assert.Equal(1, stats.DeadLetteredCount);
        Assert.Equal(5, stats.TotalCount);
    }

    [Fact]
    public void GetByCorrelationId_NonExistent_ReturnsNull()
    {
        var nonExistent = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        var result = _store.GetByCorrelationId(nonExistent);

        Assert.Null(result);
    }

    [Fact]
    public void Persist_MultipleMessages_WithUniqueCorrelationIds()
    {
        var id1 = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var id2 = Guid.CreateVersion7(DateTimeOffset.UtcNow);

        _store.Persist(new ChannelMessage<TestMessage>("path1", new TestMessage("data1"), id1));
        _store.Persist(new ChannelMessage<TestMessage>("path2", new TestMessage("data2"), id2));

        Assert.NotNull(_store.GetByCorrelationId(id1));
        Assert.NotNull(_store.GetByCorrelationId(id2));
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
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test-path", new TestMessage("data"), correlationId);

        _store.Persist(message);
        var retrieved = _store.GetByCorrelationId(correlationId);

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
