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

            // Wait for all retries (3 attempts + delays)
            await Task.Delay(2000);

            Assert.Equal(3, attempts); // Max retry attempts
            
            var result = await dlq.TryReadAsync();
            Assert.True(result.Success);
            
            var deadLetter = result.Message as DeadLetterMessage<TestMessage>;
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
            
            // First message will fail and open the circuit
            await bus.PublishAsync("test/circuit", new TestMessage("first"));
            await Task.Delay(500);

            // Reset attempts counter
            attempts = 0;

            // Second message should hit open circuit
            await bus.PublishAsync("test/circuit", new TestMessage("second"));
            await Task.Delay(500);

            // Circuit breaker should have prevented the handler from being called
            Assert.Equal(0, attempts);
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
    public async Task DrainPendingMessages_OnShutdown_ProcessesRemainingMessages()
    {
        var processedCount = 0;
        var lockObj = new object();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddDeadLetterQueue<TestMessage>();
                services.AddChannelSubscription<TestMessage>(
                    "test/drain",
                    async (message, _, _) =>
                    {
                        // Simulate slow processing
                        await Task.Delay(100);
                        lock (lockObj)
                        {
                            processedCount++;
                        }
                    });
            })
            .Build();

        await host.StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        
        // Enqueue multiple messages quickly
        for (int i = 0; i < 5; i++)
        {
            await bus.PublishAsync("test/drain", new TestMessage($"message-{i}"));
        }

        // Stop immediately
        await host.StopAsync();
        host.Dispose();

        // All messages should have been processed
        Assert.Equal(5, processedCount);
    }

    private sealed record TestMessage(string Value);
}
