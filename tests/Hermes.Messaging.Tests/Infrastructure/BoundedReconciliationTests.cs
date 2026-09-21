using System.Collections.Concurrent;
using Hermes.Messaging.Domain.Entities;
using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// HERMES-007 P5/P6 — bounded reconciliation and large-backlog behaviour.
/// </summary>
[Collection("MessageBus")]
public class BoundedReconciliationTests : IDisposable
{
    // Must match PersistentChannelRouterSubscriber.WakeupCapacity.
    private const int WakeupCapacity = 4096;

    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"h007_{Guid.NewGuid():N}");

    public BoundedReconciliationTests() => Directory.CreateDirectory(_tempPath);

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort */ }
    }

    private IHost BuildHost(
        Func<TestMessage, IServiceProvider, CancellationToken, Task> handler,
        int maxConcurrency = 1)
        => Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts =>
                {
                    opts.PersistenceBasePath = _tempPath;
                    opts.MaxConcurrency = maxConcurrency;
                    opts.InitialRetryDelayMs = 1;
                });
                services.AddDeadLetterQueue<TestMessage>();
                services.AddChannelSubscription<TestMessage>("h007/route", handler);
            })
            .Build();

    [Fact]
    public async Task Reconciliation_DoesNotQueryMoreThanAvailableWakeupCapacity()
    {
        // A blocked handler holds workers so nothing drains; reconciliation keeps running but must
        // never request more than the wake-up capacity.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(async (_, _, ct) => { try { await gate.Task.WaitAsync(ct); } catch (OperationCanceledException) { } });

        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();

            // Publish more than the wake-up capacity so the durable backlog exceeds it.
            for (var i = 0; i < WakeupCapacity + 500; i++)
            {
                await bus.PublishAsync("h007/route", new TestMessage(i.ToString()));
            }

            // Let a few reconciliation cycles run.
            await Task.Delay(2500);

            Assert.True(store.MaxRequestedDueLimit <= WakeupCapacity,
                $"Reconciliation requested {store.MaxRequestedDueLimit} which exceeds capacity {WakeupCapacity}.");
        }
        finally
        {
            gate.TrySetResult(true);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DuplicateDueScans_DoNotAccumulateUnboundedWakeups()
    {
        // Same as above: repeated reconciliation scans over a stuck backlog must not grow the
        // requested batch beyond capacity (the outstanding set dedups; capacity is the ceiling).
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(async (_, _, ct) => { try { await gate.Task.WaitAsync(ct); } catch (OperationCanceledException) { } });

        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();

            for (var i = 0; i < 200; i++)
            {
                await bus.PublishAsync("h007/route", new TestMessage(i.ToString()));
            }

            await Task.Delay(3000); // multiple reconciliation cycles

            // Never exceeds capacity across all scans, despite many duplicate due-scans.
            Assert.True(store.MaxRequestedDueLimit <= WakeupCapacity);
        }
        finally
        {
            gate.TrySetResult(true);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task MissedSignal_IsEventuallyRecovered()
    {
        // Insert a durable Pending record directly into the store the host will use, WITHOUT
        // publishing (so no fast-path signal). Reconciliation must discover and process it.
        var completed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid injectedId;

        // Pre-create the durable record using the deterministic per-type db path.
        var safeTypeName = (typeof(TestMessage).FullName ?? nameof(TestMessage)).Replace('.', '_').Replace('+', '_');
        var dbPath = Path.Combine(_tempPath, $"{safeTypeName}.db");
        using (var seed = new PersistentMessageStore<TestMessage>(dbPath))
        {
            injectedId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
            seed.Insert(new ChannelMessage<TestMessage>("h007/route", new TestMessage("missed"), Guid.CreateVersion7(DateTimeOffset.UtcNow), injectedId));
        }

        using var host = BuildHost((_, _, _) => { completed.TrySetResult(injectedId); return Task.CompletedTask; });
        await host.StartAsync();
        try
        {
            // No PublishAsync for injectedId — only reconciliation/startup-seeding can pick it up.
            var recovered = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(injectedId, recovered);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task LargeBacklog_IsEventuallyProcessed_InBoundedBatches()
    {
        // Scale chosen to require multiple bounded refill cycles while staying stable on CI.
        const int total = 6000;

        var processed = new ConcurrentDictionary<string, byte>();
        var allDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = BuildHost((msg, _, _) =>
        {
            processed[msg.Value] = 0;
            if (processed.Count == total) allDone.TrySetResult(true);
            return Task.CompletedTask;
        }, maxConcurrency: 4);

        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();

            for (var i = 0; i < total; i++)
            {
                await bus.PublishAsync("h007/route", new TestMessage(i.ToString()));
            }

            await allDone.Task.WaitAsync(TimeSpan.FromSeconds(120));

            Assert.Equal(total, processed.Count);
            // Peak requested due batch never exceeds the bounded wake-up capacity.
            Assert.True(store.MaxRequestedDueLimit <= WakeupCapacity,
                $"Peak requested batch {store.MaxRequestedDueLimit} exceeded capacity {WakeupCapacity}.");

            // All durable records reached Completed.
            var stats = store.GetStats();
            Assert.Equal(0, stats.PendingCount);
            Assert.Equal(0, stats.ProcessingCount);
            Assert.Equal(0, stats.RetryScheduledCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed record TestMessage(string Value);
}
