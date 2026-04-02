using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;

namespace Hermes.Messaging.Tests.Infrastructure;

public class InMemoryMessageBusTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"msgbus_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private IServiceCollection ConfigureTestServices(IServiceCollection services)
    {
        services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
        services.AddDeadLetterQueue<TestMessage>();
        return services;
    }

    [Fact]
    public async Task PublishAsync_WithRegisteredRoute_InvokesHandler()
    {
        var received = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                ConfigureTestServices(services);
                services.AddChannelSubscription<TestMessage>(
                    "tests/route",
                    (message, _, _) =>
                    {
                        received.TrySetResult(message);
                        return Task.CompletedTask;
                    });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            var payload = new TestMessage(Guid.CreateVersion7(DateTimeOffset.UtcNow).ToString());

            await bus.PublishAsync("tests/route", payload);

            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(payload, result);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PublishAsync_WithUnmappedRoute_DoesNotInvokeHandler()
    {
        var received = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                ConfigureTestServices(services);
                services.AddChannelSubscription<TestMessage>(
                    "tests/mapped",
                    (message, _, _) =>
                    {
                        received.TrySetResult(message);
                        return Task.CompletedTask;
                    });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("tests/unmapped", new TestMessage("ignored"));

            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.NotSame(received.Task, completed);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PublishAsync_CreatesNewScopePerMessage()
    {
        var instances = new ConcurrentBag<Guid>();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                ConfigureTestServices(services);
                services.AddScoped<MessageInvocationTracker>();

                services.AddChannelSubscription<TestMessage>(
                    "tests/scoped",
                    (message, provider, _) =>
                    {
                        var tracker = provider.GetRequiredService<MessageInvocationTracker>();
                        instances.Add(tracker.InstanceId);

                        if (Interlocked.Increment(ref dispatchCount) == 2)
                        {
                            completion.TrySetResult(true);
                        }

                        return Task.CompletedTask;
                    });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("tests/scoped", new TestMessage("first"));
            await bus.PublishAsync("tests/scoped", new TestMessage("second"));

            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, instances.Count);
            Assert.Equal(2, instances.Distinct().Count());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PublishAsync_RetriesHandlerOnFailure()
    {
        var attempts = 0;
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                ConfigureTestServices(services);
                services.AddChannelSubscription<TestMessage>(
                    "tests/retry",
                    async (message, _, _) =>
                    {
                        attempts++;

                        if (attempts < 2)
                        {
                            throw new InvalidOperationException("Simulated failure");
                        }

                        completion.TrySetResult(attempts);
                        await Task.CompletedTask;
                    });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("tests/retry", new TestMessage("retry"));

            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, result);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private sealed record TestMessage(string Value);

    private sealed class MessageInvocationTracker
    {
        public Guid InstanceId { get; } = Guid.CreateVersion7(DateTimeOffset.UtcNow);
    }
}
