# Changelog

All notable changes to Hermes.Messaging are documented here. This project is in alpha; breaking
changes are acceptable when required for correctness and are called out explicitly.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/).

## [0.2.0-alpha]

Durability and correctness hardening across HERMES-001 … HERMES-005.

### Added
- **Durable publish boundary (HERMES-001):** `PublishAsync` persists before signalling and returns
  a `PublishResult` (`MessageId`, `CorrelationId`, `AcceptedAt`). `PublishOptions`, `IMessageStore<T>`.
  Unique `MessageId`; non-unique `CorrelationId` (never the persistence key). Route validated before
  acceptance.
- **Durable state machine and recovery (HERMES-002):** `Processing`/`RetryScheduled` states, atomic
  `TryClaim`, startup recovery of interrupted `Processing`, due-work discovery, reconciliation loop.
- **Persistent retry and durable DLQ (HERMES-003):** `TimeProvider`-based bounded exponential backoff
  with jitter; failure classification (`RetryClassifier`, `NonRetryableException`); durable dead-letter
  state with `IDeadLetterAdministration<T>` (List/Get/Replay/Delete/Purge); open circuit reschedules
  without consuming an attempt.
- **Lifecycle and concurrency (HERMES-004):** explicit `RuntimeState`; publish gated on `Ready`
  (`HermesNotReadyException`); fixed async worker loops replace per-message `Task.Run`; bounded
  graceful shutdown (`ShutdownGracePeriod`).
- **API, observability, release (HERMES-005):** stable `Hermes.Messaging` meter with durable-state
  counters/histograms/gauges and an `ActivitySource`; readiness (`IMessageBusDiagnostics.IsReady`);
  persisted `SchemaVersion`; package metadata, README semantics, this changelog.

### Changed
- `IMessageBus.PublishAsync` now returns `ValueTask<PublishResult>` and takes `PublishOptions`.
- Retention cleanup no longer deletes dead letters (durable DLQ retained until explicit Delete/Purge).
- Shutdown no longer drains the entire backlog; unprocessed work is left durable for restart.

### Removed / Retired
- `ChannelPublishExtensions` (bypassed durability).
- `MessageStatus.Failed` retired (kept only for backward deserialization).

### Delivery semantics
- At-least-once within a single process. Duplicates are possible. **Not** exactly-once. Not a
  distributed broker and not a multi-process/multi-node queue.

### Migration
- Persisted records now carry a `SchemaVersion` (current: 1). Databases created before HERMES-001
  are not automatically migrated; the identity model changed from CorrelationId-keyed to
  MessageId-keyed. Start from a fresh store or perform an explicit migration.
