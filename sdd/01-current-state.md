# 01 — Current State

> **0.3.0-alpha maturity pass.** Added a BenchmarkDotNet project, a benchmark baseline, a public-API
> review, and a storage-versioning ADR. Package hardened (XML docs, SDK SourceLink, CI `pack` +
> package smoke test). A fail-fast `StoreSchemaMismatchException` guards against opening a store
> written by a newer schema. Benchmarks found no correctness or clear performance defect, so the
> messaging architecture is unchanged. Implementation-type internalization is deferred to a
> coordinated pre-beta breaking change.
>
> **Bounded reconciliation (HERMES-007 implemented).** Reconciliation and startup seeding now query
> only a bounded number of due `MessageId`s (`GetDueMessageIds(now, limit)`, limit enforced at the
> query level, IDs only) sized to the available wake-up capacity, instead of materializing the whole
> due backlog. Large backlogs are processed in bounded refill batches. No delivery/lifecycle/DLQ
> semantics changed.
>
> **Hardening update (HERMES-006 implemented).** Post-implementation review fixes: publish is
> gated on startup-recovery completion (readiness matches publishability); the reconciliation
> wake-up layer is bounded and de-duplicated; all runtime components use `IMessageStore<T>` (no
> publisher/subscriber split-brain); dead-letter replay resets the retry budget; `MaxRetryAttempts`
> renamed to `MaxAttempts`; internal identity keys use `TypeIdentity` (FullName) to avoid short-name
> collisions; the legacy circuit breaker was removed from core; durable backlog diagnostics come
> from the store; telemetry meter version hard-coding removed; README aligned with the current API;
> and a GitHub Actions CI workflow was added. Deferred (still open): BenchmarkDotNet, broad API
> internalization, NuGet publishing, SourceLink.
>
> **Phase 1 update (HERMES-001 implemented).** The publish-before-persist defect
> described below has been fixed. `PublishAsync` now validates the route, durably
> persists the message (Pending) with a unique `MessageId`, and only then does a
> best-effort Channel signal before returning an accepted `PublishResult`. The
> subscriber no longer inserts a duplicate durable record.
>
> **Phase 2 update (HERMES-002 implemented).** The durable state machine is in place:
> `Pending -> Processing -> Completed`, `Processing -> RetryScheduled -> Processing`,
> `Processing -> DeadLettered`, and explicit `DeadLettered -> Pending` replay. Messages
> are claimed atomically via `TryClaim` (duplicate-signal safe), retries are durable
> (`RetryScheduled` + `NextAttemptAt`) and paced by a reconciliation loop rather than
> holding a worker, and startup recovery returns interrupted `Processing` (and the retired
> `Failed`) records to `Pending`. The recovery-stranding defect below is fixed.
>
> **Phase 3 update (HERMES-003 implemented).** Retry timing now uses an injectable
> `TimeProvider` with bounded exponential backoff + full jitter. Failures are classified
> (`RetryClassifier`): non-retryable programming/configuration errors and `NonRetryableException`
> dead-letter immediately, shutdown cancellation is not a failure, everything else retries. An
> open circuit reschedules without consuming an attempt and never auto-dead-letters. The DLQ
> is now durable: the `DeadLettered` store state is the source of truth, retention cleanup no
> longer deletes dead letters, and an `IDeadLetterAdministration<T>` surface provides
> non-destructive List/Get plus explicit Replay/Delete/Purge. Dead-letter observer
> (`IDeadLetterHandler<T>`) success or failure cannot delete the durable record. The volatile
> DLQ defect below is fixed.>
> **Phase 4 update (HERMES-004 implemented).** Explicit runtime lifecycle states
> (`Created/Starting/Ready/Stopping/Stopped/Faulted`) are tracked in `HermesRuntimeState`;
> publishing is allowed only in `Ready` (`HermesNotReadyException` otherwise). The per-message
> `Task.Run` + `SemaphoreSlim` orchestration is replaced by a fixed pool of async worker loops
> consuming the internal `Channel<Guid>`. Shutdown rejects new publishes first (via
> `ApplicationStopping`), then awaits in-flight handlers up to a configurable
> `ShutdownGracePeriod`, leaving the durable backlog for restart. The concurrency and lifecycle
> defects below are fixed.
>
> **Phase 5 update (HERMES-005 implemented).** Observability now describes durable state: a stable
> `Hermes.Messaging` meter exposes durable counters/histograms and gauges (`messages.pending`,
> `messages.processing`, `messages.retry_scheduled`, `deadletters.depth`), plus an `ActivitySource`
> emitting `Publish`/`Process` spans; tags are low-cardinality only (no MessageId/CorrelationId).
> Readiness (`IMessageBusDiagnostics.IsReady`) requires runtime `Ready` and completed startup
> recovery; liveness (`IsHealthy`) requires a resolvable bus and a non-`Faulted` runtime. Persisted
> records carry a `SchemaVersion`. Package metadata, README semantics, and a CHANGELOG were updated.
> Remaining before 1.0: mass-internalization of infrastructure types and the circuit-breaker
> removal/relocation are intentionally deferred (would be further breaking API changes).
Audited publish flow (baseline, before HERMES-001):
```text
PublishAsync
 -> Channel write
 -> return success
 -> subscriber reads
 -> LiteDB Persist
 -> handler
```

Therefore a process crash between successful publish and persistence permanently loses an accepted message. Publisher-visible semantics are not true at-least-once.

Current publish flow (after HERMES-001):
```text
PublishAsync
 -> validate route
 -> LiteDB Persist Pending (commit)
 -> best-effort Channel signal
 -> return PublishResult (Accepted)
 -> subscriber reads -> handler -> mark Completed
```

Once persisted, duplicates remain possible:
```text
Persist Pending
handler performs external side effect
crash before Completed
restart replays Pending
side effect happens again
```

Recovery defect (FIXED in HERMES-002):
- ~~startup loads only `Pending`;~~
- ~~replay failure may mark `Failed`;~~
- ~~`Failed` is not loaded later;~~
- startup now recovers interrupted `Processing` (and retired `Failed`) back to `Pending`;
- no status is left in an unscanned state;
- attempt count is incremented atomically at claim time.

DLQ defect (FIXED in HERMES-003):
- ~~current DLQ is an in-memory bounded Channel;~~
- ~~restart loses it;~~
- ~~consuming a dead letter removes it;~~
- ~~handler exception/no handler can effectively discard it;~~
- the durable `DeadLettered` store state is now the source of truth and survives restart;
- inspection (List/Get) is non-destructive; Replay/Delete/Purge are explicit;
- the in-memory Channel remains only as a best-effort observer hook — its failure or a full
  queue cannot delete the durable record.

Circuit breaker concerns (partially addressed in HERMES-003):
- ~~global breaker can truncate retry;~~ an open circuit now reschedules without consuming an
  attempt and never auto-dead-letters;
- HalfOpen single-probe strictness and moving resilience to a handler/dependency-owned policy
  remain open (SDD 07 recommends removing the global breaker from core before 1.0).

Concurrency concerns (FIXED in HERMES-004):
- ~~per-message `Task.Run`;~~
- ~~`SemaphoreSlim`;~~
- ~~active task list cleanup;~~
- a fixed pool of N async worker loops now consumes the internal `Channel<Guid>` directly;
- duplicate wake-ups are harmless because `TryClaim` is atomic.

Lifecycle concern (FIXED in HERMES-004):
- ~~no explicit Created/Starting/Ready/Stopping/Stopped/Faulted state;~~
- explicit `RuntimeState` (Created/Starting/Ready/Stopping/Stopped/Faulted) is now tracked;
- publishing is rejected unless the runtime is `Ready` (`HermesNotReadyException`);
- shutdown flips to `Stopping` before any drain (via `ApplicationStopping`), rejecting new
  publishes, then awaits in-flight handlers up to a configurable `ShutdownGracePeriod`,
  leaving the durable backlog for restart.
