using System.Collections.Concurrent;
using Hermes.Messaging.Domain.Interfaces;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Thread-safe registry for all dead letter queues in the system.
/// Keyed by message type to prevent duplicate registrations.
/// </summary>
public sealed class DeadLetterQueueRegistry
{
    private readonly ConcurrentDictionary<Type, IDeadLetterQueue> _queues = new();

    public void Register(IDeadLetterQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        if (!_queues.TryAdd(queue.MessageType, queue))
        {
            // Already registered for this type — idempotent, no error.
        }
    }

    public IReadOnlyList<IDeadLetterQueue> GetAll() => _queues.Values.ToArray();
}
