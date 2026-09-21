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
    /// Read-only observation of the current lifecycle <see cref="RuntimeState"/>. Consumers can
    /// observe the lifecycle but cannot mutate it — the state is advanced only by the Hermes
    /// runtime itself.
    /// </summary>
    RuntimeState CurrentState { get; }

    /// <summary>
    /// Gets the durable backlog for a message type: the number of messages that still require
    /// work, defined as <c>Pending + Processing + RetryScheduled</c>. Dead-lettered messages are
    /// NOT counted here. Returns 0 if the store is unavailable (e.g. no subscriptions).
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
