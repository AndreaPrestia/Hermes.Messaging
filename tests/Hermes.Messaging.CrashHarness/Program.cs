using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Crash harness for the HERMES-001 durable publish boundary.
//
// Usage: Hermes.Messaging.CrashHarness <persistenceBasePath>
//
// Behaviour:
//   1. Start a Hermes host whose handler NEVER completes (blocks forever), so the
//      published message stays Pending in the durable store.
//   2. Publish a message. Because publish is persist-before-signal, the returned
//      PublishResult means the record is already durably committed.
//   3. Print "MESSAGEID=<guid>" to stdout so the parent test can read it.
//   4. Hard-kill this process (Environment.FailFast) — NOT a graceful StopAsync —
//      to simulate a real crash after acceptance.
//
// The parent test then re-opens the same durable store and asserts the record survived.

if (args.Length < 1)
{
    Console.Error.WriteLine("Missing persistence base path argument.");
    Environment.Exit(2);
    return;
}

var basePath = args[0];

var host = Host.CreateDefaultBuilder()
    .ConfigureServices(services =>
    {
        services.AddHermesMessaging(opts => opts.PersistenceBasePath = basePath);
        services.AddDeadLetterQueue<HarnessMessage>();
        services.AddChannelSubscription<HarnessMessage>(
            "crash/route",
            // Never completes: keeps the message in the Pending state.
            async (_, _, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
            });
    })
    .Build();

await host.StartAsync();

var bus = host.Services.GetRequiredService<IMessageBus>();
var result = await bus.PublishAsync("crash/route", new HarnessMessage("crash-payload"));

// Signal acceptance to the parent, flush, then crash hard WITHOUT graceful shutdown.
// Emit the exact durable store path so the parent test does not have to guess it.
var safeTypeName = (typeof(HarnessMessage).FullName ?? typeof(HarnessMessage).Name)
    .Replace('.', '_').Replace('+', '_');
var dbPath = Path.Combine(basePath, $"{safeTypeName}.db");

Console.Out.WriteLine($"DBPATH={dbPath}");
Console.Out.WriteLine($"COLLECTION=messages_{typeof(HarnessMessage).Name}");
Console.Out.WriteLine($"MESSAGEID={result.MessageId}");
Console.Out.Flush();

// Hard crash: bypasses host.StopAsync / drain / dispose entirely.
Environment.FailFast("Simulated crash after durable publish acceptance.");

internal sealed record HarnessMessage(string Value);
