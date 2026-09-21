using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Package smoke test: exercises ONLY the public consumer surface of the shipped NuGet package.
// It intentionally references NO implementation type — if consumer-facing functionality accidentally
// depended on an internalized type, this project would fail to COMPILE, catching packaging/API
// regressions that source-referenced tests cannot.
//
// Covered public capabilities:
//   * create host + AddHermesMessaging(configure MessageBusOptions)
//   * register a subscription (AddChannelSubscription) + a dead-letter queue
//   * start host, resolve IMessageBus, publish, receive/handle, read PublishResult
//   * resolve IMessageBusDiagnostics, read IsReady / IsHealthy / CurrentState
//   * throw NonRetryableException from a handler -> message is dead-lettered
//   * manage dead letters via IDeadLetterAdministration<T> (List / Get / Replay / Purge)

var handled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var poisonSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var tempStore = Path.Combine(Path.GetTempPath(), $"hermes_smoke_{Guid.NewGuid():N}");

using var host = Host.CreateDefaultBuilder()
    .ConfigureServices(services =>
    {
        services.AddHermesMessaging(opts =>
        {
            opts.PersistenceBasePath = tempStore;
            opts.MaxAttempts = 1;          // dead-letter immediately on first (non-retryable) failure
            opts.InitialRetryDelayMs = 1;
        });

        services.AddDeadLetterQueue<OrderCreated>();
        services.AddChannelSubscription<OrderCreated>("orders/created", (msg, _, _) =>
        {
            handled.TrySetResult(msg.OrderId);
            return Task.CompletedTask;
        });

        services.AddDeadLetterQueue<PoisonMessage>();
        services.AddChannelSubscription<PoisonMessage>("orders/poison", (_, _, _) =>
        {
            poisonSeen.TrySetResult(true);
            // Signalling a permanent, non-retryable failure — Hermes must dead-letter immediately.
            throw new NonRetryableException("poison message: not processable");
        });
    })
    .Build();

await host.StartAsync();

var bus = host.Services.GetRequiredService<IMessageBus>();
var diag = host.Services.GetRequiredService<IMessageBusDiagnostics>();
var dlqAdmin = host.Services.GetRequiredService<IDeadLetterAdministration<PoisonMessage>>();

if (!diag.IsReady) Fail(2, "bus not ready after start");
if (!diag.IsHealthy) Fail(2, "bus not healthy after start");
if (diag.CurrentState != RuntimeState.Ready) Fail(2, $"unexpected runtime state: {diag.CurrentState}");

// --- happy path -----------------------------------------------------------------------------
PublishResult result = await bus.PublishAsync(
    "orders/created",
    new OrderCreated("order-123"),
    new PublishOptions { CorrelationId = Guid.CreateVersion7(DateTimeOffset.UtcNow) });

var processedId = await handled.Task.WaitAsync(TimeSpan.FromSeconds(15));
if (result.MessageId == Guid.Empty || processedId != "order-123")
    Fail(3, "publish/handle roundtrip did not complete as expected");

// --- dead-letter path via NonRetryableException ---------------------------------------------
var poison = await bus.PublishAsync("orders/poison", new PoisonMessage("bad"));
await poisonSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));

// Wait until the durable dead-letter entry is observable through the admin API.
DeadLetterEntry<PoisonMessage>? entry = null;
var deadline = DateTime.UtcNow.AddSeconds(15);
while (DateTime.UtcNow < deadline)
{
    entry = dlqAdmin.Get(poison.MessageId);
    if (entry is not null) break;
    await Task.Delay(50);
}
if (entry is null) Fail(4, "poison message was not dead-lettered / not visible via admin API");

if (dlqAdmin.List().Count < 1) Fail(4, "IDeadLetterAdministration.List returned no entries");

// Replay then purge, exercising the rest of the admin surface.
if (!dlqAdmin.Replay(poison.MessageId)) Fail(4, "IDeadLetterAdministration.Replay returned false");
_ = dlqAdmin.Purge();

await host.StopAsync();
try { Directory.Delete(tempStore, recursive: true); } catch { /* best-effort */ }

Console.WriteLine($"SMOKE OK: MessageId={result.MessageId}, processed={processedId}, deadLettered={entry!.MessageId}");
return;

static void Fail(int code, string message)
{
    Console.Error.WriteLine($"SMOKE FAIL: {message}");
    Environment.Exit(code);
}

internal sealed record OrderCreated(string OrderId);
internal sealed record PoisonMessage(string Value);
