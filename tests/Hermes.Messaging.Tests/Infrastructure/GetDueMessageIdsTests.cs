using Hermes.Messaging;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// HERMES-007 P5 — bounded due-message ID query at the store level.
/// </summary>
[Collection("PersistentMessageStore")]
public class GetDueMessageIdsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PersistentMessageStore<TestMessage> _store;

    public GetDueMessageIdsTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dueids_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "s.db");
        _store = new PersistentMessageStore<TestMessage>(_dbPath, TimeSpan.FromSeconds(1));
    }

    private Guid InsertPending(string route = "r")
    {
        var id = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Insert(new ChannelMessage<TestMessage>(route, new TestMessage("x"), Guid.CreateVersion7(DateTimeOffset.UtcNow), id));
        return id;
    }

    [Fact]
    public void GetDueMessageIds_RespectsLimit()
    {
        for (var i = 0; i < 10; i++) InsertPending();

        var ids = _store.GetDueMessageIds(DateTimeOffset.UtcNow, limit: 3);

        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void GetDueMessageIds_ReturnsPendingAndDueRetriesOnly()
    {
        var pending = InsertPending("p");

        var dueRetry = InsertPending("dr");
        _store.TryClaim(dueRetry);
        _store.ScheduleRetry(dueRetry, DateTimeOffset.UtcNow.AddMinutes(-1), "boom");

        var futureRetry = InsertPending("fr");
        _store.TryClaim(futureRetry);
        _store.ScheduleRetry(futureRetry, DateTimeOffset.UtcNow.AddMinutes(10), "boom");

        var processing = InsertPending("pr");
        _store.TryClaim(processing); // stays Processing

        var completed = InsertPending("c");
        _store.TryClaim(completed);
        _store.MarkCompleted(completed);

        var dead = InsertPending("d");
        _store.TryClaim(dead);
        _store.MarkDeadLettered(dead, "boom");

        var ids = _store.GetDueMessageIds(DateTimeOffset.UtcNow, limit: 100).ToHashSet();

        Assert.Contains(pending, ids);
        Assert.Contains(dueRetry, ids);
        Assert.DoesNotContain(futureRetry, ids);
        Assert.DoesNotContain(processing, ids);
        Assert.DoesNotContain(completed, ids);
        Assert.DoesNotContain(dead, ids);
    }

    [Fact]
    public void GetDueMessageIds_OrdersOldestFirst()
    {
        var first = InsertPending("1");
        Thread.Sleep(50);
        var second = InsertPending("2");
        Thread.Sleep(50);
        var third = InsertPending("3");

        var ids = _store.GetDueMessageIds(DateTimeOffset.UtcNow, limit: 100);

        Assert.Equal(new[] { first, second, third }, ids.ToArray());
    }

    [Fact]
    public void GetDueMessageIds_RespectsLimit_TakesOldest()
    {
        var first = InsertPending("1");
        Thread.Sleep(50);
        var second = InsertPending("2");
        Thread.Sleep(50);
        InsertPending("3");

        var ids = _store.GetDueMessageIds(DateTimeOffset.UtcNow, limit: 2);

        Assert.Equal(new[] { first, second }, ids.ToArray());
    }

    [Fact]
    public void GetDueMessageIds_LimitZero_DoesNotScanAll()
    {
        for (var i = 0; i < 5; i++) InsertPending();

        // Limit <= 0 is an explicit argument error — it never becomes an unbounded query.
        Assert.Throws<ArgumentOutOfRangeException>(() => _store.GetDueMessageIds(DateTimeOffset.UtcNow, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _store.GetDueMessageIds(DateTimeOffset.UtcNow, -1));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { /* best-effort */ }
    }

    private sealed record TestMessage(string Value);
}
