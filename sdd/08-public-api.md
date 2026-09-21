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

Alpha is the right time for correctness-driven breaking changes. Never preserve an overload if it implies weaker durability semantics.
