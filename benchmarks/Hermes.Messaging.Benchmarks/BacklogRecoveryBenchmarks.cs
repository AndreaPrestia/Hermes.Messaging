using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Hermes.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Benchmarks;

/// <summary>
/// Measures crash-recovery / reconciliation behaviour with a pre-existing durable backlog:
/// startup recovery, time-to-first-message, and full backlog drain. Long-running — run explicitly
/// with a filter. Datasets are kept modest so they complete on a developer machine.
/// </summary>
[MemoryDiagnoser]
public class BacklogRecoveryBenchmarks
{
    [Params(1_000, 10_000, 50_000)]
    public int Backlog;

    private const string Route = "bench/backlog";
    private string _storeDir = null!;
    private string _dbPath = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        _storeDir = BenchSupport.NewTempStore();
        var safeTypeName = (typeof(BenchMessage).FullName ?? nameof(BenchMessage)).Replace('.', '_').Replace('+', '_');
        _dbPath = Path.Combine(_storeDir, $"{safeTypeName}.db");

        // Pre-seed a durable Pending backlog directly in the store (no host running yet).
        using var seed = new PersistentMessageStore<BenchMessage>(_dbPath);
        for (var i = 0; i < Backlog; i++)
        {
            var id = Guid.CreateVersion7(DateTimeOffset.UtcNow);
            seed.Insert(new ChannelMessage<BenchMessage>(Route, BenchSupport.MakeMessage(i, 128), Guid.CreateVersion7(DateTimeOffset.UtcNow), id));
        }
    }

    /// <summary>
    /// Start the host and drain the entire pre-existing backlog to durable Completed. Completion
    /// is measured against the durable store backlog reaching 0, not the handler countdown — a
    /// handler returning does not mean the record is durably Completed yet.
    /// </summary>
    [Benchmark]
    public void RecoverAndDrain()
    {
        var firstStopwatch = Stopwatch.StartNew();
        var firstSeen = false;

        var host = BenchSupport.StartHostAsync(
            _storeDir,
            Route,
            (_, _, _) =>
            {
                if (!firstSeen) { firstSeen = true; firstStopwatch.Stop(); }
                return Task.CompletedTask;
            },
            maxConcurrency: 4).GetAwaiter().GetResult();

        BenchSupport.WaitForDurableDrain<BenchMessage>(host, TimeSpan.FromMinutes(10));

        host.StopAsync().GetAwaiter().GetResult();
        host.Dispose();
    }

    [IterationCleanup]
    public void IterationCleanup() => BenchSupport.TryDelete(_storeDir);
}
