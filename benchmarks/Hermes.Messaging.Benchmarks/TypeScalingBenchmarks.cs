using BenchmarkDotNet.Attributes;
using Hermes.Messaging.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Messaging.Benchmarks;

/// <summary>
/// Measures host startup as a function of the number of registered message types, exercising the
/// current one-hosted-service-per-message-type model (1/10/50/100 types). Registration is done via
/// reflection over pre-declared concrete types so each generic subscription is a distinct closed type.
/// </summary>
[MemoryDiagnoser]
public class TypeScalingBenchmarks
{
    [Params(1, 10, 50, 100)]
    public int TypeCount;

    private IHost _host = null!;
    private string _storeDir = null!;

    private static readonly System.Reflection.MethodInfo AddSubscriptionOpen =
        typeof(TypeScalingBenchmarks).GetMethod(nameof(AddOne), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static void AddOne<T>(IServiceCollection services)
        => services.AddChannelSubscription<T>($"scale/{typeof(T).Name}", (_, _, _) => Task.CompletedTask);

    [IterationSetup]
    public void IterationSetup()
    {
        _storeDir = BenchSupport.NewTempStore();
        var types = ScaleTypes.All.Take(TypeCount).ToArray();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHermesMessaging(opts => opts.PersistenceBasePath = _storeDir);
                foreach (var t in types)
                {
                    AddSubscriptionOpen.MakeGenericMethod(t).Invoke(null, [services]);
                }
            })
            .Build();
    }

    /// <summary>Measures the cost of starting the host with <see cref="TypeCount"/> subscriptions.</summary>
    [Benchmark]
    public void StartHost()
    {
        _host.StartAsync().GetAwaiter().GetResult();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        BenchSupport.TryDelete(_storeDir);
    }
}
