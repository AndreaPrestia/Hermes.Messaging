using Hermes.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// Phase 1 — durable publish boundary tests (HERMES-001).
/// A returned PublishResult means the message was durably committed before the call returned.
/// </summary>
[Collection("MessageBus")]
public class DurablePublishTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"durpub_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private IHost BuildHost(
        Func<TestMessage, IServiceProvider, CancellationToken, Task>? handler = null,
        Action<IServiceCollection>? extra = null)
    {
        handler ??= (_, _, _) => Task.CompletedTask;

        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddChannelSubscription<TestMessage>("durable/route", handler);
                extra?.Invoke(services);
            })
            .Build();
    }

    [Fact]
    public async Task Publish_Accepted_IsAlreadyPersisted()
    {
        // Handler blocks so the message cannot be marked Completed before we inspect the store.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(async (_, _, ct) => await release.Task.WaitAsync(ct));

        try
        {
            await host.StartAsync();
            var bus = host.Services.GetRequiredService<IMessageBus>();

            var result = await bus.PublishAsync("durable/route", new TestMessage("payload"));

            // The moment publish returns, the record must already exist in the durable store.
            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();
            var persisted = store.GetByMessageId(result.MessageId);

            Assert.NotNull(persisted);
            Assert.Equal(result.MessageId, persisted.MessageId);
            Assert.Equal(result.CorrelationId, persisted.CorrelationId);
            Assert.Equal("payload", persisted.Body.Value);
        }
        finally
        {
            release.TrySetResult(true);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Publish_PersistenceFailure_IsNotAccepted()
    {
        using var host = BuildHost(extra: services =>
        {
            // Force the durable store to fail on insert.
            services.RemoveAll<IMessageStore<TestMessage>>();
            services.AddSingleton<IMessageStore<TestMessage>>(new ThrowingMessageStore<TestMessage>());
        });

        try
        {
            await host.StartAsync();
            var bus = host.Services.GetRequiredService<IMessageBus>();

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await bus.PublishAsync("durable/route", new TestMessage("payload")));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Publish_SameCorrelationId_GetsDistinctMessageIds()
    {
        using var host = BuildHost();
        try
        {
            await host.StartAsync();
            var bus = host.Services.GetRequiredService<IMessageBus>();

            var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
            var options = new PublishOptions { CorrelationId = correlationId };

            var a = await bus.PublishAsync("durable/route", new TestMessage("a"), options);
            var b = await bus.PublishAsync("durable/route", new TestMessage("b"), options);

            Assert.Equal(correlationId, a.CorrelationId);
            Assert.Equal(correlationId, b.CorrelationId);
            Assert.NotEqual(a.MessageId, b.MessageId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Publish_MissingCorrelationId_GeneratesOne()
    {
        using var host = BuildHost();
        try
        {
            await host.StartAsync();
            var bus = host.Services.GetRequiredService<IMessageBus>();

            var result = await bus.PublishAsync("durable/route", new TestMessage("payload"));

            Assert.NotEqual(Guid.Empty, result.CorrelationId);
            Assert.NotEqual(Guid.Empty, result.MessageId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Publish_UnknownRoute_IsRejectedWithoutPersistence()
    {
        using var host = BuildHost();
        try
        {
            await host.StartAsync();
            var bus = host.Services.GetRequiredService<IMessageBus>();

            await Assert.ThrowsAsync<RouteNotFoundException>(async () =>
                await bus.PublishAsync("durable/unknown", new TestMessage("payload")));

            // Nothing should have been persisted for the rejected publish.
            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();
            Assert.Empty(store.GetPendingMessages());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Publish_NotificationFailure_RetainsDurableRecord()
    {
        // Complete the channel writer so the best-effort signal cannot be delivered.
        // The durable record must still exist (a lost signal never loses an accepted message).
        using var host = BuildHost();
        try
        {
            await host.StartAsync();
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var registry = host.Services.GetRequiredService<ChannelRegistry>();

            // Close the channel so TryWrite returns false (signal cannot be delivered).
            registry.GetOrCreate<TestMessage>().Writer.Complete();

            var result = await bus.PublishAsync("durable/route", new TestMessage("payload"));

            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();
            var persisted = store.GetByMessageId(result.MessageId);

            Assert.NotNull(persisted);
            Assert.Equal(MessageStatus.Pending, persisted.Status);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Publish_CancelBeforeCommit_NotAccepted()
    {
        using var host = BuildHost();
        try
        {
            await host.StartAsync();
            var bus = host.Services.GetRequiredService<IMessageBus>();

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await bus.PublishAsync("durable/route", new TestMessage("payload"), options: null, cts.Token));

            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();
            Assert.Empty(store.GetPendingMessages());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Publish_CancelAfterCommit_ReturnsAccepted()
    {
        // A token that is cancelled only cancels BEFORE the durable commit. Once committed,
        // the accepted result is returned and the durable record exists. Here the token is not
        // cancelled before commit, so the publish is accepted; cancelling afterwards is a no-op.
        using var release = new CancellationTokenSource();
        using var host = BuildHost(async (_, _, ct) =>
        {
            // Block the handler so we can inspect the persisted record post-accept.
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        });

        try
        {
            await host.StartAsync();
            var bus = host.Services.GetRequiredService<IMessageBus>();

            var result = await bus.PublishAsync("durable/route", new TestMessage("payload"), options: null, release.Token);

            // Cancel after the commit already happened — must not invalidate acceptance.
            await release.CancelAsync();

            Assert.NotEqual(Guid.Empty, result.MessageId);

            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();
            Assert.NotNull(store.GetByMessageId(result.MessageId));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed record TestMessage(string Value);

    /// <summary>
    /// A store that always fails on insert, to simulate a persistence failure.
    /// </summary>
    private sealed class ThrowingMessageStore<T> : IMessageStore<T>
    {
        public void Insert(ChannelMessage<T> message)
            => throw new InvalidOperationException("Simulated persistence failure");

        public PersistedMessage<T>? GetByMessageId(Guid messageId) => null;

        public void UpdateStatus(Guid messageId, MessageStatus status, string? error = null) { }

        public void IncrementAttempt(Guid messageId) { }

        public void DecrementAttempt(Guid messageId) { }

        public IEnumerable<PersistedMessage<T>> GetPendingMessages() => [];

        public PersistedMessage<T>? TryClaim(Guid messageId) => null;

        public void MarkCompleted(Guid messageId) { }

        public void ScheduleRetry(Guid messageId, DateTimeOffset nextAttemptAt, string? error) { }

        public void MarkDeadLettered(Guid messageId, string? error) { }

        public int RecoverInterrupted() => 0;

        public IEnumerable<PersistedMessage<T>> GetDueMessages(DateTimeOffset now) => [];

        public IReadOnlyList<Guid> GetDueMessageIds(DateTimeOffset now, int limit) => [];

        public bool ReplayDeadLetter(Guid messageId) => false;

        public IReadOnlyList<PersistedMessage<T>> ListDeadLetters(int skip = 0, int take = 100) => [];

        public bool DeleteDeadLetter(Guid messageId) => false;

        public int PurgeDeadLetters() => 0;

        public MessageStoreStats GetStats() => new();

        public int CleanupOldMessages() => 0;
    }
}
