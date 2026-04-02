using Hermes.Messaging.Domain.Entities;
using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Tests.Infrastructure;

[Collection("MessageBus")]
public class CorrelationIdTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"msgbus_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task PublishAsync_GeneratesUniqueCorrelationId()
    {
        var correlationIds = new List<Guid>();
        var completion = new TaskCompletionSource<bool>();
        var count = 0;

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddDeadLetterQueue<TestMessage>();
                services.AddSingleton(correlationIds);
                services.AddChannelSubscription<TestMessage>(
                    "test/correlation",
                    (message, provider, _) =>
                    {
                        if (Interlocked.Increment(ref count) == 3)
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
            await bus.PublishAsync("test/correlation", new TestMessage("1"));
            await bus.PublishAsync("test/correlation", new TestMessage("2"));
            await bus.PublishAsync("test/correlation", new TestMessage("3"));

            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(3, count);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task CorrelationId_PropagatedToDeadLetterQueue()
    {
        var dlq = new DeadLetterQueue<TestMessage>();
        var completion = new TaskCompletionSource<bool>();
        var attempts = 0;

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddSingleton(dlq);
                services.AddChannelSubscription<TestMessage>(
                    "test/failing",
                    (message, _, _) =>
                    {
                        attempts++;
                        throw new InvalidOperationException("Intentional failure");
                    });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync("test/failing", new TestMessage("fail"));

            // Wait for retries and DLQ
            await Task.Delay(2000);

            var result = await dlq.TryReadAsync();
            Assert.True(result.Success);

            var deadLetter = result.Message as DeadLetterMessage<TestMessage>;
            Assert.NotNull(deadLetter);
            Assert.NotEqual(Guid.Empty, deadLetter.CorrelationId);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public void ChannelMessage_ContainsCorrelationId()
    {
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var message = new ChannelMessage<TestMessage>("test/path", new TestMessage("data"), correlationId);

        Assert.Equal(correlationId, message.CorrelationId);
        Assert.Equal("test/path", message.Path);
        Assert.Equal("data", message.Body.Value);
    }

    [Fact]
    public void DeadLetterMessage_ContainsCorrelationId()
    {
        var correlationId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var exception = new InvalidOperationException("Test");
        var failedAt = DateTimeOffset.UtcNow;

        var deadLetter = new DeadLetterMessage<TestMessage>(
            "test/path",
            new TestMessage("data"),
            exception,
            3,
            correlationId,
            failedAt);

        Assert.Equal(correlationId, deadLetter.CorrelationId);
        Assert.Equal("test/path", deadLetter.Path);
        Assert.Equal("data", deadLetter.Body.Value);
        Assert.Equal(3, deadLetter.Attempts);
        Assert.Equal(exception, deadLetter.Exception);
        Assert.Equal(failedAt, deadLetter.FailedAt);
    }

    private sealed record TestMessage(string Value);
}
