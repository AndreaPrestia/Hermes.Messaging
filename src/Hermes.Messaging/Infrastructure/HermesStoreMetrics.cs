using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Hermes.Messaging;

/// <summary>
/// Registry of durable stores that publishes gauge measurements reflecting durable state
/// (pending / processing / retry-scheduled / dead-letter depth) per message type.
/// Registered as a singleton; each store registers a stats provider at construction.
/// Internal telemetry helper — not part of the intended public consumer surface
/// (see docs/api/public-api-review.md: INTERNALIZE-BEFORE-BETA).
/// </summary>
internal sealed class HermesStoreMetrics : IDisposable
{
    private readonly ConcurrentDictionary<string, Func<MessageStoreStats>> _providers = new();
    private readonly Meter _meter;

    public HermesStoreMetrics()
    {
        _meter = new Meter(HermesTelemetry.Name);
        _meter.CreateObservableGauge("messages.pending", () => Observe(s => s.PendingCount));
        _meter.CreateObservableGauge("messages.processing", () => Observe(s => s.ProcessingCount));
        _meter.CreateObservableGauge("messages.retry_scheduled", () => Observe(s => s.RetryScheduledCount));
        _meter.CreateObservableGauge("deadletters.depth", () => Observe(s => s.DeadLetteredCount));
    }

    /// <summary>Registers (or replaces) the stats provider for a message type.</summary>
    public void Register(string messageType, Func<MessageStoreStats> statsProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(statsProvider);
        _providers[messageType] = statsProvider;
    }

    private IEnumerable<Measurement<long>> Observe(Func<MessageStoreStats, int> selector)
    {
        foreach (var kvp in _providers)
        {
            MessageStoreStats stats;
            try { stats = kvp.Value(); }
            catch { continue; }

            yield return new Measurement<long>(selector(stats), new KeyValuePair<string, object?>("message_type", kvp.Key));
        }
    }

    public void Dispose() => _meter.Dispose();
}
