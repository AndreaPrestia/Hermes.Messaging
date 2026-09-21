# 04 — Target Architecture

```text
PublishAsync
   |
   v
+----------------------+      commit
| Durable Message Store| <---------------- source of truth
+----------+-----------+
           |
           | MessageId
           v
+----------------------+
| In-memory Channel    |      notification/acceleration only
+----------+-----------+
           |
           v
+----------------------+
| Worker(s)            |
+----------+-----------+
           |
           v
+----------------------+
| Handler              |
+----------------------+
```

A scheduler/reconciliation loop discovers:
- `Pending`;
- `RetryScheduled` with `NextAttemptAt <= now`.

Workers consume MessageIds and atomically `TryClaim`.

Duplicate signals must be harmless.

> **Bounded reconciliation (HERMES-007).** Before: reconciliation could materialize the entire due
> backlog (`GetDueMessages` loaded every due `PersistedMessage<T>`, payloads included). After:
> reconciliation queries only a bounded number of due `MessageId`s based on the currently available
> wake-up capacity (`GetDueMessageIds(now, limit)` — limit enforced at the query level, IDs only).
> Startup seeding uses the same bounded query. This reduces avoidable allocations and store scans
> for large backlogs (not a benchmarked speedup); the durable store remains authoritative and any
> work not signalled this cycle is recovered by a later reconciliation.

Recommended state machine:
```text
Pending -> Processing -> Completed
Processing -> RetryScheduled
Processing -> DeadLettered
RetryScheduled -> Processing
DeadLettered -> Pending   # explicit replay only
```

Startup:
```text
Processing left by crash -> Pending
```

Avoid ambiguous `Failed`.

Alternatives:
- persist-before-enqueue alone: improves current behavior but leaves Channel too authoritative;
- durable inbox + in-memory notification: recommended;
- WAL: reject for now; it adds a second persistence mechanism without a demonstrated need.
