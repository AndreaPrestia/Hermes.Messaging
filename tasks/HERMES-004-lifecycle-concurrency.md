# HERMES-004 — Lifecycle and Concurrency

Precondition: HERMES-003 merged and green.

Implement runtime states:
```text
Created
Starting
Ready
Stopping
Stopped
Faulted
```

Publish only in Ready.

Replace per-message Task.Run orchestration with fixed async worker loops.

Shutdown:
```text
reject new publishes
stop scheduler
await in-flight up to configured grace period
leave durable backlog for restart
```

Do not drain all backlog.

Tests:
- publish before Ready rejected;
- publish during Stopping rejected;
- concurrent workers;
- duplicate signals;
- in-flight shutdown;
- shutdown timeout;
- restart continues backlog.

Run full validation and stop.
