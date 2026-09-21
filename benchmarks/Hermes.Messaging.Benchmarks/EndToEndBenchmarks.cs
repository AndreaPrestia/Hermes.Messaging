using BenchmarkDotNet.Attributes;
using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Benchmarks;

/// <summary>
/// Measures the full end-to-end path for a batch of messages:
/// Publish → Persist → Signal → Claim → Handler → Completed, across concurrency levels.
/// A fresh host/store is created per iteration so batches never overlap.
/// </summary>
[MemoryDiagnoser]
public class EndToEndBenchmarks
{
    [Params(1, 4, 16)]
    public int MaxConcurrency;

    private const int BatchSize = 1000;
    private const string Route = "bench/e2e";

    private string _storeDir = null!;
    private IHost _host = null!;
    private IMessageBus _bus = null!;
    private CountdownEvent _remaining = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        _storeDir = BenchSupport.NewTempStore();
        _remaining = new CountdownEvent(BatchSize);
        _host = BenchSupport.StartHostAsync(
            _storeDir,
            Route,
            (_, _, _) => { _remaining.Signal(); return Task.CompletedTask; },
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

        // Wait until every message has been handled (completed end-to-end).
        _remaining.Wait(TimeSpan.FromMinutes(2));
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        _remaining.Dispose();
        BenchSupport.TryDelete(_storeDir);
    }
}
