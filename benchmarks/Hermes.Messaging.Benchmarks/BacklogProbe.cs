using System.Diagnostics;
using Hermes.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Benchmarks;

/// <summary>
/// Single-shot, deterministic wall-clock probe of backlog recovery/drain. This is NOT
/// BenchmarkDotNet (no multi-iteration statistics); it exists to capture representative
/// recovery numbers for large backlogs without the very long BDN pilot/warmup cost.
/// </summary>
internal static class BacklogProbe
{
    public static async Task RunAsync(int[] sizes)
    {
        const string route = "probe/backlog";
        Console.WriteLine("backlog,seed_ms,timeToFirst_ms,drain_ms,drainRate_msgPerSec");

        foreach (var size in sizes)
        {
            var dir = BenchSupport.NewTempStore();
            var safeTypeName = (typeof(BenchMessage).FullName ?? nameof(BenchMessage)).Replace('.', '_').Replace('+', '_');
            var dbPath = Path.Combine(dir, $"{safeTypeName}.db");

            // Seed durable Pending backlog.
            var seedSw = Stopwatch.StartNew();
            using (var seed = new PersistentMessageStore<BenchMessage>(dbPath))
            {
                for (var i = 0; i < size; i++)
                {
                    var id = Guid.CreateVersion7(DateTimeOffset.UtcNow);
                    seed.Insert(new ChannelMessage<BenchMessage>(route, BenchSupport.MakeMessage(i, 128), Guid.CreateVersion7(DateTimeOffset.UtcNow), id));
                }
            }
            seedSw.Stop();

            var firstSw = new Stopwatch();
            var drainSw = new Stopwatch();
            var firstSeen = false;

            drainSw.Start();
            firstSw.Start();
            var host = await BenchSupport.StartHostAsync(
                dir,
                route,
                (_, _, _) =>
                {
                    // timeToFirst measures latency to the FIRST handler invocation only.
                    if (!firstSeen) { firstSeen = true; firstSw.Stop(); }
                    return Task.CompletedTask;
                },
                maxConcurrency: 4);

            // Drain is measured against the DURABLE store backlog reaching 0 (every record
            // Completed/DeadLettered), not the handler countdown — a handler returning does not
            // mean the record is durably Completed yet.
            BenchSupport.WaitForDurableDrain<BenchMessage>(host, TimeSpan.FromMinutes(10));
            drainSw.Stop();

            await host.StopAsync();
            host.Dispose();
            BenchSupport.TryDelete(dir);

            var rate = size / Math.Max(0.001, drainSw.Elapsed.TotalSeconds);
            Console.WriteLine($"{size},{seedSw.ElapsedMilliseconds},{firstSw.ElapsedMilliseconds},{drainSw.ElapsedMilliseconds},{rate:F0}");
        }
    }
}
