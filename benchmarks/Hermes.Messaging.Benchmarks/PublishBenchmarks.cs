using BenchmarkDotNet.Attributes;
using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Benchmarks;

/// <summary>
/// Measures the durable publish path: validation → LiteDB durable insert → best-effort signal →
/// PublishResult. A no-op handler drains the channel so the store does not grow unbounded during
/// the run, but the measured operation is the publish call itself.
/// </summary>
[MemoryDiagnoser]
public class PublishBenchmarks
{
    [Params(100, 1024, 10240)]
    public int PayloadBytes;

    private IHost _host = null!;
    private IMessageBus _bus = null!;
    private BenchMessage _message = null!;
    private string _storeDir = null!;
    private const string Route = "bench/publish";

    [GlobalSetup]
    public void Setup()
    {
        _storeDir = BenchSupport.NewTempStore();
        _host = BenchSupport.StartHostAsync(_storeDir, Route, (_, _, _) => Task.CompletedTask).GetAwaiter().GetResult();
        _bus = _host.Services.GetRequiredService<IMessageBus>();
        _message = BenchSupport.MakeMessage(1, PayloadBytes);
    }

    [Benchmark]
    public async Task<PublishResult> PublishAsync()
        => await _bus.PublishAsync(Route, _message);

    [GlobalCleanup]
    public void Cleanup()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        BenchSupport.TryDelete(_storeDir);
    }
}
