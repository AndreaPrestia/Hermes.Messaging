using System.Collections.Concurrent;
using Hermes.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// HERMES-004 — runtime lifecycle states and fixed-worker concurrency.
/// </summary>
[Collection("MessageBus")]
public class LifecycleConcurrencyTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"lifecycle_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private IHost BuildHost(
        Func<TestMessage, IServiceProvider, CancellationToken, Task> handler,
        int maxConcurrency = 1,
        TimeSpan? grace = null)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts =>
                {
                    opts.PersistenceBasePath = _tempPath;
                    opts.MaxConcurrency = maxConcurrency;
                    if (grace is not null) opts.ShutdownGracePeriod = grace.Value;
                });
                services.AddChannelSubscription<TestMessage>("life/route", handler);
            })
            .Build();
    }

    [Fact]
    public async Task Publish_BeforeReady_IsRejected()
    {
        using var host = BuildHost((_, _, _) => Task.CompletedTask);

        // Not started yet -> state is Created, not Ready.
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var state = host.Services.GetRequiredService<HermesRuntimeState>();
        Assert.Equal(RuntimeState.Created, state.Current);

        await Assert.ThrowsAsync<HermesNotReadyException>(async () =>
            await bus.PublishAsync("life/route", new TestMessage("x")));
    }

    [Fact]
    public async Task Publish_WhenReady_IsAccepted()
    {
        using var host = BuildHost((_, _, _) => Task.CompletedTask);
        await host.StartAsync();
        try
        {
            var state = host.Services.GetRequiredService<HermesRuntimeState>();
            Assert.Equal(RuntimeState.Ready, state.Current);

            var bus = host.Services.GetRequiredService<IMessageBus>();
            var result = await bus.PublishAsync("life/route", new TestMessage("x"));
            Assert.NotEqual(Guid.Empty, result.MessageId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Publish_DuringStopping_IsRejected()
    {
        using var host = BuildHost((_, _, _) => Task.CompletedTask);
        await host.StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        var state = host.Services.GetRequiredService<HermesRuntimeState>();

        await host.StopAsync();

        // After stopping, the runtime is no longer Ready and publishes are rejected.
        Assert.NotEqual(RuntimeState.Ready, state.Current);
        await Assert.ThrowsAsync<HermesNotReadyException>(async () =>
            await bus.PublishAsync("life/route", new TestMessage("x")));
    }

    [Fact]
    public async Task ConcurrentWorkers_ProcessInParallel()
    {
        const int concurrency = 4;
        var inFlight = 0;
        var maxObserved = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = BuildHost(async (_, _, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxObserved, now);
            if (now >= concurrency) reached.TrySetResult(true);
            try { await gate.Task.WaitAsync(ct); } catch (OperationCanceledException) { }
            Interlocked.Decrement(ref inFlight);
        }, maxConcurrency: concurrency);

        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            for (var i = 0; i < concurrency; i++)
            {
                await bus.PublishAsync("life/route", new TestMessage($"m{i}"));
            }

            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(maxObserved >= 2, $"Expected parallel processing; max concurrent observed = {maxObserved}");
        }
        finally
        {
            gate.TrySetResult(true);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DuplicateSignals_ProcessMessageOnce()
    {
        var processed = new ConcurrentBag<Guid>();
        var handled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = BuildHost((msg, _, _) =>
        {
            processed.Add(Guid.Parse(msg.Value));
            handled.TrySetResult(true);
            return Task.CompletedTask;
        }, maxConcurrency: 4);

        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var result = await bus.PublishAsync("life/route", new TestMessage(Guid.CreateVersion7(DateTimeOffset.UtcNow).ToString()));

            // Flood duplicate wake-ups for the same message id via the registry channel.
            var registry = host.Services.GetRequiredService<ChannelRegistry>();
            var channel = registry.GetOrCreate<TestMessage>();
            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();
            for (var i = 0; i < 10; i++)
            {
                channel.Writer.TryWrite(new ChannelMessage<TestMessage>("life/route", new TestMessage("dup"), result.CorrelationId, result.MessageId));
            }

            await handled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Allow duplicates to be drained and rejected by TryClaim.
            await Task.Delay(500);

            var persisted = store.GetByMessageId(result.MessageId);
            Assert.NotNull(persisted);
            Assert.Equal(MessageStatus.Completed, persisted.Status);
            // Attempt count reflects a single successful claim/processing.
            Assert.Equal(1, persisted.AttemptCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task InFlightShutdown_LeavesMessageDurable_ForRestart()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = BuildHost(async (_, _, ct) =>
        {
            started.TrySetResult(true);
            await block.Task.WaitAsync(ct); // propagate cancellation on shutdown
        }, grace: TimeSpan.FromMilliseconds(200));

        await host.StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();
        var result = await bus.PublishAsync("life/route", new TestMessage("x"));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Shutdown while the handler is blocked; the bounded grace period elapses.
        await host.StopAsync();

        var persisted = store.GetByMessageId(result.MessageId);
        Assert.NotNull(persisted);
        Assert.NotEqual(MessageStatus.Completed, persisted.Status); // left durable, not lost
    }

    [Fact]
    public async Task Restart_ContinuesBacklog()
    {
        var firstProcessed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid messageId;

        // Run 1: publish and block so the message never completes, then stop.
        using (var host1 = BuildHost(async (_, _, ct) =>
        {
            firstProcessed.TrySetResult(Guid.Empty);
            await block.Task.WaitAsync(ct);
        }, grace: TimeSpan.FromMilliseconds(200)))
        {
            await host1.StartAsync();
            var bus = host1.Services.GetRequiredService<IMessageBus>();
            var result = await bus.PublishAsync("life/route", new TestMessage("x"));
            messageId = result.MessageId;
            await firstProcessed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await host1.StopAsync();
        }

        // Run 2: fresh host on the same store with a working handler; backlog is recovered & completed.
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host2 = BuildHost((_, _, _) =>
        {
            completed.TrySetResult(true);
            return Task.CompletedTask;
        });

        await host2.StartAsync();
        try
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var store = host2.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            PersistedMessage<TestMessage>? persisted = null;
            while (DateTime.UtcNow < deadline)
            {
                persisted = store.GetByMessageId(messageId);
                if (persisted?.Status == MessageStatus.Completed) break;
                await Task.Delay(50);
            }

            Assert.NotNull(persisted);
            Assert.Equal(MessageStatus.Completed, persisted!.Status);
        }
        finally
        {
            await host2.StopAsync();
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) break;
        }
    }

    private sealed record TestMessage(string Value);
}
