using System.Collections.Concurrent;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Circuit breaker to prevent cascading failures when downstream services are unavailable.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly ConcurrentDictionary<string, CircuitState> _circuits = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;

    public CircuitBreaker(
        int failureThreshold = 5,
        TimeSpan? openDuration = null)
    {
        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromMinutes(1);
    }

    public bool IsOpen(string route)
    {
        return GetState(route) == CircuitStateEnum.Open;
    }

    public void RecordSuccess(string route)
    {
        _circuits.AddOrUpdate(
            route,
            _ => new CircuitState(CircuitStateEnum.Closed, 0),
            (_, state) =>
            {
                var resolved = ResolveState(state);

                return resolved.State switch
                {
                    // Success in half-open → close circuit
                    CircuitStateEnum.HalfOpen => new CircuitState(CircuitStateEnum.Closed, 0),
                    // Reset failure count on success while closed
                    CircuitStateEnum.Closed when resolved.FailureCount > 0 => resolved with { FailureCount = 0 },
                    _ => resolved
                };
            });
    }

    public void RecordFailure(string route)
    {
        _circuits.AddOrUpdate(
            route,
            _ => new CircuitState(CircuitStateEnum.Closed, 1),
            (_, state) =>
            {
                var resolved = ResolveState(state);

                if (resolved.State == CircuitStateEnum.HalfOpen)
                {
                    // Failure in half-open → reopen circuit
                    return new CircuitState(
                        CircuitStateEnum.Open,
                        0,
                        OpenedUntil: DateTimeOffset.UtcNow.Add(_openDuration));
                }

                var newFailureCount = resolved.FailureCount + 1;
                if (newFailureCount >= _failureThreshold)
                {
                    return new CircuitState(
                        CircuitStateEnum.Open,
                        newFailureCount,
                        OpenedUntil: DateTimeOffset.UtcNow.Add(_openDuration));
                }

                return resolved with { FailureCount = newFailureCount };
            });
    }

    public CircuitStateEnum GetState(string route)
    {
        if (!_circuits.TryGetValue(route, out var state))
        {
            return CircuitStateEnum.Closed;
        }

        return ResolveState(state).State;
    }

    /// <summary>
    /// Materializes the Open → HalfOpen transition when the open duration has expired.
    /// </summary>
    private static CircuitState ResolveState(CircuitState state)
    {
        if (state.State == CircuitStateEnum.Open && DateTimeOffset.UtcNow >= state.OpenedUntil)
        {
            return new CircuitState(CircuitStateEnum.HalfOpen, 0);
        }

        return state;
    }

    private record CircuitState(
        CircuitStateEnum State,
        int FailureCount,
        DateTimeOffset? OpenedUntil = null);
}

public enum CircuitStateEnum
{
    Closed,   // Normal operation
    Open,     // Circuit is open, requests are blocked
    HalfOpen  // Testing if service is back
}

/// <summary>
/// Thrown when a message dispatch is blocked because the circuit breaker is open for the target route.
/// </summary>
public sealed class CircuitBreakerOpenException : InvalidOperationException
{
    public CircuitBreakerOpenException(string message) : base(message) { }
}
