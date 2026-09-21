# 13 — Acceptance Criteria

## Durability
- successful publish means durable commit already happened;
- lost Channel signal cannot lose accepted message;
- persistence failure cannot return Accepted;
- post-commit cancellation cannot create ambiguity.

## Identity
- MessageId unique;
- CorrelationId may be caller supplied;
- multiple messages may share CorrelationId.

## Routing
- unknown route/type rejected before persistence;
- exactly one handler per `{T, route}`;
- case-insensitive route matching.

## Recovery
- interrupted Processing is recovered;
- RetryScheduled survives restart;
- no message is stranded in an unscanned status.

## Retry
- attempts persisted;
- NextAttemptAt persisted;
- worker is not held by long retry delay;
- policy testable.

## DLQ
- dead letters survive restart;
- inspection is non-destructive;
- replay explicit;
- observer callback cannot erase durable record.

## Lifecycle
- explicit states;
- publish only in Ready;
- shutdown rejects new work first;
- backlog remains durable for restart.

## Concurrency
- no Task.Run per async message;
- duplicate signals do not cause simultaneous duplicate execution in one runtime;
- ordering limitations documented.

## Observability
- metrics reflect durable state;
- logs distinguish failure classes;
- payload not logged by default;
- IDs not metric tags.

## Product boundary
- no distributed dependency;
- no multi-process guarantee;
- no exactly-once claim.
