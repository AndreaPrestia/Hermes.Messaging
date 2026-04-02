using Microsoft.Extensions.DependencyInjection;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Default implementation of <see cref="IMessageBusDiagnostics"/>.
/// </summary>
internal sealed class MessageBusDiagnostics : IMessageBusDiagnostics
{
    private readonly CircuitBreaker _circuitBreaker;
    private readonly IServiceProvider _serviceProvider;

    public MessageBusDiagnostics(CircuitBreaker circuitBreaker, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(circuitBreaker);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        _circuitBreaker = circuitBreaker;
        _serviceProvider = serviceProvider;
    }

    public bool IsHealthy
    {
        get
        {
            // Healthy if the core bus is resolvable
            var bus = _serviceProvider.GetService<IMessageBus>();
            return bus is not null;
        }
    }

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
