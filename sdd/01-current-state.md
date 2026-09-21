# 01 — Current State

> **Phase 1 update (HERMES-001 implemented).** The publish-before-persist defect
> described below has been fixed. `PublishAsync` now validates the route, durably
> persists the message (Pending) with a unique `MessageId`, and only then does a
> best-effort Channel signal before returning an accepted `PublishResult`. The
> subscriber no longer inserts a duplicate durable record. The remaining defects
> below (duplicate replay, recovery stranding, volatile DLQ) are still open and are
> addressed by later phases.

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

Recovery defect:
- startup loads only `Pending`;
- replay failure may mark `Failed`;
- `Failed` is not loaded later;
- persisted attempt counts do not accurately reflect runtime attempts.

DLQ defect:
- current DLQ is an in-memory bounded Channel;
- restart loses it;
- consuming a dead letter removes it;
- handler exception/no handler can effectively discard it.

Circuit breaker concerns:
- global breaker can truncate retry;
- HalfOpen is not a strict single probe;
- temporary outages may become dead letters.

Concurrency concerns:
- per-message `Task.Run`;
- `SemaphoreSlim`;
- active task list cleanup;
- fragile shutdown/exception observation.

Lifecycle concern:
- no explicit Created/Starting/Ready/Stopping/Stopped/Faulted state;
- publishing can happen before recovery is complete or with no valid consumer.
