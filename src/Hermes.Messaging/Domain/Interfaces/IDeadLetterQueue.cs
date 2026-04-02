namespace Hermes.Messaging.Domain.Interfaces;

/// <summary>
/// Non-generic interface for dead letter queues to enable polymorphic handling.
/// </summary>
public interface IDeadLetterQueue
{
    Type MessageType { get; }
    Task<DeadLetterReadResult> TryReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of attempting to read from a dead letter queue.
/// </summary>
public sealed record DeadLetterReadResult(bool Success, object? Message);
