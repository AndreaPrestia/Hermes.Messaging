# Changelog

All notable changes to Hermes.Messaging are documented here. This project is in alpha; breaking
changes are acceptable when required for correctness and are called out explicitly.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/).

## [0.2.0-alpha]

Durability and correctness hardening across HERMES-001 … HERMES-007.

### Bounded reconciliation (HERMES-007)
- Added `IMessageStore<T>.GetDueMessageIds(now, limit)` — enforces the limit at the query level and
  projects `MessageId` only (no payloads). `limit <= 0` throws `ArgumentOutOfRangeException`.
- Reconciliation and startup seeding now request only as many due IDs as the wake-up channel can
  currently accept (`WakeupCapacity - outstanding`), instead of materializing the entire due backlog.
- No semantic changes; the durable store remains the source of truth and missed signals are still
  recovered. Added store, reconciliation, large-backlog (6k), and replay retry-budget tests.
- Docs: fixed the stale "Retry & Circuit Breaker" README TOC entry and added a bounded-reconciliation
  note to the README/SDD architecture.

### Hardening review (HERMES-006)
- **P1** Publishing is rejected until startup recovery completes; recovery runs synchronously during
  host start. `IMessageBusDiagnostics.IsReady` matches publishability exactly.
- **P2** Bounded, de-duplicated wake-up notification — reconciliation for a slow consumer no longer
  grows notification memory; missed signals are still recovered.
- **P3** Runtime components depend on `IMessageStore<T>`; publisher and subscriber cannot use
  different stores. `GetStats`/`CleanupOldMessages` moved onto the abstraction.
- **P4** Dead-letter replay resets the retry budget (`AttemptCount = 0`, error/next-attempt cleared).
- **P5** `MessageBusOptions.MaxRetryAttempts` → `MaxAttempts` (total handler invocations incl. first);
  obsolete alias retained.
- **P6** `TypeIdentity.Key<T>()` (FullName-based) prevents short-name key collisions.
- **P7** Deterministic post-commit cancellation test (store commit hook cancels the token, publish
  still returns Accepted).
- **P8** README rewritten to match the durable runtime, API, and semantics; added "When not to use".
- **P9** GitHub Actions CI (`.github/workflows/ci.yml`) runs restore/build/test on .NET 10.
- **P10** Removed hard-coded meter version; durable backlog diagnostics come from the store, not the
  channel; volatile channel metrics are clearly separated.
- **P11** Removed the legacy circuit breaker from core.

#### Deferred (tracked, not done in HERMES-006)
- BenchmarkDotNet project.
- Broad public-API internalization of infrastructure types.
- NuGet publishing workflow and SourceLink.
- These items keep the original HERMES-005 scope **not fully complete**; they remain on the roadmap.

### Original HERMES-001 … HERMES-005 changes

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
