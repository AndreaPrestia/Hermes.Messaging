namespace Hermes.Messaging;

/// <summary>
/// Explicit runtime state of the Hermes bus within a single process.
/// </summary>
public enum RuntimeState
{
    /// <summary>Constructed but not yet started.</summary>
    Created = 0,

    /// <summary>Startup in progress (store open/validate, recovery, worker start).</summary>
    Starting = 1,

    /// <summary>Fully started; publishing is allowed.</summary>
    Ready = 2,

    /// <summary>Shutdown in progress; new publishes are rejected.</summary>
    Stopping = 3,

    /// <summary>Fully stopped.</summary>
    Stopped = 4,

    /// <summary>Startup failed; the bus is not usable.</summary>
    Faulted = 5
}

/// <summary>
/// Thread-safe holder for the process-wide Hermes runtime state. Registered as a singleton.
/// </summary>
/// <remarks>
/// Publish is permitted only in <see cref="RuntimeState.Ready"/>. The state is advanced by the
/// <see cref="HermesLifecycle"/> hosted service.
/// </remarks>
internal sealed class HermesRuntimeState
{
    private int _state = (int)RuntimeState.Created;

    /// <summary>Gets the current runtime state.</summary>
    public RuntimeState Current => (RuntimeState)Volatile.Read(ref _state);

    /// <summary>True only when publishing is currently allowed.</summary>
    public bool IsReady => Current == RuntimeState.Ready;

    /// <summary>Unconditionally sets the state.</summary>
    public void Set(RuntimeState state) => Volatile.Write(ref _state, (int)state);

    /// <summary>
    /// Atomically transitions from <paramref name="expected"/> to <paramref name="next"/>.
    /// Returns true if the transition was applied.
    /// </summary>
    public bool TryTransition(RuntimeState expected, RuntimeState next)
        => Interlocked.CompareExchange(ref _state, (int)next, (int)expected) == (int)expected;

    /// <summary>
    /// Throws <see cref="HermesNotReadyException"/> if the bus is not in <see cref="RuntimeState.Ready"/>.
    /// </summary>
    public void EnsureReady()
    {
        var current = Current;
        if (current != RuntimeState.Ready)
        {
            throw new HermesNotReadyException(current);
        }
    }
}

/// <summary>
/// Thrown when a publish is attempted while the bus is not in the Ready state.
/// </summary>
public sealed class HermesNotReadyException : InvalidOperationException
{
    public HermesNotReadyException(RuntimeState state)
        : base($"Hermes is not ready to accept publishes (current state: {state}).")
    {
        State = state;
    }

    public RuntimeState State { get; }
}
