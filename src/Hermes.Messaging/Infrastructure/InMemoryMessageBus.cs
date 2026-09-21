using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;

using Hermes.Messaging.Domain.Entities;

namespace Hermes.Messaging.Infrastructure;

/// <summary>
/// Durable in-process message bus.
/// </summary>
/// <remarks>
/// Publish sequence (see SDD 03 — Delivery Semantics):
/// <list type="number">
/// <item>validate runtime + route;</item>
/// <item>create envelope with a unique MessageId;</item>
/// <item>durably persist as Pending and commit;</item>
/// <item>best-effort signal to the channel;</item>
/// <item>return Accepted.</item>
/// </list>
/// A returned <see cref="PublishResult"/> means the durable commit already happened.
/// A lost channel signal never loses an accepted message — the durable store is the
/// source of truth and the message is replayed on restart.
/// </remarks>
public sealed class InMemoryMessageBus : IMessageBus
{
    private readonly ChannelRegistry _registry;
    private readonly IServiceProvider _services;

    public InMemoryMessageBus(ChannelRegistry registry, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);
        _registry = registry;
        _services = services;
    }

    public ValueTask<PublishResult> PublishAsync<T>(
        string route,
        T message,
        PublishOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(message);

        // 0. Publishing is only allowed while the runtime is Ready. Before startup completes and
        //    once shutdown has begun, new publishes are rejected (nothing is persisted).
        _services.GetService<HermesRuntimeState>()?.EnsureReady();

        // 1. Validate route BEFORE any durable acceptance.
        var routes = _services.GetService<ChannelRouteTable<T>>();
        if (routes is null || !routes.HasRoute(route))
        {
            throw new RouteNotFoundException(route, typeof(T));
        }

        // Only honour cancellation BEFORE the durable commit.
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Create the envelope with a unique MessageId and a (possibly caller-supplied,
        //    non-unique) CorrelationId.
        var correlationId = options?.CorrelationId ?? Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var messageId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var envelope = new ChannelMessage<T>(route, message, correlationId, messageId);

        using var activity = HermesTelemetry.ActivitySource.StartActivity("Publish", ActivityKind.Producer);
        activity?.SetTag("message_type", typeof(T).Name);
        activity?.SetTag("route", route);

        var startTs = Stopwatch.GetTimestamp();
        HermesTelemetry.MessagesPublished.Add(1, HermesTelemetry.Tags(typeof(T), route));

        // 3. Persist Pending and commit. If this throws, nothing is Accepted.
        var store = _services.GetRequiredService<IMessageStore<T>>();
        try
        {
            store.Insert(envelope);
        }
        catch
        {
            HermesTelemetry.PublishFailed.Add(1, HermesTelemetry.Tags(typeof(T), route));
            activity?.SetStatus(ActivityStatusCode.Error, "persist failed");
            throw;
        }

        HermesTelemetry.MessagesPersisted.Add(1, HermesTelemetry.Tags(typeof(T), route));

        var acceptedAt = DateTimeOffset.UtcNow;

        // 4. Best-effort signal to the channel. From here on the message is durably accepted:
        //    a lost or skipped signal must NOT turn an accepted message into a failure, and
        //    post-commit cancellation must NOT be surfaced. The message will be replayed from
        //    the durable store on restart if it is never dequeued.
        SignalBestEffort(envelope);

        HermesTelemetry.PublishDuration.Record(Stopwatch.GetElapsedTime(startTs).TotalMilliseconds, HermesTelemetry.Tags(typeof(T), route));

        var result = new PublishResult
        {
            MessageId = messageId,
            CorrelationId = correlationId,
            AcceptedAt = acceptedAt
        };

        return ValueTask.FromResult(result);
    }

    private void SignalBestEffort<T>(ChannelMessage<T> envelope)
    {
        try
        {
            var channel = _registry.GetOrCreate<T>();
            if (channel.Writer.TryWrite(envelope))
            {
                ChannelMetrics.RecordEnqueued(typeof(T), envelope.Path);
            }
            else
            {
                // The bounded channel is full; we deliberately do NOT block or fail. The durable
                // record already exists and the reconciliation loop will pick it up. Record the
                // missed signal so operators can see acceleration-path saturation.
                HermesTelemetry.SignalMissed.Add(1, HermesTelemetry.Tags(typeof(T), envelope.Path));
            }
        }
        catch
        {
            // Signalling is only an acceleration mechanism; never fail an accepted publish.
            HermesTelemetry.SignalMissed.Add(1, HermesTelemetry.Tags(typeof(T), envelope.Path));
        }
    }
}
