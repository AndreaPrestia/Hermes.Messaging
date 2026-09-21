using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Hermes.Messaging;

/// <summary>
/// Central telemetry for Hermes: a stable <see cref="System.Diagnostics.ActivitySource"/> and a
/// stable <see cref="System.Diagnostics.Metrics.Meter"/> named <c>Hermes.Messaging</c> exposing
/// durable-state counters, histograms and gauges (SDD 09).
/// </summary>
/// <remarks>
/// Tags are low-cardinality only (<c>message_type</c>, <c>route</c>, <c>outcome</c>). MessageId and
/// CorrelationId are never used as metric labels.
/// </remarks>
public static class HermesTelemetry
{
    /// <summary>Stable telemetry name for both the meter and the activity source.</summary>
    public const string Name = "Hermes.Messaging";

    /// <summary>Activity source for Publish/Process spans. Public so callers can subscribe.</summary>
    public static readonly ActivitySource ActivitySource = new(Name);

    internal static readonly Meter Meter = new(Name);

    // Counters
    internal static readonly Counter<long> MessagesPublished = Meter.CreateCounter<long>("messages.published");
    internal static readonly Counter<long> MessagesPersisted = Meter.CreateCounter<long>("messages.persisted");
    internal static readonly Counter<long> PublishFailed = Meter.CreateCounter<long>("publish.failed");
    internal static readonly Counter<long> SignalMissed = Meter.CreateCounter<long>("signal.missed");
    internal static readonly Counter<long> ProcessingStarted = Meter.CreateCounter<long>("processing.started");
    internal static readonly Counter<long> ProcessingSucceeded = Meter.CreateCounter<long>("processing.succeeded");
    internal static readonly Counter<long> ProcessingFailed = Meter.CreateCounter<long>("processing.failed");
    internal static readonly Counter<long> ProcessingRetried = Meter.CreateCounter<long>("processing.retried");
    internal static readonly Counter<long> ProcessingDeadLettered = Meter.CreateCounter<long>("processing.deadlettered");
    internal static readonly Counter<long> ProcessingReplayed = Meter.CreateCounter<long>("processing.replayed");

    // Histograms
    internal static readonly Histogram<double> PublishDuration = Meter.CreateHistogram<double>("publish.duration", unit: "ms");
    internal static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>("processing.duration", unit: "ms");

    internal static TagList Tags(Type messageType, string route) => new()
    {
        { "message_type", messageType.Name },
        { "route", route }
    };

    internal static TagList Tags(Type messageType, string route, string outcome) => new()
    {
        { "message_type", messageType.Name },
        { "route", route },
        { "outcome", outcome }
    };
}
