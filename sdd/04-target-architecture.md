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
