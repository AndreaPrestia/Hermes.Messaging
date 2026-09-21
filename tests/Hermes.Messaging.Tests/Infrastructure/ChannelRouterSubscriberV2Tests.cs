using Hermes.Messaging.Domain.Entities;
using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Tests.Infrastructure;

[Collection("MessageBus")]
public class PersistentChannelRouterSubscriberTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"msgbus_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task DispatchWithRetry_SuccessOnFirstAttempt_NoRetry()
    {
        var attempts = 0;
        var completion = new TaskCompletionSource<bool>();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddDeadLetterQueue<TestMessage>();
                services.AddChannelSubscription<TestMessage>(
                    "test/success",
                    (message, _, _) =>
                    {
                        attempts++;
                        completion.TrySetResult(true);
                        return Task.CompletedTask;
                    });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("test/success", new TestMessage("data"));

            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, attempts);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task DispatchWithRetry_FailsOnce_RetriesAndSucceeds()
    {
        var attempts = 0;
        var completion = new TaskCompletionSource<bool>();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddDeadLetterQueue<TestMessage>();
                services.AddChannelSubscription<TestMessage>(
                    "test/retry",
                    (message, _, _) =>
                    {
                        attempts++;
                        if (attempts < 2)
                        {
                            throw new InvalidOperationException("Transient failure");
                        }
                        completion.TrySetResult(true);
                        return Task.CompletedTask;
                    });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("test/retry", new TestMessage("data"));

            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, attempts);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task DispatchWithRetry_ExceedsMaxRetries_MovesToDeadLetterQueue()
    {
        var attempts = 0;
        var dlq = new DeadLetterQueue<TestMessage>();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(dlq);
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddChannelSubscription<TestMessage>(
                    "test/fail",
                    (message, _, _) =>
                    {
                        attempts++;
                        throw new InvalidOperationException("Permanent failure");
                    });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("test/fail", new TestMessage("data"));

            // Retries are now durable and driven by the reconciliation loop (RetryScheduled ->
            // due -> re-signal), so wait for the dead letter rather than a fixed delay.
            var deadLetter = await dlq.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(3, attempts); // Max retry attempts

            Assert.NotNull(deadLetter);
            Assert.Equal("test/fail", deadLetter.Path);
            Assert.Equal(3, deadLetter.Attempts);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task DispatchWithRetry_CircuitBreakerOpen_ThrowsCircuitBreakerOpenException()
    {
        var circuitBreaker = new CircuitBreaker(failureThreshold: 1);
        var attempts = 0;

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(circuitBreaker);
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddDeadLetterQueue<TestMessage>();
                services.AddChannelSubscription<TestMessage>(
                    "test/circuit",
                    (message, _, _) =>
                    {
                        attempts++;
                        throw new InvalidOperationException("Failure");
                    });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();

            // First message will fail and open the circuit.
            await bus.PublishAsync("test/circuit", new TestMessage("first"));

            // Wait until the circuit is observed open.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!circuitBreaker.IsOpen("TestMessage:test/circuit") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            Assert.True(circuitBreaker.IsOpen("TestMessage:test/circuit"), "Circuit should be open after the first failure.");

            // With the circuit open, a second message must not have its handler invoked.
            attempts = 0;
            var second = await bus.PublishAsync("test/circuit", new TestMessage("second"));
            await Task.Delay(500);

            Assert.Equal(0, attempts);

            // And the second message was not completed while the circuit was open.
            var persistedSecond = store.GetByMessageId(second.MessageId);
            Assert.NotNull(persistedSecond);
            Assert.NotEqual(MessageStatus.Completed, persistedSecond.Status);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task DispatchWithRetry_RecordsSuccessInCircuitBreaker()
    {
        var circuitBreaker = new CircuitBreaker(failureThreshold: 5);
        var completion = new TaskCompletionSource<bool>();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(circuitBreaker);
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddDeadLetterQueue<TestMessage>();
                services.AddChannelSubscription<TestMessage>(
                    "test/success",
                    (message, _, _) =>
                    {
                        completion.TrySetResult(true);
                        return Task.CompletedTask;
                    });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("test/success", new TestMessage("data"));

            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Circuit should remain closed
            Assert.False(circuitBreaker.IsOpen("TestMessage:test/success"));
            Assert.Equal(CircuitStateEnum.Closed, circuitBreaker.GetState("TestMessage:test/success"));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task UnprocessedBacklog_OnShutdown_RemainsDurableForRestart()
    {
        // SDD 06: shutdown does NOT drain the entire backlog. Any message not yet completed
        // stays durable (Pending / RetryScheduled / Processing) and is recovered on restart —
        // it must never be silently lost. This replaces the old "drain everything" behavior.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddDeadLetterQueue<TestMessage>();
                services.AddChannelSubscription<TestMessage>(
                    "test/drain",
                    async (message, _, ct) =>
                    {
                        Interlocked.Increment(ref started);
                        // Block so processing cannot finish before we stop the host. A realistic
                        // handler propagates cancellation (does not swallow it), so the message
                        // is left non-Completed and recovered on the next start.
                        await gate.Task.WaitAsync(ct);
                    });
            })
            .Build();

        await host.StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        var ids = new List<Guid>();
        for (int i = 0; i < 5; i++)
        {
            var r = await bus.PublishAsync("test/drain", new TestMessage($"message-{i}"));
            ids.Add(r.MessageId);
        }

        var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();

        // Stop abruptly while work is blocked. Query the store BEFORE disposing the host,
        // since the store is a host-owned singleton that is disposed with the host.
        await host.StopAsync();

        // No message was completed, and none was lost — every one is still durable.
        foreach (var id in ids)
        {
            var persisted = store.GetByMessageId(id);
            Assert.NotNull(persisted);
            Assert.NotEqual(MessageStatus.Completed, persisted.Status);
        }

        host.Dispose();
    }

    private sealed record TestMessage(string Value);
}
