using Hermes.Messaging.Domain.Entities;
using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// HERMES-006 hardening: readiness gating, store abstraction consistency, DLQ replay budget,
/// MaxAttempts semantics, type-identity keys, and the post-commit cancellation race.
/// </summary>
[Collection("MessageBus")]
public class Hermes006HardeningTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"h006_{Guid.NewGuid():N}");

    public Hermes006HardeningTests()
    {
        Directory.CreateDirectory(_tempPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort */ }
    }

    // ---------- P1: readiness gates publishability ----------

    [Fact]
    public async Task Publish_WhileStartupRecoveryIncomplete_IsRejected()
    {
        // Seed readiness with an extra expected subscriber that never recovers. The runtime can
        // reach Ready per lifecycle, but RecoveryComplete stays false, so publish must be rejected
        // and diagnostics must report not-ready — deterministically, without timing sleeps.
        var readiness = new HermesReadiness();
        readiness.Expect("never-recovers::pending");

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddChannelSubscription<TestMessage>("h006/route", (_, _, _) => Task.CompletedTask);
                services.RemoveAll<HermesReadiness>();
                services.AddSingleton(readiness);
            })
            .Build();

        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var diag = host.Services.GetRequiredService<IMessageBusDiagnostics>();

            Assert.False(readiness.RecoveryComplete);
            Assert.False(diag.IsReady);
            await Assert.ThrowsAsync<HermesNotReadyException>(async () =>
                await bus.PublishAsync("h006/route", new TestMessage("x")));

            // Once the outstanding recovery completes, publishability and diagnostics flip together.
            readiness.MarkRecovered("never-recovers::pending");
            Assert.True(diag.IsReady);
            var result = await bus.PublishAsync("h006/route", new TestMessage("x"));
            Assert.NotEqual(Guid.Empty, result.MessageId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Publish_AfterAllStartupRecoveryCompletes_IsAccepted()
    {
        using var host = BuildSimpleHost();
        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var readiness = host.Services.GetRequiredService<HermesReadiness>();
            Assert.True(readiness.RecoveryComplete);

            var result = await bus.PublishAsync("h006/route", new TestMessage("x"));
            Assert.NotEqual(Guid.Empty, result.MessageId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Diagnostics_IsReady_MatchesPublishability()
    {
        using var host = BuildSimpleHost();
        var diag = host.Services.GetRequiredService<IMessageBusDiagnostics>();
        var bus = host.Services.GetRequiredService<IMessageBus>();

        Assert.False(diag.IsReady);
        await Assert.ThrowsAsync<HermesNotReadyException>(async () => await bus.PublishAsync("h006/route", new TestMessage("x")));

        await host.StartAsync();
        try
        {
            Assert.True(diag.IsReady);
            var r = await bus.PublishAsync("h006/route", new TestMessage("x"));
            Assert.NotEqual(Guid.Empty, r.MessageId);
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.False(diag.IsReady);
    }

    // ---------- P3: no publisher/subscriber store split-brain ----------

    [Fact]
    public async Task CustomStoreReplacement_IsUsedByWholeRuntime()
    {
        var backing = new PersistentMessageStore<TestMessage>(Path.Combine(_tempPath, "custom.db"));
        var spy = new CountingStore<TestMessage>(backing);
        var handled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddChannelSubscription<TestMessage>("h006/route", (_, _, _) => { handled.TrySetResult(true); return Task.CompletedTask; });
                services.RemoveAll<IMessageStore<TestMessage>>();
                services.AddSingleton<IMessageStore<TestMessage>>(spy);
                services.AddSingleton<PersistentMessageStore<TestMessage>>(backing);
            })
            .Build();

        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("h006/route", new TestMessage("x"));
            await handled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200); // allow MarkCompleted

            // Both publisher (Insert) and subscriber (TryClaim/MarkCompleted) used the same custom store.
            Assert.True(spy.Inserts >= 1, "publisher did not use the custom store");
            Assert.True(spy.Claims >= 1, "subscriber did not use the custom store");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------- P4: DLQ replay resets retry budget ----------

    [Fact]
    public void ReplayDeadLetter_ResetsAttemptCount_ClearsLastError()
    {
        using var store = new PersistentMessageStore<TestMessage>(Path.Combine(_tempPath, "replay.db"));
        Directory.CreateDirectory(_tempPath);
        var id = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        store.Insert(new ChannelMessage<TestMessage>("r", new TestMessage("x"), Guid.CreateVersion7(DateTimeOffset.UtcNow), id));
        store.TryClaim(id); // attempt -> 1
        store.TryClaim(id); // not claimable (Processing); no-op
        store.MarkDeadLettered(id, "boom");

        Assert.True(store.ReplayDeadLetter(id));

        var m = store.GetByMessageId(id);
        Assert.NotNull(m);
        Assert.Equal(MessageStatus.Pending, m.Status);
        Assert.Equal(0, m.AttemptCount);
        Assert.Null(m.LastError);
        Assert.Null(m.NextAttemptAt);
    }

    // ---------- P5: MaxAttempts semantics ----------

    [Fact]
    public async Task MaxAttempts_1_DeadLettersAfterFirstFailure()
    {
        var attempts = 0;
        using var host = BuildHostWithHandler((_, _, _) => { Interlocked.Increment(ref attempts); throw new InvalidOperationException("fail"); },
            opts => opts.MaxAttempts = 1);
        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();
            var r = await bus.PublishAsync("h006/route", new TestMessage("x"));

            await WaitForStatus(store, r.MessageId, MessageStatus.DeadLettered);
            Assert.Equal(1, Volatile.Read(ref attempts));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task MaxAttempts_3_AllowsExactlyThreeHandlerInvocations()
    {
        var attempts = 0;
        using var host = BuildHostWithHandler((_, _, _) => { Interlocked.Increment(ref attempts); throw new InvalidOperationException("fail"); },
            opts => { opts.MaxAttempts = 3; opts.InitialRetryDelayMs = 1; });
        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();
            var r = await bus.PublishAsync("h006/route", new TestMessage("x"));

            await WaitForStatus(store, r.MessageId, MessageStatus.DeadLettered, TimeSpan.FromSeconds(30));
            Assert.Equal(3, Volatile.Read(ref attempts));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void MaxRetryAttempts_ObsoleteAlias_MapsToMaxAttempts()
    {
#pragma warning disable CS0618
        var opts = new MessageBusOptions { MaxRetryAttempts = 7 };
        Assert.Equal(7, opts.MaxAttempts);
        opts.MaxAttempts = 2;
        Assert.Equal(2, opts.MaxRetryAttempts);
#pragma warning restore CS0618
    }

    // ---------- P6: stable type-identity keys ----------

    [Fact]
    public void Readiness_DoesNotCollideForSameShortTypeName()
    {
        var readiness = new HermesReadiness();
        readiness.Expect(TypeIdentity.Key<NamespaceA.Order>());
        readiness.Expect(TypeIdentity.Key<NamespaceB.Order>());

        Assert.False(readiness.RecoveryComplete);
        readiness.MarkRecovered(TypeIdentity.Key<NamespaceA.Order>());
        Assert.False(readiness.RecoveryComplete); // the other short-name twin is still pending

        readiness.MarkRecovered(TypeIdentity.Key<NamespaceB.Order>());
        Assert.True(readiness.RecoveryComplete);
    }

    [Fact]
    public void MetricsRegistry_DoesNotCollideForSameShortTypeName()
    {
        using var metrics = new HermesStoreMetrics();
        var a = new MessageStoreStats { PendingCount = 1 };
        var b = new MessageStoreStats { PendingCount = 2 };
        metrics.Register(TypeIdentity.Key<NamespaceA.Order>(), () => a);
        metrics.Register(TypeIdentity.Key<NamespaceB.Order>(), () => b);

        // Two distinct keys despite the same short name.
        Assert.NotEqual(TypeIdentity.Key<NamespaceA.Order>(), TypeIdentity.Key<NamespaceB.Order>());
    }

    // ---------- P7: post-commit cancellation race ----------

    [Fact]
    public async Task Publish_CancelAfterCommit_ReturnsAccepted_Deterministic()
    {
        using var cts = new CancellationTokenSource();
        var backing = new PersistentMessageStore<TestMessage>(Path.Combine(_tempPath, "race.db"));
        // Store hook: the instant Insert commits, cancel the token, THEN let publish continue.
        var racing = new PostInsertHookStore<TestMessage>(backing, onInserted: () => cts.Cancel());

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddChannelSubscription<TestMessage>("h006/route", (_, _, _) => Task.CompletedTask);
                services.RemoveAll<IMessageStore<TestMessage>>();
                services.AddSingleton<IMessageStore<TestMessage>>(racing);
                services.AddSingleton<PersistentMessageStore<TestMessage>>(backing);
            })
            .Build();

        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();

            // Commit happens, token cancels during Insert, publish path continues.
            var result = await bus.PublishAsync("h006/route", new TestMessage("x"), options: null, cts.Token);

            Assert.NotEqual(Guid.Empty, result.MessageId);
            Assert.NotNull(backing.GetByMessageId(result.MessageId)); // durable record exists
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------- helpers ----------

    private IHost BuildSimpleHost() => BuildHostWithHandler((_, _, _) => Task.CompletedTask, null);

    private IHost BuildHostWithHandler(
        Func<TestMessage, IServiceProvider, CancellationToken, Task> handler,
        Action<MessageBusOptions>? configure)
        => Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts =>
                {
                    opts.PersistenceBasePath = _tempPath;
                    configure?.Invoke(opts);
                });
                services.AddDeadLetterQueue<TestMessage>();
                services.AddChannelSubscription<TestMessage>("h006/route", handler);
            })
            .Build();

    private static async Task WaitForStatus(PersistentMessageStore<TestMessage> store, Guid id, MessageStatus status, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (store.GetByMessageId(id)?.Status == status) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Message {id} did not reach {status} in time (last: {store.GetByMessageId(id)?.Status}).");
    }

    private sealed record TestMessage(string Value);

    /// <summary>Store that counts inserts and claims to prove the whole runtime uses one instance.</summary>
    private sealed class CountingStore<T>(IMessageStore<T> inner) : DelegatingStore<T>(inner)
    {
        private int _inserts;
        private int _claims;
        public int Inserts => Volatile.Read(ref _inserts);
        public int Claims => Volatile.Read(ref _claims);

        public override void Insert(ChannelMessage<T> message) { Interlocked.Increment(ref _inserts); base.Insert(message); }
        public override PersistedMessage<T>? TryClaim(Guid id) { var r = base.TryClaim(id); if (r is not null) Interlocked.Increment(ref _claims); return r; }
    }

    /// <summary>Store that runs a hook immediately after a successful Insert commit.</summary>
    private sealed class PostInsertHookStore<T>(IMessageStore<T> inner, Action onInserted) : DelegatingStore<T>(inner)
    {
        public override void Insert(ChannelMessage<T> message)
        {
            base.Insert(message);
            onInserted();
        }
    }
}
