using System.Collections.Concurrent;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Tracks startup-recovery completion per message type. Registered as a singleton.
/// </summary>
/// <remarks>
/// Readiness (for liveness/readiness probes) requires the runtime to be <see cref="RuntimeState.Ready"/>
/// AND every registered subscriber to have completed its startup recovery (SDD 09).
/// </remarks>
internal sealed class HermesReadiness
{
    private readonly ConcurrentDictionary<string, bool> _recovered = new();

    /// <summary>Registers a subscriber as expected before readiness can be reported.</summary>
    public void Expect(string messageType) => _recovered.TryAdd(messageType, false);

    /// <summary>Marks a subscriber's startup recovery as complete.</summary>
    public void MarkRecovered(string messageType) => _recovered[messageType] = true;

    /// <summary>True when all expected subscribers have completed startup recovery.</summary>
    public bool RecoveryComplete => _recovered.IsEmpty || _recovered.Values.All(v => v);
}
