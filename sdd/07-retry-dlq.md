# 07 — Retry and Dead Letter Queue

Prefer `MaxAttempts` when the value includes the initial invocation.

Retry state must be durable:
```text
AttemptCount
NextAttemptAt
LastError
```

Transition:
```text
Processing -> RetryScheduled
```
The scheduler wakes it later. Do not hold a worker in long `Task.Delay`.

Use bounded exponential backoff with jitter and `TimeProvider`.

Retry classification must eventually distinguish non-retryable programming/configuration failures, transient dependency failures, and shutdown cancellation.

Recommended circuit-breaker direction: remove the global breaker from core before 1.0 or make resilience handler/dependency-owned. An open circuit must not automatically mean dead-letter.

DLQ must be durable: `DeadLettered` is a persistent state.

Operational surface should support:
```text
List
Get
Replay
Delete
Purge
```
with useful filters and paging.

`IDeadLetterHandler<T>` may remain only as an observer hook. Observer success/failure must not delete the durable record.
