# HERMES-001 — Durable Publish Boundary

Implement SDD Phase 1 only.

Required:
1. MessageId.
2. CorrelationId separate/non-unique.
3. PublishOptions.
4. PublishResult.
5. smallest useful IMessageStore<T>.
6. persist before Channel signal.
7. validate route before acceptance.
8. persistence failure cannot return Accepted.
9. cancellation after commit cannot look unaccepted.
10. subscriber must not insert duplicate durable record.

Required tests, at minimum:
```text
Publish_Accepted_IsAlreadyPersisted
Publish_PersistenceFailure_IsNotAccepted
Publish_SameCorrelationId_GetsDistinctMessageIds
Publish_MissingCorrelationId_GeneratesOne
Publish_UnknownRoute_IsRejectedWithoutPersistence
Publish_NotificationFailure_RetainsDurableRecord
Publish_CancelBeforeCommit_NotAccepted
Publish_CancelAfterCommit_ReturnsAccepted
```

Add a real child-process crash test if feasible. Graceful StopAsync is not a crash test.

Non-goals:
- full state machine;
- durable retry scheduler;
- durable DLQ admin API;
- worker-loop rewrite;
- lifecycle state machine;
- circuit breaker redesign;
- full observability;
- benchmarks;
- distributed support.

Run restore/build/test and stop.
