# 01 — Current State

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
> DLQ defect below is fixed.

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

Concurrency concerns:
- per-message `Task.Run`;
- `SemaphoreSlim`;
- active task list cleanup;
- fragile shutdown/exception observation.

Lifecycle concern:
- no explicit Created/Starting/Ready/Stopping/Stopped/Faulted state;
- publishing can happen before recovery is complete or with no valid consumer.
