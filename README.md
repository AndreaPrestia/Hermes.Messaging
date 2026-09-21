# Hermes.Messaging

An **in-process, single-process** durable message bus for .NET built on `System.Threading.Channels` with persistent storage (LiteDB): a durable state machine with crash recovery, persistent retry with jittered backoff, a durable dead-letter queue, explicit runtime lifecycle, and OpenTelemetry-compatible metrics and tracing.

> **Persist-before-signal durability.** `PublishAsync` returns an accepted `PublishResult` only after the message has been durably committed. The durable store is the source of truth; the in-memory channel is only a wake-up/acceleration signal — a lost signal never loses an accepted message.
>
> **Delivery semantics:** at-least-once within the single-process boundary. Duplicates are possible; this is **not** exactly-once. Not a distributed broker and not a multi-process/multi-node queue.

---

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Getting Started](#getting-started)
- [Publishing Messages](#publishing-messages)
- [Subscribing to Messages](#subscribing-to-messages)
- [Configuration](#configuration)
- [Message Lifecycle](#message-lifecycle)
- [Retry Semantics](#retry-semantics)
- [Dead Letter Queue](#dead-letter-queue)
- [Diagnostics & Monitoring](#diagnostics--monitoring)
- [Project Structure](#project-structure)

---

## Architecture Overview

```
Publisher (only when runtime is Ready)         Subscriber (BackgroundService)
   │                                                    │
   ▼                                                    ▼
IMessageBus.PublishAsync()                ┌─ PersistentChannelRouterSubscriber<T> ─┐
   │ 1. validate route                    │  startup: RecoverInterrupted()          │
   │ 2. Persist LiteDB (Pending)  ◄── source of truth   (Processing -> Pending)     │
   │ 3. commit                            │  fixed worker loops:                    │
   │ 4. best-effort signal ─────────────► │   - TryClaim (atomic) -> Processing     │
   │ 5. return PublishResult (Accepted)   │   - dispatch once                       │
   ▼                                      │   - Complete / ScheduleRetry / DeadLetter│
Channel<ChannelMessage<T>>  ············► │  reconciliation loop re-signals due work │
   (wake-up/acceleration only)           └──────────────────────────────────────────┘
                                                    │ (on dead-letter)
                                                    ▼
                                          Durable DeadLettered state
                                          (IDeadLetterAdministration<T>:
                                           List/Get/Replay/Delete/Purge)
```

**Key design decisions:**

- **Durable store is the source of truth.** The bounded channel is only a wake-up signal; a lost signal is recovered by the reconciliation loop.
- **Atomic claim** (`TryClaim`) makes duplicate signals harmless — a message is processed by exactly one worker at a time.
- **Durable state machine:** `Pending → Processing → Completed`, `Processing → RetryScheduled → Processing`, `Processing → DeadLettered`, explicit `DeadLettered → Pending` replay. Interrupted `Processing` is recovered to `Pending` on startup.
- **Persistent retry** with bounded exponential backoff + jitter (`TimeProvider`); workers are never held by a retry delay.
- **Durable DLQ** managed via `IDeadLetterAdministration<T>` (inspection is non-destructive; replay/delete/purge are explicit). The observer hook cannot delete the durable record.
- **Explicit lifecycle** — publishing is allowed only in `Ready`; shutdown rejects new publishes first, drains in-flight up to a grace period, and leaves the backlog durable for restart.
- **Fixed worker loops** — no per-message `Task.Run`.
- **Bounded reconciliation** — reconciliation (and startup seeding) queries only a bounded number of due `MessageId`s based on the currently available wake-up capacity, rather than materializing the entire due backlog. This reduces avoidable allocations and store scans for large backlogs; the durable store remains the source of truth and any work not signalled this cycle is picked up by a later reconciliation as workers make progress.

### Observability

Metrics and traces use the stable name `Hermes.Messaging`:

- **Metrics (Meter `Hermes.Messaging`):** counters `messages.published`, `messages.persisted`, `publish.failed`, `signal.missed`, `processing.started/succeeded/failed/retried/deadlettered`; histograms `publish.duration`, `processing.duration`; gauges `messages.pending`, `messages.processing`, `messages.retry_scheduled`, `deadletters.depth`. Tags are low-cardinality (`message_type`, `route`, `outcome`) — never MessageId/CorrelationId.
- **Tracing (ActivitySource `Hermes.Messaging`):** `Publish` and `Process` spans.
- **Health:** `IMessageBusDiagnostics.IsHealthy` (liveness) and `IsReady` (runtime `Ready` + startup recovery complete).

---

## Getting Started

### 1. Register the message bus

```csharp
builder.Services.AddHermesMessaging(options =>
{
    options.DefaultChannelCapacity = 10_000;
    options.MaxAttempts = 3;               // total handler invocations before dead-lettering
    options.InitialRetryDelayMs = 100;
    options.MaxConcurrency = 4;            // fixed worker loops per message type
    options.ShutdownGracePeriod = TimeSpan.FromSeconds(30);
    options.PersistenceBasePath = "/data/hermes"; // optional, defaults to LocalApplicationData
});
```

### 2. Subscribe to a route

```csharp
builder.Subscribe<OrderCreatedEvent>(
    "orders/created",
    async (message, sp, ct) =>
    {
        var handler = sp.GetRequiredService<OrderCreatedHandler>();
        await handler.HandleAsync(message, ct);
    },
    options =>
    {
        options.Capacity = 5000;
        options.FullMode = BoundedChannelFullMode.Wait;
    });
```

`Subscribe<T>` returns a `ChannelSubscriptionBuilder<T>` for optional fluent configuration — for example, registering a dead-letter handler:

```csharp
builder.Subscribe<OrderCreatedEvent>("orders/created", OrderCreatedHandler.HandleAsync)
    .WithDeadLetterHandler<OrderDeadLetterHandler>();
```

### 3. Publish a message

```csharp
public class OrderService(IMessageBus bus)
{
    public async Task CreateOrderAsync(Order order, CancellationToken ct)
    {
        // ... create order ...

        PublishResult result = await bus.PublishAsync(
            "orders/created",
            new OrderCreatedEvent(order.Id),
            options: new PublishOptions { CorrelationId = order.CorrelationId },
            cancellationToken: ct);

        // A returned result means the message is already durably committed.
        _log.LogInformation("Accepted {MessageId} at {AcceptedAt}", result.MessageId, result.AcceptedAt);
    }
}
```

---

## Publishing Messages

Inject `IMessageBus` and call `PublishAsync<T>`. The signature is:

```csharp
ValueTask<PublishResult> PublishAsync<T>(
    string route,
    T message,
    PublishOptions? options = null,
    CancellationToken cancellationToken = default);
```

- **Persist-before-signal:** the message is durably committed to the store *before* the call
  returns. A returned `PublishResult` means it is already accepted; a lost in-memory signal never
  loses it (the reconciliation loop recovers it).
- **`PublishResult`** exposes `MessageId` (unique), `CorrelationId` (non-unique), and `AcceptedAt`.
- **`PublishOptions`** lets you supply a `CorrelationId`; if omitted, a UUIDv7 is generated.
- **Route is validated first** — publishing to an unknown route throws `RouteNotFoundException`
  and persists nothing.
- **Readiness gate:** publishing before startup recovery completes, or after shutdown has begun,
  throws `HermesNotReadyException`.
- **Cancellation** can prevent acceptance only *before* the durable commit; once committed the
  accepted result is returned even if the token is then cancelled.

> `options` is the **third** positional parameter and `cancellationToken` is the fourth. Do not
> pass a `CancellationToken` as the third argument.

---

## Subscribing to Messages

### Route Registration

Each subscription maps a **route path** to a **handler delegate** for a specific message type `T`.

The recommended API is `Subscribe<T>`, which returns a `ChannelSubscriptionBuilder<T>` for fluent configuration:

```csharp
// On IHostApplicationBuilder
builder.Subscribe<T>(path, handler);
builder.Subscribe<T>(path, handler, channelOptions);

// On IServiceCollection
services.AddSubscription<T>(path, handler);
services.AddSubscription<T>(path, handler, channelOptions);
```

The service-collection form is also available directly (used by `AddSubscription` internally):

```csharp
services.AddChannelSubscription<T>(path, handler);
services.AddChannelSubscription<T>(path, handler, channelOptions);
```

- Routes are **case-insensitive**.
- A route can only be registered **once** per message type — duplicates throw `InvalidOperationException` at startup.
- Publishing to a route with **no handler** throws `RouteNotFoundException` at publish time and
  persists nothing. (If a route is removed after a message was already accepted, that message is
  dead-lettered without retries.)

### `ChannelSubscriptionBuilder<T>`

The builder returned by `Subscribe<T>` / `AddSubscription<T>` supports the following fluent methods:

| Method | Description |
|--------|-------------|
| `.WithDeadLetterHandler<THandler>()` | Registers an `IDeadLetterHandler<T>` (scoped) that the `DeadLetterQueueProcessor` invokes when a message fails all retries. |

```csharp
builder.Subscribe<OrderCreatedEvent>(
        "orders/created",
        OrderCreatedHandler.HandleAsync,
        options => { options.Capacity = 5000; })
    .WithDeadLetterHandler<OrderDeadLetterHandler>();
```

The channel subscription is registered **immediately** when `Subscribe<T>` is called — there is no terminal `Build()` method. The builder simply adds optional features on top.

### What Gets Registered Automatically

Calling `Subscribe<T>` (or `AddChannelSubscription<T>`) auto-registers all required infrastructure for type `T`. As of `0.4.0-alpha` **all of these except `IDeadLetterAdministration<T>` are `internal` implementation types** — they are wired up for you and are **not** supported extension points. Consumers interact through the public surface only (see [Public API stability](#public-api-stability)).

| Component | Visibility | Lifetime | Purpose |
|-----------|-----------|----------|---------|
| `ChannelRegistry` | internal | Singleton | Bounded channel pool, one per `T` |
| `ChannelRouteTable<T>` | internal | Singleton | Route → handler mapping |
| `DeadLetterQueue<T>` | internal | Singleton | Best-effort dead-letter observer channel |
| `DeadLetterQueueRegistry` | internal | Singleton | Type-keyed registry of all DLQ observers |
| `PersistentMessageStore<T>` (as `IMessageStore<T>`) | internal | Singleton | LiteDB durable store (source of truth) |
| `IDeadLetterAdministration<T>` | **public** | Singleton | Durable DLQ admin: List/Get/Replay/Delete/Purge |
| `PersistentChannelRouterSubscriber<T>` | internal | Hosted Service | Fixed worker loops: claim → dispatch |

---

## Configuration

### `MessageBusOptions`

| Property | Default | Description |
|----------|---------|-------------|
| `DefaultChannelCapacity` | `10,000` | Max messages buffered per type in the wake-up channel |
| `MaxAttempts` | `3` | Total handler invocations (including the first) before dead-lettering |
| `InitialRetryDelayMs` | `100` | First retry backoff (exponential, jittered, capped at 30s) |
| `MaxConcurrency` | `1` | Number of fixed async worker loops per type |
| `ShutdownGracePeriod` | `30s` | Max wait for in-flight handlers during graceful shutdown |
| `PersistenceBasePath` | `%LocalAppData%/Hermes/Messaging` | Directory for LiteDB files. One `.db` file per message type. |

### Custom Channel Options

Override wake-up channel settings per subscription:

```csharp
builder.Subscribe<T>("path", handler, options =>
{
    options.Capacity = 50_000;
});
```

---

## Message Lifecycle

```
Pending ──(TryClaim)─► Processing ──► Completed
   ▲                     │
   │                     ├──► RetryScheduled ──(due)──► Processing
   │                     └──► DeadLettered
   └──(explicit replay)──┘  ← DeadLettered

startup: Processing left by a crash ──► Pending
```

| Status | When | Durable |
|--------|------|:---:|
| **Pending** | Durably accepted, awaiting a worker claim | ✅ |
| **Processing** | Claimed by a worker; being handled | ✅ (recovered to Pending on restart) |
| **RetryScheduled** | Attempt failed; scheduled for a future retry (`NextAttemptAt`) | ✅ (survives restart) |
| **Completed** | Handler returned successfully | ✅ (cleaned up after 7 days) |
| **DeadLettered** | Attempts exhausted or non-retryable failure | ✅ (retained until explicit Delete/Purge) |

> The ambiguous `Failed` state was retired. Old records carrying it are recovered to `Pending`.

### Crash Recovery

On startup (synchronously, before the runtime becomes publishable), each subscriber:

1. `RecoverInterrupted()` — moves any `Processing` (or retired `Failed`) records back to `Pending`.
2. Seeds the wake-up channel with all due work (`Pending` + due `RetryScheduled`), covering signals lost before startup.
3. Marks its startup recovery complete; publishing is rejected until every subscriber has done so.

A periodic reconciliation loop re-scans due work, so a lost in-memory signal is always recovered.

---

## Retry Semantics

- **Attempts:** `MaxAttempts` counts total handler invocations, including the first. `MaxAttempts = 1`
  dead-letters after the first failure (no retries); `MaxAttempts = 3` allows exactly three.
- **Durable backoff:** on a retryable failure the message becomes `RetryScheduled` with a
  `NextAttemptAt` computed via bounded exponential backoff + full jitter (uses `TimeProvider`).
  A worker is **never** held on a retry delay — the reconciliation loop re-signals when due.
- **Classification (`RetryClassifier`):**
  - non-retryable (`NonRetryableException`, `RouteNotFoundException`, `ArgumentException`,
    `NotSupportedException`, `NotImplementedException`) → dead-letter immediately;
  - cancellation during shutdown → not a failure (left durable for restart);
  - everything else → retry until `MaxAttempts`, then dead-letter.

> The legacy per-route circuit breaker was **removed from core** in 0.2.0-alpha. Resilience for
> flaky downstream dependencies belongs in the handler or its dependency clients.

---

## Dead Letter Queue

The dead-letter queue is **durable**: `DeadLettered` is a persistent store state and is the source
of truth. The in-memory `DeadLetterQueue<T>` channel is only a best-effort **observer** hook.

- Dead-lettering writes the durable record **first**; if the observer channel is full the durable
  record is still retained (only the notification is dropped).
- Dead letters are **not** auto-cleaned by retention — they persist until an explicit delete/purge.
- Administration is via `IDeadLetterAdministration<T>` (inspection is non-destructive):

```csharp
public sealed class DlqAdmin(IDeadLetterAdministration<OrderCreatedEvent> dlq)
{
    public IReadOnlyList<DeadLetterEntry<OrderCreatedEvent>> List() => dlq.List(skip: 0, take: 100);
    public bool Replay(Guid messageId) => dlq.Replay(messageId); // -> Pending, fresh retry budget
    public bool Delete(Guid messageId) => dlq.Delete(messageId);
    public int Purge() => dlq.Purge();
}
```

> **Replay resets the retry budget:** a replayed message returns to `Pending` with
> `AttemptCount = 0` and a cleared error, so it gets a full set of attempts again.

The `DeadLetterQueueProcessor` (single `BackgroundService`) drains the observer channels every 5
seconds and invokes any registered `IDeadLetterHandler<T>`. An observer exception or a full observer
channel can never delete the durable record.

### Custom DLQ Handler

Register an `IDeadLetterHandler<T>` via the subscription builder to process dead-lettered messages:

```csharp
builder.Subscribe<OrderCreatedEvent>("orders/created", OrderCreatedHandler.HandleAsync)
    .WithDeadLetterHandler<OrderDlqHandler>();

public class OrderDlqHandler : IDeadLetterHandler<OrderCreatedEvent>
{
    public Task HandleAsync(DeadLetterMessage<OrderCreatedEvent> deadLetter, CancellationToken ct)
    {
        // Alert, persist to external store, compensate, etc.
        logger.LogError(deadLetter.Exception,
            "Order {Path} failed {Attempts} times. CorrelationId: {Id}",
            deadLetter.Path, deadLetter.Attempts, deadLetter.CorrelationId);
        return Task.CompletedTask;
    }
}
```

The handler is resolved as **Scoped** from the DI container, so it can inject scoped services (e.g., DbContext).

> You can also register a handler manually: `services.AddScoped<IDeadLetterHandler<T>, THandler>()`.

If no handler is registered, the processor logs a warning and drops the message.

---

## Diagnostics & Monitoring

### `IMessageBusDiagnostics`

Inject to query runtime state:

```csharp
public class HealthController(IMessageBusDiagnostics diag)
{
    public IResult GetHealth()
    {
        var live    = diag.IsHealthy;                          // liveness
        var ready   = diag.IsReady;                            // Ready + recovery complete
        var backlog = diag.GetBacklogCount<OrderCreatedEvent>(); // durable: Pending+Processing+RetryScheduled
        var stats   = diag.GetStoreStats<OrderCreatedEvent>();   // full durable counts

        return Results.Ok(new { live, ready, backlog, stats });
    }
}
```

- **`IsHealthy`** (liveness): the bus is resolvable and the runtime has not `Faulted`.
- **`IsReady`** (readiness): runtime is `Ready` **and** startup recovery is complete for all
  subscribers. This matches publishability exactly.
- **`GetBacklogCount<T>()`** returns the **durable** backlog (`Pending + Processing + RetryScheduled`)
  from the store — not in-memory channel counters. Dead letters are excluded.

### OpenTelemetry Metrics & Tracing

Durable-state metrics and traces use the stable name **`Hermes.Messaging`** (both the meter and the
`ActivitySource`). Tags are low-cardinality only (`message_type`, `route`, `outcome`) — MessageId and
CorrelationId are never used as labels.

| Instrument | Type | Description |
|------------|------|-------------|
| `messages.published` / `messages.persisted` | Counter | Accepted / durably committed |
| `publish.failed` / `signal.missed` | Counter | Persist failure / dropped wake-up |
| `processing.started/succeeded/failed/retried/deadlettered` | Counter | Processing outcomes |
| `publish.duration` / `processing.duration` | Histogram (ms) | Latencies |
| `messages.pending` / `messages.processing` / `messages.retry_scheduled` | Gauge | Durable backlog by state |
| `deadletters.depth` | Gauge | Durable dead-letter count |

A separate internal meter also emits volatile channel/dispatch counters (`messagebus.*`) describing
the acceleration layer only; these are not the durable backlog.

Tracing emits `Publish` and `Process` spans on the `Hermes.Messaging` `ActivitySource`.

---

## Project Structure

```
Hermes.Messaging/
├── Domain/
│   ├── Entities/
│   │   ├── ChannelMessage.cs          # Envelope: Path + Body + CorrelationId
│   │   └── PersistedMessage.cs        # LiteDB entity + MessageStatus enum
│   └── Interfaces/
│       └── IDeadLetterQueue.cs        # Non-generic interface for polymorphic DLQ access
│
└── Infrastructure/
    ├── IMessageBus.cs                 # Core publish interface (ValueTask<PublishResult>)
    ├── PublishOptions.cs / PublishResult.cs
    ├── IMessageBusDiagnostics.cs      # Liveness/readiness/backlog/stats
    ├── InMemoryMessageBus.cs          # Publish: gate → validate → persist → signal → accept
    ├── MessageBusDiagnostics.cs       # IMessageBusDiagnostics implementation
    ├── DependencyInjection.cs         # AddHermesMessaging() + MessageBusOptions
    ├── HermesRuntimeState.cs          # RuntimeState + HermesNotReadyException
    ├── HermesLifecycle.cs             # Runtime lifecycle hosted service
    ├── HermesReadiness.cs             # Per-type startup-recovery tracking
    ├── HermesTelemetry.cs             # ActivitySource + durable meters (Hermes.Messaging)
    ├── HermesStoreMetrics.cs          # Durable-state observable gauges
    ├── TypeIdentity.cs                # Stable FullName-based internal identity keys
    ├── RetryClassification.cs         # RetryClassifier + NonRetryableException
    ├── IMessageStore.cs               # Durable store abstraction (source of truth)
    ├── ChannelRegistry.cs             # Wake-up channel pool (one per T)
    ├── ChannelRouteTable.cs           # Route → handler dispatch + RouteNotFoundException
    ├── ChannelRouteRegistration.cs    # DI-time route wiring
    ├── ChannelSubscriptionExtensions.cs / ChannelSubscriptionBuilder.cs
    ├── ChannelMetrics.cs              # Volatile channel/dispatch metrics (acceleration layer)
    ├── PersistentMessageStore.cs      # LiteDB IMessageStore<T> implementation
    ├── PersistentChannelRouterSubscriber.cs  # Fixed worker loops: claim → dispatch → complete/retry/dead-letter
    ├── DeadLetterAdministration.cs    # IDeadLetterAdministration<T> + DeadLetterEntry<T>
    ├── DeadLetterQueue.cs             # Best-effort observer channel + DeadLetterMessage<T>
    ├── DeadLetterQueueRegistry.cs     # Type-keyed registry of observer channels
    └── DeadLetterQueueProcessor.cs    # BackgroundService: drains observer channels → IDeadLetterHandler<T>
```

---

## Dependencies

| Package | Purpose |
|---------|---------|
| `LiteDB` | Embedded database for durable message persistence |
| `Microsoft.Extensions.Hosting.Abstractions` | `BackgroundService`, `IHostedService`, `IHostApplicationBuilder` |

No external message broker required. The entire bus runs in-process.

---

## When *not* to use Hermes

Hermes is deliberately a single-process, in-process durable bus. Do **not** use it for:

- **Multi-process consumers** — the durable store is owned by one process; there is no cross-process locking.
- **Multi-instance / horizontally scaled services** — each instance has its own store; there is no shared queue.
- **Distributed messaging / cross-service transport** — use RabbitMQ, Azure Service Bus, Kafka, etc.
- **Exactly-once processing** — delivery is at-least-once; duplicates are possible.
- **Durability on ephemeral storage** — if the persistence path is not durable (e.g. a container tmpfs), crash recovery guarantees do not hold.

For those scenarios, use a real broker. Hermes targets reliable in-process work within one service instance.

---

## Limitations

- Single process / single instance; at-least-once; duplicates possible; not exactly-once.
- Reconciliation runs on a fixed ~1s interval, which bounds the earliest effective retry.
- Persisted records carry a `SchemaVersion` (currently 1); cross-version migration is a future concern.

---

## Public API stability

The entire supported consumer API lives in a single namespace — a normal application needs only:

```csharp
using Hermes.Messaging;
```

Implementation types (the durable store, channel registries/route tables, hosted subscribers,
readiness/runtime-state holders, store telemetry registries, observer queues, and the persisted-entity
types) are `internal`. A consumer interacts with Hermes only through:

- **Registration / configuration:** `AddHermesMessaging`, `AddChannelSubscription<T>` / `Subscribe<T>` /
  `AddSubscription<T>`, `ChannelSubscriptionBuilder<T>.WithDeadLetterHandler<THandler>()`,
  `MessageBusOptions`. (Dead-letter infrastructure is registered automatically with each subscription —
  there is no separate `AddDeadLetterQueue<T>` call.)
- **Publishing:** `IMessageBus`, `PublishOptions`, `PublishResult`.
- **Handling / retry contract:** the handler delegate, `IDeadLetterHandler<T>`, `DeadLetterMessage<T>`,
  and `NonRetryableException` (throw it from a handler to dead-letter immediately).
- **Dead-letter administration:** `IDeadLetterAdministration<T>`, `DeadLetterEntry<T>`.
- **Diagnostics:** `IMessageBusDiagnostics` (`IsReady`, `IsHealthy`, `CurrentState`, `GetBacklogCount<T>`,
  `GetStoreStats<T>`), `MessageStoreStats`, `RuntimeState`. `GetStoreStats<T>()` returns `null` when no
  durable store is registered for `T` (i.e. `T` has no subscription).
- **Telemetry:** `HermesTelemetry` — exposes the stable `Name` and an `ActivitySource` consumers can
  subscribe to; the internal `Meter`/instruments are not public.
- **Exceptions that can escape:** `RouteNotFoundException`, `HermesNotReadyException`,
  `StoreSchemaMismatchException`.

**Supported extension points:** custom message handlers, custom `IDeadLetterHandler<T>` implementations,
and observing lifecycle/state via `IMessageBusDiagnostics`. **Not supported:** replacing the persistence
engine, resolving/instantiating any implementation type, or mutating runtime state directly — those are
internal and may change without notice.

Stability note: **`0.5.x` is the beta API line.** The intended consumer API is now tracked as the
shipped compatibility baseline ([`PublicAPI.Shipped.txt`](src/Hermes.Messaging/PublicAPI.Shipped.txt))
by `Microsoft.CodeAnalysis.PublicApiAnalyzers` (RS0016/RS0017 as errors), so accidental additions,
removals, or signature/nullability changes fail CI. Breaking public API changes during beta should be
exceptional, explicitly documented, and reflected in the API baseline (recorded in
`PublicAPI.Unshipped.txt`, then reconciled into `PublicAPI.Shipped.txt` on release). This is a beta
line, not a 1.0 stability guarantee. Implementation types remain internal and are not supported
extension points.
