# HERMES-003 — Persistent Retry and Durable DLQ

Precondition: HERMES-002 merged and green.

Implement retry:
```text
AttemptCount
NextAttemptAt
LastError
bounded exponential backoff + jitter
TimeProvider
```

Do not hold workers in long Task.Delay.

Implement durable DLQ:
```text
DeadLettered
List
Get
Replay
Delete
Purge
```

Observer/dead-letter handler failure must not delete durable records.

Review/remove core circuit breaker if it conflicts with retry semantics.

Tests:
- retry survives restart;
- correct attempt count;
- poison -> DeadLettered;
- dead letter survives restart;
- replay -> Pending;
- observer exception retains record;
- purge/delete.

Run full validation and stop.
