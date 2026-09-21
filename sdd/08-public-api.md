# 08 — Public API

The public API must expose product semantics, not infrastructure internals.

Target surface:
```text
IMessageBus
PublishOptions
PublishResult
MessageContext / MessageEnvelope
HermesOptions
HermesSubscriptionOptions
registration extensions
dead-letter administration surface
diagnostics/health
```

Suggested publish API:
```csharp
ValueTask<PublishResult> PublishAsync<T>(
    string route,
    T message,
    PublishOptions? options = null,
    CancellationToken cancellationToken = default);
```

`PublishResult` should expose at least:
```text
MessageId
CorrelationId
AcceptedAt
```

`PublishOptions` should expose at least:
```text
CorrelationId
```

Handler context may expose:
```text
MessageId
CorrelationId
Route
Attempt
CreatedAt
Headers
CancellationToken
```

Before beta, internalize infrastructure details such as:
- ChannelRegistry;
- ChannelRouteTable;
- LiteDB store implementation;
- persistence entities;
- Channel-specific DLQ internals;
- circuit breaker implementation.

Correctness-driven changes remain the priority, and an overload must never be preserved if it implies weaker durability semantics. However, since the `v0.5.0-beta` freeze the public API is a tracked compatibility contract: breaking public API changes must be exceptional, explicitly justified, documented in CHANGELOG/release notes, reflected through `PublicApiAnalyzers`, and versioned appropriately — not made merely because a refactor would be cleaner.
