using Microsoft.Extensions.DependencyInjection;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Default implementation of <see cref="IMessageBusDiagnostics"/>.
/// </summary>
internal sealed class MessageBusDiagnostics : IMessageBusDiagnostics
{
    private readonly IServiceProvider _serviceProvider;
    private readonly HermesRuntimeState _runtimeState;
    private readonly HermesReadiness _readiness;

    public MessageBusDiagnostics(
        IServiceProvider serviceProvider,
        HermesRuntimeState runtimeState,
        HermesReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(runtimeState);
        ArgumentNullException.ThrowIfNull(readiness);

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

    public int GetBacklogCount<T>()
    {
        // Durable backlog: work that still needs doing, from the store — NOT Channel enqueue/
        // dequeue counters. Dead letters are excluded (they are terminal until explicit replay).
        var store = _serviceProvider.GetService<IMessageStore<T>>();
        if (store is null)
        {
            return 0;
        }

        var stats = store.GetStats();
        return stats.PendingCount + stats.ProcessingCount + stats.RetryScheduledCount;
    }

    public MessageStoreStats? GetStoreStats<T>()
    {
        var store = _serviceProvider.GetService<IMessageStore<T>>();
        return store?.GetStats();
    }
}
