using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Hermes.Messaging.Infrastructure;

internal static class ChannelMetrics
{
    private static readonly Meter Meter = new("Hermes.Messaging", "1.0.0");
    private static readonly Counter<long> EnqueuedCounter = Meter.CreateCounter<long>("messagebus.enqueued");
    private static readonly Counter<long> DroppedCounter = Meter.CreateCounter<long>("messagebus.dropped");
    private static readonly Counter<long> DispatchedCounter = Meter.CreateCounter<long>("messagebus.dispatched");
    private static readonly Counter<long> DispatchFailedCounter = Meter.CreateCounter<long>("messagebus.dispatch.failed");
    private static readonly Counter<long> RetryCounter = Meter.CreateCounter<long>("messagebus.dispatch.retried");
    private static readonly Histogram<double> DispatchDurationHistogram = Meter.CreateHistogram<double>("messagebus.dispatch.duration", unit: "ms");
    private static readonly ObservableGauge<long> BacklogGauge = Meter.CreateObservableGauge("messagebus.backlog", ObserveBacklog);

    private static readonly ConcurrentDictionary<(Type MessageType, string Path), long> Backlog = new();

    public static void RecordEnqueued(Type messageType, string path)
    {
        var key = (messageType, path);
        Backlog.AddOrUpdate(key, 1, (_, current) => current + 1);
        EnqueuedCounter.Add(1, CreateTags(messageType, path));
    }

    public static void RecordDropped(Type messageType, string path)
    {
        DroppedCounter.Add(1, CreateTags(messageType, path));
    }

    public static void RecordDequeued(Type messageType, string path)
    {
        var key = (messageType, path);
        Backlog.AddOrUpdate(key, 0, (_, current) => current > 0 ? current - 1 : 0);

        if (Backlog.TryGetValue(key, out var remaining) && remaining == 0)
        {
            Backlog.TryRemove(key, out _);
        }
    }

    public static void RecordDispatchSuccess(Type messageType, string path, double durationMilliseconds)
    {
        var tags = CreateTags(messageType, path);
        DispatchedCounter.Add(1, tags);
        DispatchDurationHistogram.Record(durationMilliseconds, tags);
    }

    public static void RecordDispatchFailure(Type messageType, string path, double durationMilliseconds)
    {
        var tags = CreateTags(messageType, path);
        DispatchFailedCounter.Add(1, tags);
        DispatchDurationHistogram.Record(durationMilliseconds, tags);
    }

    public static void RecordRetry(Type messageType, string path)
    {
        RetryCounter.Add(1, CreateTags(messageType, path));
    }

    /// <summary>
    /// Gets the total backlog count for a specific message type across all routes.
    /// </summary>
    public static int GetBacklog(Type messageType)
    {
        var total = 0L;
        foreach (var kvp in Backlog)
        {
            if (kvp.Key.MessageType == messageType && kvp.Value > 0)
            {
                total += kvp.Value;
            }
        }
        return (int)Math.Min(total, int.MaxValue);
    }

    private static IEnumerable<Measurement<long>> ObserveBacklog()
    {
        foreach (var kvp in Backlog)
        {
            if (kvp.Value <= 0)
            {
                continue;
            }

            var tags = CreateTags(kvp.Key.MessageType, kvp.Key.Path);
            yield return new Measurement<long>(kvp.Value, tags);
        }
    }

    private static TagList CreateTags(Type messageType, string path)
    {
        var tags = new TagList
        {
            { "message_type", messageType.Name },
            { "route", path }
        };

        return tags;
    }
}
