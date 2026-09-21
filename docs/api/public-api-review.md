# Public API Review — 0.4.x (breaking API cleanup)

> ## 0.4.1-alpha update — final API design before beta
>
> The `0.4.1-alpha` pass finalized the public API shape. Changes relative to the `0.4.0-alpha`
> inventory below:
>
> - **Namespace:** every public consumer type moved to the single root **`Hermes.Messaging`**
>   namespace (previously `Hermes.Messaging.Infrastructure`; `StoreSchemaMismatchException` was in
>   `Hermes.Messaging.Domain.Entities`). A consumer now needs only `using Hermes.Messaging;`. The
>   `Infrastructure`/`Domain.*` namespaces below are historical — no public type lives there anymore.
> - **Removed:** `SubscribeAsync<T>` (misleading `Async` suffix), `AddDeadLetterQueue<T>` (redundant —
>   DLQ infra is auto-registered by `AddChannelSubscription<T>`), and the obsolete
>   `MessageBusOptions.MaxRetryAttempts` alias (use `MaxAttempts`).
> - **Guard replaced:** the reflection snapshot (`docs/api/PublicAPI.txt` + `PublicApiSurfaceTests`)
>   was removed in favor of `Microsoft.CodeAnalysis.PublicApiAnalyzers` with
>   `src/Hermes.Messaging/PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`. RS0016/RS0017 +
>   nullability/duplicate/order rules are **build errors** (`.editorconfig`), giving compile-time
>   coverage of signatures, nullable annotations, default parameter values, and generic constraints.
>   Maintainers add a public API deliberately by recording it in `PublicAPI.Unshipped.txt`.
>
> The rest of this document is the original `0.4.0-alpha` internalization record; the KEEP/INTERNALIZE
> decisions still hold (only the namespace prefixes and the three removals above have changed).

---

This document inventories the externally visible API of `Hermes.Messaging` after the `0.4.0-alpha`
public-API cleanup and classifies each type as one of:

- **KEEP** — intended, supported public surface.
- **INTERNALIZE** — implementation detail; made `internal` in this pass.
- **OBSOLETE** — kept public for compatibility but marked `[Obsolete]`.
- **REDESIGN** — public shape needs a deliberate change (recorded, may be deferred).

The authoritative machine-readable public surface is now
[`src/Hermes.Messaging/PublicAPI.Shipped.txt`](../../src/Hermes.Messaging/PublicAPI.Shipped.txt),
enforced at build time by `Microsoft.CodeAnalysis.PublicApiAnalyzers` (see the 0.4.1 update above).

The test project uses `[InternalsVisibleTo]`, and the benchmark project (which seeds the durable
store directly as internal tooling) does too — so internalizing a type does **not** break tests or
benchmarks. External consumers are validated by the package smoke test
(`tests/Hermes.Messaging.PackageSmokeTest`), which compiles only against the shipped package's
public surface.

---

## KEEP — intended public consumer surface

| Namespace.Type | Why a consumer needs it |
|----------------|-------------------------|
| `Hermes.Messaging.Infrastructure.IMessageBus` | The publish entry point. |
| `Hermes.Messaging.Infrastructure.PublishOptions` | Publish input (correlation id). |
| `Hermes.Messaging.Infrastructure.PublishResult` | Publish output (durable acceptance proof). |
| `Hermes.Messaging.Infrastructure.MessageBusOptions` | Configuration (`AddHermesMessaging(configure)`). |
| `Hermes.Messaging.Infrastructure.DependencyInjection` | `AddHermesMessaging`, `AddDeadLetterQueue<T>`. |
| `Hermes.Messaging.Infrastructure.ChannelSubscriptionExtensions` | `Subscribe`/`AddSubscription`/`AddChannelSubscription`/`SubscribeAsync`. |
| `Hermes.Messaging.Infrastructure.ChannelSubscriptionBuilder<T>` | Fluent `WithDeadLetterHandler<THandler>()`. |
| `Hermes.Messaging.Infrastructure.IDeadLetterAdministration<T>` | Durable DLQ admin (List/Get/Replay/Delete/Purge). |
| `Hermes.Messaging.Infrastructure.DeadLetterEntry<T>` | Read model returned by the admin API. |
| `Hermes.Messaging.Infrastructure.IDeadLetterHandler<T>` | Consumer-implemented DLQ observer. |
| `Hermes.Messaging.Infrastructure.DeadLetterMessage<T>` | Passed to `IDeadLetterHandler<T>`. |
| `Hermes.Messaging.Infrastructure.IMessageBusDiagnostics` | Liveness/readiness/state + durable backlog/stats. |
| `Hermes.Messaging.Infrastructure.MessageStoreStats` | Projection returned by `GetStoreStats<T>()`. |
| `Hermes.Messaging.Infrastructure.RuntimeState` | Enum observed via `IMessageBusDiagnostics.CurrentState`. |
| `Hermes.Messaging.Infrastructure.NonRetryableException` | Handlers throw it to dead-letter immediately. |
| `Hermes.Messaging.Infrastructure.RouteNotFoundException` | Can escape publish; consumers may catch. |
| `Hermes.Messaging.Infrastructure.HermesNotReadyException` | Can escape publish-before-ready; consumers may catch. |
| `Hermes.Messaging.Domain.Entities.StoreSchemaMismatchException` | Can escape host startup; operators may catch. |
| `Hermes.Messaging.Infrastructure.HermesTelemetry` | `ActivitySource`/meter name for OpenTelemetry wiring. |

### Additive change in this pass
- `IMessageBusDiagnostics.CurrentState` (get-only `RuntimeState`) was **added** so consumers can
  observe lifecycle state without the (now-internal) mutable `HermesRuntimeState` holder.

---

## INTERNALIZE — made `internal` in this pass

Verified by real usage: none of these are referenced by the package smoke test or the crash harness
(consumers see only public API). Tests/benchmarks that touch them rely on `[InternalsVisibleTo]`,
which is **not** a reason to keep a type public.

| Namespace.Type | Public contract that replaces direct consumer access |
|----------------|------------------------------------------------------|
| `Infrastructure.PersistentChannelRouterSubscriber<T>` | Hosted service; wired by `AddChannelSubscription<T>`. **Linchpin** — internalizing it unblocked its whole ctor-parameter graph. |
| `Infrastructure.ChannelRouteRegistration<T>` | DI wiring detail. |
| `Infrastructure.ChannelRegistry` | Wake-up channel pool. |
| `Infrastructure.ChannelRouteTable<T>` | Route→handler dispatch. |
| `Infrastructure.HermesStoreMetrics` | Telemetry gauge registry; metrics exposed via `HermesTelemetry`. |
| `Infrastructure.HermesReadiness` | Readiness coordination; observed via `IMessageBusDiagnostics.IsReady`. |
| `Infrastructure.HermesRuntimeState` (incl. `Set`/`TryTransition`/`EnsureReady`) | State observed via `IMessageBusDiagnostics.CurrentState`/`IsReady`; mutation is runtime-only. |
| `Infrastructure.InMemoryMessageBus` | Consumers use `IMessageBus`. |
| `Infrastructure.PersistentMessageStore<T>` | Durable store; access via diagnostics/admin projections. |
| `Infrastructure.IMessageStore<T>` | Storage abstraction — **not** a supported extension point (see Storage decision). |
| `Infrastructure.DeadLetterQueue<T>` | Observer channel; consumers use `IDeadLetterHandler<T>`/admin. |
| `Infrastructure.DeadLetterQueueRegistry` | Observer registry. |
| `Infrastructure.DeadLetterQueueProcessor` | Hosted service draining observers. |
| `Infrastructure.DeadLetterAdministration<T>` (concrete) | Consumers use the `IDeadLetterAdministration<T>` interface. |
| `Infrastructure.RetryClassifier` | Retry classification is runtime-internal. |
| `Infrastructure.FailureDisposition` (enum) | Result of internal classification. |
| `Domain.Interfaces.IDeadLetterQueue` | Internal observer abstraction. |
| `Domain.Entities.DeadLetterReadResult` | Internal observer read result. |
| `Domain.Entities.ChannelMessage<T>` | Internal envelope. |
| `Domain.Entities.PersistedMessage<T>` | Internal durable entity. |
| `Domain.Entities.MessageStatus` (enum) | Internal durable-state enum (was only in `IMessageStore<T>` signatures). |
| `Domain.Entities.PersistedMessageSchema` | Internal durable-format version constant. |

---

## OBSOLETE

| Member | Note |
|--------|------|
| `MessageBusOptions.MaxRetryAttempts` | Already `[Obsolete]`; alias of `MaxAttempts` (total handler invocations). Kept for alpha compatibility. Still appears in the public baseline until removed at beta. |

No new obsoletes were introduced: the registration extensions (`Subscribe`, `AddSubscription`,
`AddChannelSubscription`, `SubscribeAsync`) are genuinely distinct entry points (host-builder vs
service-collection; fluent vs terminal), not redundant aliases, so none were deprecated (avoiding
churn without clear value).

---

## REDESIGN (recorded; deferred to beta)

| Item | Problem | Decision |
|------|---------|----------|
| `StoreSchemaMismatchException` namespace | Lives in `Hermes.Messaging.Domain.Entities` while all other consumer exceptions are in `Hermes.Messaging.Infrastructure`. | Kept public (operators may catch it) but the namespace is a wart. Moving it is a breaking change; deferred to the beta namespace-polish pass to avoid piecemeal churn now. |
| Consumer namespaces | Core consumer APIs live in `Hermes.Messaging.Infrastructure`, which reads like an implementation namespace. | Deferred: a namespace migration (`Hermes.Messaging` / `.DeadLetters` / `.Diagnostics`) would be broad and breaking; documented as a beta item rather than combined blindly with internalization. |

---

## Storage abstraction decision (Phase C)

`IMessageStore<T>` and its concrete `PersistentMessageStore<T>` are **internalized**. Hermes does
**not** promise pluggable/alternate persistence providers, and no storage-provider plugin system was
introduced. The persisted entity types (`PersistedMessage<T>`, `ChannelMessage<T>`) and the durable
status enum (`MessageStatus`) disappear from the consumer surface with it. Diagnostics
(`MessageStoreStats` via `IMessageBusDiagnostics.GetStoreStats<T>()`) and dead-letter administration
(`IDeadLetterAdministration<T>`) remain the supported durable-state projections.

## Runtime state decision (Phase D)

`HermesRuntimeState` (and its mutating `Set`/`TryTransition`) is **internalized**. Consumers can now
**observe** the lifecycle read-only via the new `IMessageBusDiagnostics.CurrentState`, plus the
existing `IsReady`/`IsHealthy`. No new lifecycle abstraction was introduced.

## Readiness decision (Phase E)

`HermesReadiness` is **internalized** (internal recovery/lifecycle coordination). Readiness remains
exposed through `IMessageBusDiagnostics.IsReady`.

## Retry API decision (Phase H)

`NonRetryableException` stays **public** (handlers throw it). `RetryClassifier` and
`FailureDisposition` are **internalized** — no consumer calls `Classify`; they are runtime
implementation details (tests exercise them via `[InternalsVisibleTo]`, which is not a reason to
keep them public).

## Dead-letter API decision (Phase F)

**KEEP public:** `IDeadLetterAdministration<T>`, `DeadLetterEntry<T>`, `IDeadLetterHandler<T>`,
`DeadLetterMessage<T>`. **INTERNALIZE:** `DeadLetterQueue<T>`, `DeadLetterQueueRegistry`,
`DeadLetterQueueProcessor`, `IDeadLetterQueue`, `DeadLetterReadResult`, and the concrete
`DeadLetterAdministration<T>`. Consumers manage durable dead letters through the administration
interface, not the observer-channel implementation.

## Persistence schema API decision (Phase G)

- `MessageStatus` — **internalized** (only appeared in the now-internal `IMessageStore<T>`
  signatures; `DeadLetterEntry<T>` does not expose it).
- `PersistedMessageSchema` — **internalized** (internal durable-format logic only).
- `StoreSchemaMismatchException` — **kept public** (it can escape host startup and operators may
  reasonably identify it programmatically). No persisted entity type is exposed because of it.

## Namespace decision (Phase J)

No namespace migration was performed. The priority was removing accidental public surface, not
aesthetic renaming. A namespace migration would be broad and breaking; it is documented above under
REDESIGN as a deferred beta item.
