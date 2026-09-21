namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Provides diagnostics and health information for the message bus.
/// Injected separately from <see cref="IMessageBus"/> to avoid polluting the publish API.
/// </summary>
public interface IMessageBusDiagnostics
{
    /// <summary>
    /// Liveness: true when the core bus is resolvable and the runtime has not faulted.
    /// </summary>
    bool IsHealthy { get; }

    /// <summary>
    /// Readiness: true only when the runtime is <see cref="RuntimeState.Ready"/>, the durable
    /// store is initialized, and startup recovery has completed for all subscribers (SDD 09).
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Gets the current circuit breaker state for a specific message type and route.
    /// </summary>
    /// <typeparam name="T">Message payload type.</typeparam>
    /// <param name="route">The route path.</param>
    CircuitStateEnum GetCircuitState<T>(string route);

    /// <summary>
    /// Gets the approximate number of messages waiting to be processed across all routes
    /// for a specific message type.
    /// </summary>
    /// <typeparam name="T">Message payload type.</typeparam>
    int GetBacklogCount<T>();

    /// <summary>
    /// Gets persistent store statistics for a specific message type.
    /// Returns null if the store is not available (e.g., type has no subscriptions).
    /// </summary>
    /// <typeparam name="T">Message payload type.</typeparam>
    MessageStoreStats? GetStoreStats<T>();
}
