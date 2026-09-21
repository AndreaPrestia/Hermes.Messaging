# 09 — Observability

Observability must describe durable state, not only Channel state.

Use stable meter name:
```text
Hermes.Messaging
```

Target counters/histograms:
```text
messages.published
messages.persisted
publish.failed
signal.missed
processing.started
processing.succeeded
processing.failed
processing.retried
processing.deadlettered
processing.replayed
publish.duration
processing.duration
backpressure.duration
```

Target gauges:
```text
messages.pending
messages.processing
messages.retry_scheduled
deadletters.depth
```

Low-cardinality tags may include:
```text
message_type
route
outcome
```

Do not use MessageId or CorrelationId as metric labels.

Tracing should eventually use `ActivitySource("Hermes.Messaging")` with conceptual spans:
```text
Publish -> Persist -> Signal
Process -> QueueWait -> Handle -> RetrySchedule/DeadLetter/Complete
```

Do not log payloads by default.

Readiness should require runtime `Ready`, initialized store, and completed startup recovery.
