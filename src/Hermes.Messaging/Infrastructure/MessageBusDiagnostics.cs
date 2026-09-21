using Microsoft.Extensions.DependencyInjection;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Default implementation of <see cref="IMessageBusDiagnostics"/>.
/// </summary>
internal sealed class MessageBusDiagnostics : IMessageBusDiagnostics
{
    private readonly CircuitBreaker _circuitBreaker;
    private readonly IServiceProvider _serviceProvider;
    private readonly HermesRuntimeState _runtimeState;
    private readonly HermesReadiness _readiness;

    public MessageBusDiagnostics(
        CircuitBreaker circuitBreaker,
        IServiceProvider serviceProvider,
        HermesRuntimeState runtimeState,
        HermesReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(circuitBreaker);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(runtimeState);
        ArgumentNullException.ThrowIfNull(readiness);

        _circuitBreaker = circuitBreaker;
        _serviceProvider = serviceProvider;
        _runtimeState = runtimeState;
        _readiness = readiness;
    }

    public bool IsHealthy
    {
        get
        {
            // Liveness: core bus resolvable and runtime not faulted.
            var bus = _serviceProvider.GetService<IMessageBus>();
            return bus is not null && _runtimeState.Current != RuntimeState.Faulted;
        }
    }

    public bool IsReady => _runtimeState.IsReady && _readiness.RecoveryComplete;

    public CircuitStateEnum GetCircuitState<T>(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        var circuitKey = typeof(T).Name + ":" + route;
        return _circuitBreaker.GetState(circuitKey);
    }

    public int GetBacklogCount<T>()
    {
        return ChannelMetrics.GetBacklog(typeof(T));
    }

    public MessageStoreStats? GetStoreStats<T>()
    {
        var store = _serviceProvider.GetService<PersistentMessageStore<T>>();
        return store?.GetStats();
    }
}
