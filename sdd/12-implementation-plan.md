# 12 — Implementation Plan

## Phase 0 — Characterization
Freeze actual behavior with tests:
- publish-before-persist;
- recovery stranding;
- volatile/destructive DLQ;
- concurrency/shutdown;
- baseline build/test.

No redesign.

## Phase 1 — Durable publish boundary
Implement:
- MessageId;
- non-unique CorrelationId;
- PublishOptions;
- PublishResult;
- IMessageStore<T>;
- route validation;
- persist-before-signal;
- deterministic cancellation;
- subscriber no longer inserts duplicate persisted message.

## Phase 2 — Durable state machine and recovery
Implement:
```text
Pending
Processing
RetryScheduled
Completed
DeadLettered
```
Add:
- TryClaim;
- interrupted Processing recovery;
- due-work discovery;
- periodic reconciliation;
- lost-signal recovery.

## Phase 3 — Persistent retry and DLQ
Implement:
- durable attempt count;
- NextAttemptAt;
- jittered backoff;
- retry classification;
- durable DLQ;
- list/get/replay/delete/purge.

## Phase 4 — Lifecycle and concurrency
Implement:
- explicit runtime states;
- publish only in Ready;
- fixed async worker loops;
- bounded graceful shutdown;
- no per-message Task.Run.

## Phase 5 — API and observability
Implement:
- public API cleanup;
- internalize infrastructure classes;
- tracing;
- durable-state metrics;
- health/readiness;
- accurate docs.

## Phase 6 — Performance and packaging
Implement:
- benchmarks;
- CI;
- package metadata;
- SourceLink if appropriate;
- README/changelog;
- release gates;
- compatibility policy.

Optimize only after measurement.
