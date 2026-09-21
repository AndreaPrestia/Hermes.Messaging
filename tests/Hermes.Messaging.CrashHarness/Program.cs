using Hermes.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Crash / recovery harness for the durable publish boundary and state machine.
//
// Usage:
//   Hermes.Messaging.CrashHarness crash   <persistenceBasePath>
//   Hermes.Messaging.CrashHarness recover <persistenceBasePath>
//
// crash mode (HERMES-001 / HERMES-002 crash window):
//   1. Start a host whose handler NEVER completes (blocks), so the published message is
//      left durable (Pending or Processing) but not Completed.
//   2. Publish a message (persist-before-signal => already durably committed).
//   3. Print DBPATH / COLLECTION / MESSAGEID, then hard-crash via Environment.FailFast.
//
// recover mode (HERMES-002 recovery):
//   1. Start a host with a WORKING handler on the same store/path.
//   2. Startup recovery returns interrupted Processing -> Pending; the message is then
//      re-claimed and processed to Completed.
//   3. Print RECOVERED=<id> and exit gracefully.

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: <crash|recover> <persistenceBasePath>");
    Environment.Exit(2);
    return;
}

var mode = args[0];
var basePath = args[1];

var safeTypeName = (typeof(HarnessMessage).FullName ?? typeof(HarnessMessage).Name)
    .Replace('.', '_').Replace('+', '_');
var dbPath = Path.Combine(basePath, $"{safeTypeName}.db");

if (mode == "crash")
{
    var host = Host.CreateDefaultBuilder()
        .ConfigureServices(services =>
        {
            services.AddHermesMessaging(opts => opts.PersistenceBasePath = basePath);
            services.AddChannelSubscription<HarnessMessage>(
                "crash/route",
                // Never completes: keeps the message from reaching Completed.
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

    Console.Out.WriteLine($"DBPATH={dbPath}");
    Console.Out.WriteLine($"COLLECTION=messages_{typeof(HarnessMessage).Name}");
    Console.Out.WriteLine($"MESSAGEID={result.MessageId}");
    Console.Out.Flush();

    // Hard crash: bypasses host.StopAsync / dispose entirely.
    Environment.FailFast("Simulated crash after durable publish acceptance.");
    return;
}

if (mode == "recover")
{
    var processed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);

    var host = Host.CreateDefaultBuilder()
        .ConfigureServices(services =>
        {
            services.AddHermesMessaging(opts => opts.PersistenceBasePath = basePath);
            services.AddChannelSubscription<HarnessMessage>(
                "crash/route",
                (_, _, _) =>
                {
                    processed.TrySetResult(Guid.Empty);
                    return Task.CompletedTask;
                });
        })
        .Build();

    await host.StartAsync();

    try
    {
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Console.Out.WriteLine("RECOVERED=1");
        Console.Out.Flush();
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine("Recovery timed out — message was not reprocessed.");
        await host.StopAsync();
        Environment.Exit(3);
        return;
    }

    await host.StopAsync();
    return;
}

Console.Error.WriteLine($"Unknown mode '{mode}'.");
Environment.Exit(2);

internal sealed record HarnessMessage(string Value);
