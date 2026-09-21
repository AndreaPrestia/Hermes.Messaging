using Hermes.Messaging.Domain.Entities;
using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Tests.Infrastructure;

/// <summary>
/// HERMES-007 P7 — end-to-end proof that explicit dead-letter replay grants a fresh retry budget.
/// </summary>
[Collection("MessageBus")]
public class ReplayRetryBudgetTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"h007replay_{Guid.NewGuid():N}");

    public ReplayRetryBudgetTests() => Directory.CreateDirectory(_tempPath);

    public void Dispose()
    {
        try { Directory.Delete(_tempPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ReplayedMessage_GetsFullRetryBudgetAgain()
    {
        var attempts = 0;

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts =>
                {
                    opts.PersistenceBasePath = _tempPath;
                    opts.MaxAttempts = 3;
                    opts.InitialRetryDelayMs = 1; // short, deterministic waits
                });
                services.AddDeadLetterQueue<TestMessage>();
                services.AddChannelSubscription<TestMessage>(
                    "h007/replay",
                    (_, _, _) => { Interlocked.Increment(ref attempts); throw new InvalidOperationException("always fails"); });
            })
            .Build();

        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var store = host.Services.GetRequiredService<PersistentMessageStore<TestMessage>>();
            var admin = host.Services.GetRequiredService<IDeadLetterAdministration<TestMessage>>();

            // First cycle: 3 handler invocations -> DeadLettered with AttemptCount == 3.
            var result = await bus.PublishAsync("h007/replay", new TestMessage("x"));
            await WaitForStatus(store, result.MessageId, MessageStatus.DeadLettered, TimeSpan.FromSeconds(30));
            Assert.Equal(3, Volatile.Read(ref attempts));
            Assert.Equal(3, store.GetByMessageId(result.MessageId)!.AttemptCount);

            // Replay -> Pending with a fresh budget.
            Assert.True(admin.Replay(result.MessageId));

            // Second cycle: another 3 invocations -> DeadLettered again.
            await WaitForStatus(store, result.MessageId, MessageStatus.DeadLettered, TimeSpan.FromSeconds(30));

            Assert.Equal(6, Volatile.Read(ref attempts));               // total handler invocations
            Assert.Equal(3, store.GetByMessageId(result.MessageId)!.AttemptCount); // second-cycle durable count
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task WaitForStatus(PersistentMessageStore<TestMessage> store, Guid id, MessageStatus status, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (store.GetByMessageId(id)?.Status == status) return;
            await Task.Delay(20);
        }
        Assert.Fail($"Message {id} did not reach {status} in time (last: {store.GetByMessageId(id)?.Status}).");
    }

    private sealed record TestMessage(string Value);
}
