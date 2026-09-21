using Hermes.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// HERMES-003 — end-to-end retry classification and durable DLQ behavior through a host.
/// </summary>
[Collection("MessageBus")]
public class RetryDlqIntegrationTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"retrydlq_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task NonRetryable_DeadLettersImmediately_WithoutRetries_AndIsInspectableAndReplayable()
    {
        var attempts = 0;

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                services.AddChannelSubscription<TestMessage>(
                    "test/poison",
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref attempts);
                        throw new NonRetryableException("poison");
                    });
            })
            .Build();

        await host.StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        var admin = host.Services.GetRequiredService<IDeadLetterAdministration<TestMessage>>();

        var result = await bus.PublishAsync("test/poison", new TestMessage("x"));

        // Wait until it appears in the durable DLQ.
        var entry = await WaitForDeadLetterAsync(admin, result.MessageId);
        Assert.NotNull(entry);

        // Only one attempt was made — non-retryable failures are not retried.
        Assert.Equal(1, attempts);
        Assert.Equal("test/poison", entry!.Route);

        // Inspection is non-destructive: still present after Get/List.
        Assert.NotNull(admin.Get(result.MessageId));
        Assert.Single(admin.List());

        // Replay returns it to Pending (removes from DLQ list).
        Assert.True(admin.Replay(result.MessageId));
        Assert.Null(admin.Get(result.MessageId));

        await host.StopAsync();
    }

    [Fact]
    public async Task ObserverException_DoesNotDeleteDurableRecord()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _tempPath);
                // Observer that always throws — must NOT affect the durable record.
                services.AddSingleton<IDeadLetterHandler<TestMessage>, ThrowingDeadLetterHandler>();
                services.AddChannelSubscription<TestMessage>(
                    "test/poison",
                    (_, _, _) => throw new NonRetryableException("poison"));
            })
            .Build();

        await host.StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        var admin = host.Services.GetRequiredService<IDeadLetterAdministration<TestMessage>>();

        var result = await bus.PublishAsync("test/poison", new TestMessage("x"));

        var entry = await WaitForDeadLetterAsync(admin, result.MessageId);
        Assert.NotNull(entry);

        // Give the observer time to run (and throw) a few poll cycles.
        await Task.Delay(500);

        // The durable dead-letter record is retained regardless of observer failure.
        Assert.NotNull(admin.Get(result.MessageId));

        await host.StopAsync();
    }

    private static async Task<DeadLetterEntry<TestMessage>?> WaitForDeadLetterAsync(
        IDeadLetterAdministration<TestMessage> admin, Guid messageId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var entry = admin.Get(messageId);
            if (entry is not null) return entry;
            await Task.Delay(50);
        }
        return null;
    }

    private sealed record TestMessage(string Value);

    private sealed class ThrowingDeadLetterHandler : IDeadLetterHandler<TestMessage>
    {
        public Task HandleAsync(DeadLetterMessage<TestMessage> deadLetter, CancellationToken cancellationToken)
            => throw new InvalidOperationException("observer failure");
    }
}
