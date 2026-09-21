# Public API Review — 0.3.0-alpha (beta preparation)

This document inventories the externally visible API of `Hermes.Messaging` and classifies each
public type as one of:

- **KEEP** — intended, stable public surface.
- **OBSOLETE** — kept for compatibility; slated for removal.
- **INTERNALIZE-BEFORE-BETA** — implementation detail that is currently public and should become
  `internal` before the beta API freeze, once we confirm no intended consumer depends on it.
- **NEEDS-DESIGN** — public but its shape/exposure needs a deliberate decision before beta.

The test project uses `[InternalsVisibleTo]`, so internalizing a type does **not** break tests.
External consumers are validated separately by the package smoke test (`tests/PackageSmokeTest`),
which only compiles against the shipped NuGet package's public surface.

## Actions taken in this pass

**No types were internalized in this pass.** An initial attempt to internalize the two clearly-
internal helpers (`ChannelRouteRegistration<T>`, `HermesStoreMetrics`) failed to compile: both are
constructor parameters of the **public** `PersistentChannelRouterSubscriber<T>` hosted service
(CS0051 — a public member cannot expose a less-accessible parameter type). That proves these types
cannot be internalized in isolation: the whole registration/subscriber/store graph must be
internalized together as one coordinated breaking change. Per the task ("do not mass-internalize
automatically; document ambiguous cases"), that work is deferred to a dedicated pre-beta task and
documented below. XML doc comments now flag both as `INTERNALIZE-BEFORE-BETA`.

## Classification

### Core public contract — KEEP

| Type | Notes |
|------|-------|
| `IMessageBus` | Primary publish API. |
| `PublishOptions` | Publish input (CorrelationId). |
| `PublishResult` | Publish output (MessageId/CorrelationId/AcceptedAt). |
| `IMessageBusDiagnostics` | Liveness/readiness/backlog/stats. |
| `IDeadLetterAdministration<T>` | Durable DLQ admin (List/Get/Replay/Delete/Purge). |
| `DeadLetterEntry<T>` | DLQ read model. |
| `MessageBusOptions` | Configuration. |
| `DependencyInjection` (`AddHermesMessaging`) | Registration entry point. |
| `ChannelSubscriptionExtensions` (`Subscribe`/`AddSubscription`/`AddChannelSubscription`) | Subscription registration. |
| `ChannelSubscriptionBuilder<T>` | Fluent subscription config (`WithDeadLetterHandler`). |
| `IDeadLetterHandler<T>` | Consumer-implemented observer hook. |
| `RetryClassifier`, `FailureDisposition`, `NonRetryableException` | Retry classification consumers rely on (throwing `NonRetryableException`). |
| `RouteNotFoundException` | Thrown to callers on unknown route. |
| `HermesNotReadyException`, `RuntimeState` | Publish-gating exception + observable state. |
| `HermesRuntimeState` | Observable runtime state (resolvable). NEEDS-DESIGN candidate — see below. |
| `MessageStatus` | Durable state enum surfaced via stats/entries. |
| `HermesTelemetry` | Stable meter/ActivitySource **names** are the contract; exposing the static as public is convenient for consumers wiring OpenTelemetry. |
| `MessageStoreStats` | Returned by diagnostics. |

### KEEP but review naming/shape — NEEDS-DESIGN

| Type | Concern |
|------|---------|
| `HermesRuntimeState` | Public mutable state holder (`Set`, `TryTransition`) is broader than consumers need; consider exposing a read-only `RuntimeState Current` via diagnostics only. |
| `HermesReadiness` | Public but is an internal coordination primitive; readiness is already surfaced via `IMessageBusDiagnostics.IsReady`. Candidate to internalize once confirmed no consumer resolves it directly. |
| `IDeadLetterQueue` / `DeadLetterReadResult` | Non-generic polymorphic access used by the processor; unclear if consumers need it. |
| `PersistedMessageSchema` | Public constant for schema version; keep but document as informational. |

### Implementation details — INTERNALIZE-BEFORE-BETA

These are public today but are implementation types that intended consumers should not use
directly. They are **not** internalized in this pass because DI registration, diagnostics, and the
current test suite resolve some of them by concrete type; internalizing requires a coordinated
change and is a breaking API change best done as its own step.

| Type | Why public today | Target |
|------|------------------|--------|
| `PersistentMessageStore<T>` | Registered as concrete singleton; tests resolve it directly. | `internal` (expose only via `IMessageStore<T>`/diagnostics). |
| `IMessageStore<T>` | Storage abstraction; only one production impl. | Keep public **only if** a pluggable store is a goal; otherwise internalize. Currently NEEDS-DESIGN. |
| `ChannelRegistry` | Wake-up channel pool. | `internal`. |
| `ChannelRouteTable<T>` | Route→handler dispatch table. | `internal`. |
| `PersistentChannelRouterSubscriber<T>` | Hosted service. | `internal`. |
| `DeadLetterQueue<T>` | Best-effort observer channel. | `internal` (consumers use `IDeadLetterHandler<T>` / admin). |
| `DeadLetterQueueRegistry` | Observer channel registry. | `internal`. |
| `DeadLetterQueueProcessor` | Hosted service. | `internal`. |
| `DeadLetterMessage<T>` | Passed to `IDeadLetterHandler<T>` — **must stay public** (consumer-facing). | KEEP. |
| `ChannelMessage<T>` | Internal envelope. | `internal`. |
| `PersistedMessage<T>` | Durable entity. | `internal` (read models like `DeadLetterEntry<T>`/`MessageStoreStats` are the public projections). |
| `HermesRuntimeState.Set/TryTransition` | Mutation surface. | Reduce to read-only public view. |

### Already internal (correct)

`ChannelMetrics`, `TypeIdentity`, `HermesLifecycle`, `MessageBusDiagnostics` (impl), and now
`ChannelRouteRegistration<T>`, `HermesStoreMetrics`.

## Recommendation

Perform the `INTERNALIZE-BEFORE-BETA` group as a single, clearly-labelled breaking change in a
dedicated task before `0.5.0-beta`, together with the package smoke test as the guardrail for the
intended public surface. Do not mass-internalize piecemeal across releases.
