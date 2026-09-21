# 05 — Domain and Persistence Model

Conceptual durable envelope:
```text
MessageId
CorrelationId
MessageType
Route
CreatedAt
Payload
Headers
State
AttemptCount
NextAttemptAt
LastError
LastUpdatedAt
SchemaVersion
TraceParent
TraceState
```

Not every field must be public.

Identity:
- `MessageId` unique;
- `CorrelationId` non-unique.

States:
```text
Pending
Processing
RetryScheduled
Completed
DeadLettered
```

Introduce `IMessageStore<T>` or equivalent, without exposing LiteDB types.

It should evolve to support:
```text
Insert
Get
TryClaim
MarkCompleted
ScheduleRetry
MarkDeadLettered
RecoverInterrupted
GetDueMessages
ListDeadLetters
ReplayDeadLetter
Delete/Purge
CleanupCompleted
```

Correctness-sensitive transitions must be atomic within one Hermes process.

Stored messages require a schema/payload version strategy before 1.0. Incompatible payloads must not silently disappear.

Do not identify durable message types solely by `typeof(T).Name`; define a stable logical identity before 1.0.
