using BenchmarkDotNet.Attributes;
using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Benchmarks;

/// <summary>
/// Measures the full end-to-end path for a batch of messages:
/// Publish → Persist → Signal → Claim → Handler → durable Completed, across concurrency levels.
/// A fresh host/store is created per iteration so batches never overlap.
/// <para>
/// Completion is measured against the DURABLE store state (backlog == 0), not the handler
/// returning. A handler returning only means the callback ran; the record is not yet Completed
/// until <c>MarkCompleted</c> commits. Waiting on the durable backlog is the only faithful
/// end-to-end signal.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class EndToEndBenchmarks
{
    [Params(1, 4, 16)]
    public int MaxConcurrency;

    private const int BatchSize = 1000;
    private const string Route = "bench/e2e";
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(2);

    private string _storeDir = null!;
    private IHost _host = null!;
    private IMessageBus _bus = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        _storeDir = BenchSupport.NewTempStore();
        _host = BenchSupport.StartHostAsync(
            _storeDir,
            Route,
            (_, _, _) => Task.CompletedTask,
            MaxConcurrency).GetAwaiter().GetResult();
        _bus = _host.Services.GetRequiredService<IMessageBus>();
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void ProcessBatch()
    {
        for (var i = 0; i < BatchSize; i++)
        {
            _bus.PublishAsync(Route, BenchSupport.MakeMessage(i, 256)).AsTask().GetAwaiter().GetResult();
        }

        // Wait until every message is durably Completed (backlog drained to 0), not merely handled.
        BenchSupport.WaitForDurableDrain<BenchMessage>(_host, DrainTimeout);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        BenchSupport.TryDelete(_storeDir);
    }
}
