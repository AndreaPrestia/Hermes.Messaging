using Hermes.Messaging.Domain.Entities;
using Hermes.Messaging.Infrastructure;
using LiteDB;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// HERMES 0.3.0-alpha — fail-fast when a store was written by a NEWER schema than this build
/// supports (ADR: never silently orphan durable messages).
/// </summary>
[Collection("PersistentMessageStore")]
public class StoreSchemaValidationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public StoreSchemaValidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"schemaval_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "s.db");
    }

    [Fact]
    public void OpeningStore_WithNewerSchemaRecord_FailsFast()
    {
        // Write a record stamped with a schema version from the "future".
        var collection = $"messages_{typeof(TestMessage).Name}";
        using (var db = new LiteDatabase(_dbPath))
        {
            var col = db.GetCollection<PersistedMessage<TestMessage>>(collection);
            col.Insert(new PersistedMessage<TestMessage>
            {
                Id = Guid.CreateVersion7(DateTimeOffset.UtcNow),
                MessageId = Guid.CreateVersion7(DateTimeOffset.UtcNow),
                CorrelationId = Guid.CreateVersion7(DateTimeOffset.UtcNow),
                Path = "r",
                Body = new TestMessage("x"),
                Status = MessageStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                SchemaVersion = PersistedMessageSchema.CurrentVersion + 1
            });
        }

        var ex = Assert.Throws<StoreSchemaMismatchException>(() => new PersistentMessageStore<TestMessage>(_dbPath));
        Assert.Equal(PersistedMessageSchema.CurrentVersion + 1, ex.FoundVersion);
        Assert.Equal(PersistedMessageSchema.CurrentVersion, ex.SupportedVersion);
    }

    [Fact]
    public void OpeningStore_WithCurrentSchema_Succeeds()
    {
        var id = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        using (var store = new PersistentMessageStore<TestMessage>(_dbPath))
        {
            store.Insert(new ChannelMessage<TestMessage>("r", new TestMessage("x"), Guid.CreateVersion7(DateTimeOffset.UtcNow), id));
        }

        // Re-open: current-schema records must not trip the guard.
        using var reopened = new PersistentMessageStore<TestMessage>(_dbPath);
        Assert.NotNull(reopened.GetByMessageId(id));
    }

    [Fact]
    public void OpeningEmptyStore_Succeeds()
    {
        using var store = new PersistentMessageStore<TestMessage>(_dbPath);
        Assert.Empty(store.GetPendingMessages());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private sealed record TestMessage(string Value);
}
