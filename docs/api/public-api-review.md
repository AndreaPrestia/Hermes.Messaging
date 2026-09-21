# Public API Review — current (`0.5.0-beta`)

This document describes the **current** supported public API of `Hermes.Messaging` and the workflow
for maintaining its compatibility baseline. It reflects the `0.5.0-beta` public API freeze (the
current API is now the shipped compatibility baseline).

- **Public namespace:** everything a consumer needs is in the single root namespace
  **`Hermes.Messaging`** — an application typically needs only `using Hermes.Messaging;`. There are
  no public types under `Hermes.Messaging.Infrastructure` or `Hermes.Messaging.Domain.*`; those are
  internal implementation namespaces.
- **Authoritative surface:** the machine-readable public API is
  [`src/Hermes.Messaging/PublicAPI.Shipped.txt`](../../src/Hermes.Messaging/PublicAPI.Shipped.txt)
  and [`PublicAPI.Unshipped.txt`](../../src/Hermes.Messaging/PublicAPI.Unshipped.txt), enforced at
  build time by `Microsoft.CodeAnalysis.PublicApiAnalyzers` (RS0016/RS0017 + nullability/duplicate/
  order rules are **build errors** via `.editorconfig`). These files, not this prose, are the source
  of truth.

---

## Current public surface

### Registration / configuration
| Type / member | Purpose |
|---------------|---------|
| `DependencyInjection.AddHermesMessaging(IServiceCollection)` | Register the bus with defaults. |
| `DependencyInjection.AddHermesMessaging(IServiceCollection, Action<MessageBusOptions>)` | Register + configure. |
| `ChannelSubscriptionExtensions.AddChannelSubscription<T>(...)` (×2) | Register a route handler on `IServiceCollection` (with/without channel options). Auto-registers all per-type infrastructure, including the durable dead-letter store. |
| `ChannelSubscriptionExtensions.AddSubscription<T>(...)` | Same, returning `ChannelSubscriptionBuilder<T>` for fluent config. |
| `ChannelSubscriptionExtensions.Subscribe<T>(this IHostApplicationBuilder, ...)` | Host-builder entry, returns the fluent builder. |
| `ChannelSubscriptionBuilder<T>.WithDeadLetterHandler<THandler>()` | Register an `IDeadLetterHandler<T>`. |
| `MessageBusOptions` | `DefaultChannelCapacity`, `MaxAttempts`, `InitialRetryDelayMs`, `MaxConcurrency`, `ShutdownGracePeriod`, `PersistenceBasePath`. |

> Dead-letter infrastructure is registered **automatically** by each subscription. There is no
> separate `AddDeadLetterQueue<T>` call, and there is no `SubscribeAsync<T>` (removed in 0.4.1 — it
> was synchronous and misleadingly suffixed).

### Publishing
| Type / member | Purpose |
|---------------|---------|
| `IMessageBus.PublishAsync<T>(string route, T message, PublishOptions? options = null, CancellationToken = default)` | Durable publish; a returned `PublishResult` means the message was committed before return. |
| `PublishOptions` | `CorrelationId` (optional). |
| `PublishResult` | `MessageId`, `CorrelationId`, `AcceptedAt`. |

### Handling / retry contract
| Type / member | Purpose |
|---------------|---------|
| `IDeadLetterHandler<T>.HandleAsync(DeadLetterMessage<T>, CancellationToken)` | Consumer-implemented dead-letter observer. |
| `DeadLetterMessage<T>` | Passed to the handler (`Path`, `Body`, `Exception`, `Attempts`, `CorrelationId`, `FailedAt`). |
| `NonRetryableException` | Throw from a handler to dead-letter immediately (no further attempts). |

### Dead-letter administration
| Type / member | Purpose |
|---------------|---------|
| `IDeadLetterAdministration<T>` | `List`, `Get`, `Replay`, `Delete`, `Purge` over the durable dead-letter store. |
| `DeadLetterEntry<T>` | Read model returned by the admin API. |

### Diagnostics
| Type / member | Purpose |
|---------------|---------|
| `IMessageBusDiagnostics.IsHealthy` | Liveness (bus resolvable, not faulted). |
| `IMessageBusDiagnostics.IsReady` | Readiness (runtime Ready + startup recovery complete). |
| `IMessageBusDiagnostics.CurrentState` | Read-only `RuntimeState` observation (consumers observe, never mutate). |
| `IMessageBusDiagnostics.GetBacklogCount<T>()` | Durable backlog (Pending + Processing + RetryScheduled). |
| `IMessageBusDiagnostics.GetStoreStats<T>()` | `MessageStoreStats?` — **`null` when no durable store is registered for `T`** (i.e. `T` has no subscription). |
| `MessageStoreStats` | Pending/Processing/RetryScheduled/Completed/DeadLettered/Total counts. |
| `RuntimeState` | `Created`/`Starting`/`Ready`/`Stopping`/`Stopped`/`Faulted`. |

### Exceptions (may escape to consumer code)
`NonRetryableException`, `RouteNotFoundException`, `HermesNotReadyException`,
`StoreSchemaMismatchException` — all in `Hermes.Messaging`.

### Telemetry
`HermesTelemetry` exposes only `Name` (the stable `"Hermes.Messaging"` used for the meter and
activity source) and `ActivitySource` (intentionally public so consumers can subscribe spans for
OpenTelemetry). The `Meter` and all metric instruments are `internal` and are **not** part of the
public API.

### Not public
No implementation types are exported: the durable store, channel registry/route table, hosted
subscriber, readiness/runtime-state holders, store-metrics registry, observer dead-letter queue, and
the persisted-entity/status types are all `internal`. This is enforced by the analyzer baseline.

---

## API baseline workflow

`Microsoft.CodeAnalysis.PublicApiAnalyzers` tracks the public surface in two files in
`src/Hermes.Messaging/`:

- **`PublicAPI.Shipped.txt`** — the public API of the **last actually-released** version. It is only
  updated when a release is cut.
- **`PublicAPI.Unshipped.txt`** — public API **additions/removals/changes since the last release**.
  This is where in-progress API lives until it ships.

The analyzer treats the union of both files as the "declared" public API. Any public type/member not
in either file fails the build with **RS0016**; any entry present in a file but missing from the
assembly fails with **RS0017**. Nullability (`RS0037`/`RS0041`), duplicates (`RS0025`), and ordering
(`RS0024`) are also enforced as errors (see `.editorconfig`).

### Current baseline state (0.5.0-beta)
The `0.5.0-beta` freeze established the first analyzer-managed compatibility baseline: the **entire
current public API is now in `PublicAPI.Shipped.txt`** and `PublicAPI.Unshipped.txt` contains only the
`#nullable enable` header. `Shipped.txt` is the beta compatibility floor.

Baseline provenance is the repository's intentional freeze process, not package-publication history:
the repository has no GitHub Releases and the only pre-existing Git tag is `v0.2.0-alpha`. NuGet.org
publication status of intermediate alpha builds is **not** used as the compatibility-baseline source
for this repository.

### Adding or changing a public API (maintainer steps)
1. Make the code change.
2. Build. The analyzer reports RS0016 (new/undeclared) or RS0017 (removed) for the delta.
3. Record the delta in `PublicAPI.Unshipped.txt`:
   - **Addition:** add the canonical line the analyzer expects (build error text shows it; or apply
     the "Add to public API" code fix / `dotnet format analyzers --diagnostics RS0016`).
   - **Removal:** move the line to `PublicAPI.Unshipped.txt` prefixed with `*REMOVED*` (do **not**
     just delete it — that loses the compatibility record until release).
4. Review the diff to `PublicAPI.Unshipped.txt` in code review — this is the intentional-change gate.
5. Keep the `#nullable enable` header at the top of each file.

### Cutting a release (post-beta workflow)
The existing beta API is in `Shipped.txt`. A new public **addition** goes to `Unshipped.txt`; a
**removal/change** is recorded in `Unshipped.txt` using the analyzer's conventions (`*REMOVED*`
prefix for removals — do not just delete). When the next version is released, reconcile every line
from `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` (applying `*REMOVED*` deletions), then
reset `PublicAPI.Unshipped.txt` to just `#nullable enable`. `Shipped.txt` remains the compatibility
floor for the release line.

> Do not weaken RS0016/RS0017 (or set them below `error`) to make a change compile. The whole point
> is that public-surface drift is a deliberate, reviewed act.

---

## Historical 0.4.0 cleanup decisions

The full type-by-type KEEP/INTERNALIZE rationale from the `0.4.0-alpha` internalization pass (which
made the implementation graph `internal`) is preserved in `CHANGELOG.md` under the `0.4.0-alpha`
entry. Subsequent shape changes are recorded under `0.4.1-alpha` (single-namespace migration;
removal of `SubscribeAsync<T>`, `AddDeadLetterQueue<T>`, and the obsolete `MaxRetryAttempts`) and
`0.4.2-alpha` (baseline-hygiene: Shipped/Unshipped correction, CI action bump), and `0.5.0-beta`
(public API freeze: current API promoted to `Shipped.txt`; attribution resolved to Andrea Prestia).
This document is kept as the current-state reference; the CHANGELOG is the historical record.
