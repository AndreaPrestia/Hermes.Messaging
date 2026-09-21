# HERMES-002 — Durable State Machine and Recovery

Precondition: HERMES-001 merged and green.

Implement:
```text
Pending -> Processing -> Completed
Processing -> RetryScheduled
Processing -> DeadLettered
RetryScheduled -> Processing
DeadLettered -> Pending  # explicit replay
```

Startup:
```text
Processing -> Pending
```

Add:
- TryClaim(MessageId);
- due-work scan;
- scheduler/reconciliation loop;
- Channel<MessageId> notification;
- duplicate-signal safety;
- retire ambiguous Failed state.

Tests must cover real crash windows around commit, signal, claim, handler and complete.

Do not redesign DLQ administration or concurrency beyond what is necessary. Run full validation and stop.
