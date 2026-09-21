using Hermes.Messaging;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// HERMES-003 — durable dead-letter administration (List/Get/Replay/Delete/Purge) and durability.
/// </summary>
[Collection("PersistentMessageStore")]
public class DeadLetterAdministrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PersistentMessageStore<TestMessage> _store;
    private readonly DeadLetterAdministration<TestMessage> _admin;

    public DeadLetterAdministrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dla_{Guid.NewGuid()}.db");
        _store = new PersistentMessageStore<TestMessage>(_dbPath, TimeSpan.FromSeconds(1));
        _admin = new DeadLetterAdministration<TestMessage>(_store);
    }

    private Guid DeadLetter(string route = "route", string error = "boom")
    {
        var id = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Insert(new ChannelMessage<TestMessage>(route, new TestMessage("data"), Guid.CreateVersion7(DateTimeOffset.UtcNow), id));
        _store.TryClaim(id);
        _store.MarkDeadLettered(id, error);
        return id;
    }

    [Fact]
    public void List_ReturnsOnlyDeadLettered()
    {
        var dead = DeadLetter();

        var pending = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Insert(new ChannelMessage<TestMessage>("p", new TestMessage("x"), Guid.CreateVersion7(DateTimeOffset.UtcNow), pending));

        var list = _admin.List();

        Assert.Single(list);
        Assert.Equal(dead, list[0].MessageId);
        Assert.Equal("boom", list[0].LastError);
    }

    [Fact]
    public void List_Paging_Works()
    {
        for (var i = 0; i < 5; i++) DeadLetter($"route{i}");

        var page1 = _admin.List(skip: 0, take: 2);
        var page2 = _admin.List(skip: 2, take: 2);
        var page3 = _admin.List(skip: 4, take: 2);

        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.Single(page3);
        // No overlap.
        var ids = page1.Concat(page2).Concat(page3).Select(x => x.MessageId).ToHashSet();
        Assert.Equal(5, ids.Count);
    }

    [Fact]
    public void Get_ReturnsDeadLetter_NullForNonDeadLetter()
    {
        var dead = DeadLetter();
        Assert.NotNull(_admin.Get(dead));

        var pending = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Insert(new ChannelMessage<TestMessage>("p", new TestMessage("x"), Guid.CreateVersion7(DateTimeOffset.UtcNow), pending));
        Assert.Null(_admin.Get(pending));
        Assert.Null(_admin.Get(Guid.CreateVersion7(DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void Replay_MovesToPending_AndRemovesFromDlqList()
    {
        var dead = DeadLetter();

        Assert.True(_admin.Replay(dead));

        Assert.Empty(_admin.List());
        Assert.Equal(MessageStatus.Pending, _store.GetByMessageId(dead)!.Status);
        Assert.Null(_admin.Get(dead));
    }

    [Fact]
    public void Delete_RemovesSingleDeadLetter()
    {
        var a = DeadLetter("a");
        var b = DeadLetter("b");

        Assert.True(_admin.Delete(a));

        Assert.Null(_store.GetByMessageId(a));
        Assert.NotNull(_store.GetByMessageId(b));
        Assert.False(_admin.Delete(a)); // already gone
    }

    [Fact]
    public void Delete_NonDeadLettered_ReturnsFalse()
    {
        var pending = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Insert(new ChannelMessage<TestMessage>("p", new TestMessage("x"), Guid.CreateVersion7(DateTimeOffset.UtcNow), pending));

        Assert.False(_admin.Delete(pending));
        Assert.NotNull(_store.GetByMessageId(pending)); // untouched
    }

    [Fact]
    public void Purge_RemovesAllDeadLetters_Only()
    {
        DeadLetter("a");
        DeadLetter("b");
        var pending = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        _store.Insert(new ChannelMessage<TestMessage>("p", new TestMessage("x"), Guid.CreateVersion7(DateTimeOffset.UtcNow), pending));

        var purged = _admin.Purge();

        Assert.Equal(2, purged);
        Assert.Empty(_admin.List());
        Assert.NotNull(_store.GetByMessageId(pending)); // non-dead-letter retained
    }

    [Fact]
    public void DeadLetter_SurvivesRestart()
    {
        var dead = DeadLetter();
        _store.Dispose();

        using var reopened = new PersistentMessageStore<TestMessage>(_dbPath, TimeSpan.FromSeconds(1));
        var admin = new DeadLetterAdministration<TestMessage>(reopened);

        var list = admin.List();
        Assert.Single(list);
        Assert.Equal(dead, list[0].MessageId);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
    }

    private sealed record TestMessage(string Value);
}
