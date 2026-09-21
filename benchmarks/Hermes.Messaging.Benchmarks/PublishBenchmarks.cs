using BenchmarkDotNet.Attributes;
using Hermes.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Benchmarks;

/// <summary>
/// Measures the durable publish path: validation → LiteDB durable insert → best-effort signal →
/// PublishResult. The measured operation is the publish call itself.
/// <para>
/// A fresh host/store is created per BenchmarkDotNet iteration and deleted afterwards, so the LiteDB
/// file only ever holds the publishes of a single iteration (bounded) rather than growing across the
/// whole run. A no-op handler is registered purely so the subscriber can drain the wake-up channel;
/// draining does NOT shrink the durable store (Completed records persist until retention cleanup),
/// which is exactly why per-iteration isolation — not the handler — is what keeps the store bounded.
/// </para>
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

    [IterationSetup]
    public void IterationSetup()
    {
        _storeDir = BenchSupport.NewTempStore();
        _host = BenchSupport.StartHostAsync(_storeDir, Route, (_, _, _) => Task.CompletedTask).GetAwaiter().GetResult();
        _bus = _host.Services.GetRequiredService<IMessageBus>();
        _message = BenchSupport.MakeMessage(1, PayloadBytes);
    }

    [Benchmark]
    public async Task<PublishResult> PublishAsync()
        => await _bus.PublishAsync(Route, _message);

    [IterationCleanup]
    public void IterationCleanup()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        BenchSupport.TryDelete(_storeDir);
    }
}
