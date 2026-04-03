# Hermes.Messaging

An in-process message bus built on `System.Threading.Channels` with persistent storage (LiteDB), automatic retry, circuit breaking, dead letter queues, and OpenTelemetry-compatible metrics.

> **Persistence is always enabled.** Every message is written to disk before processing and replayed automatically after a crash.

---

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Getting Started](#getting-started)
- [Publishing Messages](#publishing-messages)
- [Subscribing to Messages](#subscribing-to-messages)
- [Configuration](#configuration)
- [Message Lifecycle](#message-lifecycle)
- [Retry & Circuit Breaker](#retry--circuit-breaker)
- [Dead Letter Queue](#dead-letter-queue)
- [Diagnostics & Monitoring](#diagnostics--monitoring)
- [Project Structure](#project-structure)

---

## Architecture Overview

```
Publisher                         Subscriber (BackgroundService)
   │                                       │
   ▼                                       ▼
IMessageBus.PublishAsync()     ┌─── PersistentChannelRouterSubscriber<T> ───┐
   │                           │                                            │
   ▼                           │  1. Persist to LiteDB (Pending)            │
Channel<ChannelMessage<T>>  ──►│  2. Dispatch via ChannelRouteTable<T>      │
   (bounded, backpressure)     │  3. Retry with exponential backoff         │
                               │  4. Circuit breaker per route              │
                               │  5. Mark Completed / DeadLettered          │
                               └────────────────────────────────────────────┘
                                       │ (on failure after all retries)
                                       ▼
                               DeadLetterQueue<T>
                                       │
                                       ▼
                               DeadLetterQueueProcessor
                               (dispatches to IDeadLetterHandler<T>)
```

**Key design decisions:**

- **One channel per message type `T`** — bounded, with backpressure (`FullMode = Wait`).
- **One `BackgroundService` per message type** — reads from the channel, persists, dispatches.
- **Route-based dispatch** — a single channel can serve multiple routes (e.g., `"orders/created"`, `"orders/cancelled"`), each with its own handler.
- **Crash recovery** — on startup, pending messages from the LiteDB store are replayed before the channel reader starts.

---

## Getting Started

### 1. Register the message bus

```csharp
builder.Services.AddHermesMessageBus(options =>
{
    options.DefaultChannelCapacity = 10_000;
    options.MaxRetryAttempts = 3;
    options.InitialRetryDelayMs = 100;
    options.MaxConcurrency = 4;           // parallel handlers per message type
    options.PersistenceBasePath = "/data/messagebus"; // optional, defaults to LocalApplicationData
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

        await bus.PublishAsync("orders/created", new OrderCreatedEvent(order.Id), ct);
    }
}
```

---

## Publishing Messages

Inject `IMessageBus` and call `PublishAsync<T>`:

```csharp
await bus.PublishAsync("route/path", payload, cancellationToken);
```

- Each message gets a **UUIDv7 correlation ID** (time-ordered) for tracing.
- If the channel has capacity, the write completes synchronously (zero allocation fast path).
- If the channel is full, the publisher awaits with backpressure — it will **not** drop the message.

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

Legacy methods are still available for backward compatibility:

```csharp
services.AddChannelSubscription<T>(path, handler);
builder.SubscribeAsync<T>(path, handler);
```

- Routes are **case-insensitive**.
- A route can only be registered **once** per message type — duplicates throw `InvalidOperationException` at startup.
- Publishing to a route with **no handler** throws `RouteNotFoundException` — the message is sent to the dead letter queue immediately (no retries).

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

Calling `Subscribe<T>` (or `AddChannelSubscription<T>`) auto-registers all required infrastructure for type `T`:

| Component | Lifetime | Purpose |
|-----------|----------|---------|
| `ChannelRegistry` | Singleton | Bounded channel pool, one per `T` |
| `ChannelRouteTable<T>` | Singleton | Route → handler mapping |
| `DeadLetterQueue<T>` | Singleton | Failed messages storage |
| `DeadLetterQueueRegistry` | Singleton | Type-keyed registry of all DLQs |
| `PersistentMessageStore<T>` | Singleton | LiteDB crash recovery store |
| `PersistentChannelRouterSubscriber<T>` | Hosted Service | Background reader/dispatcher |

---

## Configuration

### `MessageBusOptions`

| Property | Default | Description |
|----------|---------|-------------|
| `DefaultChannelCapacity` | `10,000` | Max messages buffered per type before backpressure kicks in |
| `MaxRetryAttempts` | `3` | Number of dispatch attempts before sending to DLQ |
| `InitialRetryDelayMs` | `100` | First retry delay (doubles on each retry, capped at 30s) |
| `MaxConcurrency` | `1` | Parallel message handlers per type. `1` = sequential, `> 1` = concurrent via `SemaphoreSlim` |
| `PersistenceBasePath` | `%LocalAppData%/Hermes/MessageBus` | Directory for LiteDB files. One `.db` file per message type. |

### Custom Channel Options

Override channel settings per subscription:

```csharp
builder.Subscribe<T>("path", handler, options =>
{
    options.Capacity = 50_000;
    options.SingleWriter = true;
});
```

> **Note:** `SingleReader` is always `true` — the subscriber loop is the sole channel reader even with `MaxConcurrency > 1` (workers receive already-dequeued items).

---

## Message Lifecycle

```
┌──────────┐     ┌─────────┐     ┌───────────┐     ┌──────────────┐
│ Published │────►│ Pending │────►│ Completed │     │ DeadLettered │
└──────────┘     └─────────┘     └───────────┘     └──────────────┘
                      │                                    ▲
                      │          ┌──────────┐              │
                      └─────────►│  Failed  │──────────────┘
                                 └──────────┘
                              (retry exhausted)
```

| Status | When | Stored in DB |
|--------|------|:---:|
| **Pending** | Message persisted, processing not yet started or in progress | ✅ |
| **Completed** | Handler returned successfully | ✅ (cleaned up after 7 days) |
| **Failed** | Handler threw, but retries remain (replay will try again) | ✅ |
| **DeadLettered** | All retries exhausted — moved to DLQ | ✅ (cleaned up after 7 days) |

### Crash Recovery

On startup, `PersistentChannelRouterSubscriber<T>` calls `ReplayPendingMessagesAsync()`:

1. Queries all messages with `Status == Pending` from LiteDB.
2. Re-dispatches them through the normal retry pipeline.
3. Messages that exceed `MaxRetryAttempts` during replay are moved to the DLQ.
4. Only after replay completes does the subscriber start reading from the live channel.

---

## Retry & Circuit Breaker

### Retry Policy

- **Strategy:** Exponential backoff starting at `InitialRetryDelayMs`, doubling on each attempt, capped at 30 seconds.
- **Max attempts:** Configurable via `MaxRetryAttempts` (default 3).
- **Non-retryable:** `RouteNotFoundException` and `OperationCanceledException` bypass the retry loop entirely.

### Circuit Breaker

Each route has an independent circuit breaker (keyed by `{TypeName}:{route}`):

| State | Behavior |
|-------|----------|
| **Closed** | Normal operation. Failures increment a counter. |
| **Open** | Requests throw `CircuitBreakerOpenException` immediately. Auto-transitions to HalfOpen after `openDuration` (default 1 minute). |
| **HalfOpen** | One test request is allowed. Success → Closed. Failure → Open again. |

Defaults: threshold = **5 failures**, open duration = **1 minute**.

---

## Dead Letter Queue

Messages that fail after all retries are moved to a per-type `DeadLetterQueue<T>`:

- Bounded channel (capacity 10,000, `FullMode = Wait`).
- If `TryEnqueue` fails (queue full), a **Critical** log is emitted and the message is permanently lost.
- The `DeadLetterQueueProcessor` (single `BackgroundService`) polls all DLQs every 5 seconds, processing up to 100 messages per queue per cycle.

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
        var healthy = diag.IsHealthy;
        var backlog = diag.GetBacklogCount<OrderCreatedEvent>();
        var circuit = diag.GetCircuitState<OrderCreatedEvent>("orders/created");
        var stats   = diag.GetStoreStats<OrderCreatedEvent>();

        return Results.Ok(new { healthy, backlog, circuit, stats });
    }
}
```

### OpenTelemetry Metrics

All metrics are emitted under the `Hermes.MessageBus` meter:

| Metric | Type | Tags | Description |
|--------|------|------|-------------|
| `messagebus.enqueued` | Counter | `message_type`, `route` | Messages published to channel |
| `messagebus.dispatched` | Counter | `message_type`, `route` | Successfully dispatched |
| `messagebus.dispatch.failed` | Counter | `message_type`, `route` | Dispatch failures (per attempt) |
| `messagebus.dispatch.retried` | Counter | `message_type`, `route` | Retry attempts |
| `messagebus.dropped` | Counter | `message_type`, `route` | Messages dropped (no handler, DLQ full) |
| `messagebus.dispatch.duration` | Histogram (ms) | `message_type`, `route` | Handler execution time |
| `messagebus.backlog` | Gauge | `message_type`, `route` | Messages waiting in channel |

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
    ├── IMessageBus.cs                 # Core publish interface
    ├── IMessageBusDiagnostics.cs      # Health & diagnostics interface
    ├── InMemoryMessageBus.cs          # IMessageBus implementation
    ├── MessageBusDiagnostics.cs       # IMessageBusDiagnostics implementation
    ├── DependencyInjection.cs         # AddHermesMessaging() + MessageBusOptions
    ├── ChannelRegistry.cs             # Bounded channel pool (one per T)
    ├── ChannelRouteTable.cs           # Route → handler dispatch + RouteNotFoundException
    ├── ChannelRouteRegistration.cs    # DI-time route wiring
    ├── ChannelSubscriptionExtensions.cs  # Subscribe<T>() + AddSubscription<T>() + legacy SubscribeAsync<T>()
    ├── ChannelSubscriptionBuilder.cs  # Fluent builder: WithDeadLetterHandler<T>()
    ├── ChannelPublishExtensions.cs    # Fast-path TryWrite + backpressure fallback
    ├── ChannelMetrics.cs              # OpenTelemetry counters, histograms, gauges
    ├── PersistentMessageStore.cs      # LiteDB CRUD + cleanup + stats
    ├── PersistentChannelRouterSubscriber.cs  # Core BackgroundService: persist → dispatch → retry
    ├── CircuitBreaker.cs              # Per-route circuit breaker + CircuitBreakerOpenException
    ├── DeadLetterQueue.cs             # Bounded DLQ channel + DeadLetterMessage<T>
    ├── DeadLetterQueueRegistry.cs     # Type-keyed registry of all DLQs
    └── DeadLetterQueueProcessor.cs    # BackgroundService: polls DLQs → IDeadLetterHandler<T>
```

---

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `LiteDB` | 5.0.21 | Embedded NoSQL database for message persistence |
| `Microsoft.Extensions.Hosting.Abstractions` | 10.0.1 | `BackgroundService`, `IHostedService`, `IHostApplicationBuilder` |

No external message broker required. The entire bus runs in-process.
