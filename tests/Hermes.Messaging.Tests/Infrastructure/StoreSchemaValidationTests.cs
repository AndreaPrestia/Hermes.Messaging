using Hermes.Messaging;
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

    [Fact]
    public void OpeningStore_WithNewerSchema_DoesNotMutateIndexesBeforeFailing()
    {
        // Seed a "future" schema record WITHOUT any of the Hermes indexes present, so we can
        // observe whether the failing open mutated store structure before refusing.
        var collectionName = $"messages_{typeof(TestMessage).Name}";
        using (var db = new LiteDatabase(_dbPath))
        {
            var col = db.GetCollection<PersistedMessage<TestMessage>>(collectionName);
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

        var before = ReadHermesIndexNames(collectionName);

        Assert.Throws<StoreSchemaMismatchException>(() => new PersistentMessageStore<TestMessage>(_dbPath));

        // No Hermes indexes should have been added by the failed open: validation runs first.
        var after = ReadHermesIndexNames(collectionName);
        Assert.Empty(before);
        Assert.Empty(after);
    }

    [Fact]
    public void OpeningCurrentStore_WritesSchemaMetadataDocument()
    {
        using (var store = new PersistentMessageStore<TestMessage>(_dbPath))
        {
            store.Insert(new ChannelMessage<TestMessage>("r", new TestMessage("x"),
                Guid.CreateVersion7(DateTimeOffset.UtcNow), Guid.CreateVersion7(DateTimeOffset.UtcNow)));
        }

        using var db = new LiteDatabase(_dbPath);
        var meta = db.GetCollection("hermes_meta");
        var doc = meta.FindById("schema");
        Assert.NotNull(doc);
        Assert.Equal(PersistedMessageSchema.CurrentVersion, doc["SchemaVersion"].AsInt32);
    }

    [Fact]
    public void OpeningStore_WithNewerSchemaMetadataDocument_FailsFast()
    {
        // A store whose metadata document already advertises a future schema must fail fast even
        // if the (empty) message collection would otherwise look compatible.
        using (var db = new LiteDatabase(_dbPath))
        {
            var meta = db.GetCollection("hermes_meta");
            var doc = new BsonDocument
            {
                ["_id"] = "schema",
                ["SchemaVersion"] = PersistedMessageSchema.CurrentVersion + 1
            };
            meta.Upsert(doc);
        }

        var ex = Assert.Throws<StoreSchemaMismatchException>(() => new PersistentMessageStore<TestMessage>(_dbPath));
        Assert.Equal(PersistedMessageSchema.CurrentVersion + 1, ex.FoundVersion);
    }

    [Fact]
    public void OpeningLegacyStore_WithoutMetadataDocument_ValidatesViaRecordsThenUpgrades()
    {
        // Simulate a store written before the metadata document existed: current-schema records,
        // but no hermes_meta collection. It must open (records are compatible) and then persist
        // the metadata document so future opens are O(1).
        var collectionName = $"messages_{typeof(TestMessage).Name}";
        using (var db = new LiteDatabase(_dbPath))
        {
            var col = db.GetCollection<PersistedMessage<TestMessage>>(collectionName);
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
                SchemaVersion = PersistedMessageSchema.CurrentVersion
            });
            Assert.False(db.CollectionExists("hermes_meta"));
        }

        using (var store = new PersistentMessageStore<TestMessage>(_dbPath))
        {
            Assert.Single(store.GetPendingMessages());
        }

        using var reopened = new LiteDatabase(_dbPath);
        var meta = reopened.GetCollection("hermes_meta");
        var metaDoc = meta.FindById("schema");
        Assert.NotNull(metaDoc);
        Assert.Equal(PersistedMessageSchema.CurrentVersion, metaDoc["SchemaVersion"].AsInt32);
    }

    private List<string> ReadHermesIndexNames(string collectionName)
    {
        using var db = new LiteDatabase(_dbPath);
        // LiteDB always maintains the implicit _id index; count only the Hermes-added ones.
        return db.GetCollection("$indexes")
            .Query()
            .Where(x => x["collection"] == collectionName)
            .ToList()
            .Select(x => x["name"].AsString)
            .Where(name => name != "_id")
            .ToList();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private sealed record TestMessage(string Value);
}
