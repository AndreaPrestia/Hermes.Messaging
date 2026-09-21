using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Package smoke test: exercises ONLY the public consumer surface of the shipped NuGet package —
// registration, one subscription, one publish, and reading a PublishResult / diagnostics.
// If a required API is missing from the package (e.g. accidentally internalized), this fails to
// compile or run, catching packaging regressions that source-referenced tests cannot.

var handled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var tempStore = Path.Combine(Path.GetTempPath(), $"hermes_smoke_{Guid.NewGuid():N}");

using var host = Host.CreateDefaultBuilder()
    .ConfigureServices(services =>
    {
        services.AddHermesMessaging(opts => opts.PersistenceBasePath = tempStore);
        services.AddDeadLetterQueue<OrderCreated>();
        services.AddChannelSubscription<OrderCreated>("orders/created", (msg, _, _) =>
        {
            handled.TrySetResult(msg.OrderId);
            return Task.CompletedTask;
        });
    })
    .Build();

await host.StartAsync();

var bus = host.Services.GetRequiredService<IMessageBus>();
var diag = host.Services.GetRequiredService<IMessageBusDiagnostics>();

if (!diag.IsReady)
{
    Console.Error.WriteLine("SMOKE FAIL: bus not ready after start");
    Environment.Exit(2);
}

PublishResult result = await bus.PublishAsync(
    "orders/created",
    new OrderCreated("order-123"),
    new PublishOptions { CorrelationId = Guid.CreateVersion7(DateTimeOffset.UtcNow) });

var processedId = await handled.Task.WaitAsync(TimeSpan.FromSeconds(15));

await host.StopAsync();
try { Directory.Delete(tempStore, recursive: true); } catch { /* best-effort */ }

if (result.MessageId == Guid.Empty || processedId != "order-123")
{
    Console.Error.WriteLine("SMOKE FAIL: publish/handle roundtrip did not complete as expected");
    Environment.Exit(3);
}

Console.WriteLine($"SMOKE OK: MessageId={result.MessageId}, processed={processedId}");

internal sealed record OrderCreated(string OrderId);
