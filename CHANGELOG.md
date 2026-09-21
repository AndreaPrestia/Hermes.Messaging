# Changelog

All notable changes to Hermes.Messaging are documented here. This project is in alpha; breaking
changes are acceptable when required for correctness and are called out explicitly.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/).

## [0.3.0-alpha] — 2026-09-21

Maturity / beta-preparation pass. No messaging-architecture changes: benchmarks found no
correctness or well-understood performance defect, so the durable-store-as-source-of-truth design
is unchanged.

### Added
- **Benchmark project** `benchmarks/Hermes.Messaging.Benchmarks` (BenchmarkDotNet): durable publish
  (100 B / 1 KB / 10 KB), end-to-end processing (MaxConcurrency 1/4/16), message-type scaling
  (1/10/50/100), and a long-running backlog-recovery benchmark plus a fast single-shot backlog probe.
- **`docs/performance/benchmark-baseline.md`** — baseline results, environment, and honest
  observations (measured facts vs interpretation).
- **`docs/api/public-api-review.md`** — full public-API classification (KEEP / OBSOLETE /
  INTERNALIZE-BEFORE-BETA / NEEDS-DESIGN).
- **`docs/architecture/adr-storage-versioning.md`** — storage identity + versioning ADR.
- **Fail-fast store schema guard:** opening a durable store written by a newer schema than the
  running build throws `StoreSchemaMismatchException` (never silently orphans a backlog).
- **Package smoke test** `tests/Hermes.Messaging.PackageSmokeTest` — consumes the packed NuGet and
  exercises the public consumer surface; wired into CI after `dotnet pack`.

### Changed
- **Package hardening:** ship XML documentation, `EmbedUntrackedSources`, and
  `ContinuousIntegrationBuild` in CI. SourceLink is provided by the .NET 10 SDK (no external
  `Microsoft.SourceLink.GitHub` package — avoids the transitively-vulnerable
  `Microsoft.Build.Tasks.Git`, NU1902).
- **CI** now also runs `dotnet pack` and the package smoke test.
- Version bumped to `0.3.0-alpha`.

### Deferred (documented, not implemented)
- Internalizing implementation types (`PersistentMessageStore<T>`, `ChannelRegistry`,
  `PersistentChannelRouterSubscriber<T>`, etc.) — they are coupled to the public subscriber
  constructor and must be internalized together as one coordinated breaking change before beta.
- Stable logical message-type identity (decouple from CLR names) and any record-shape migration.

### Notes
- **License/authors metadata discrepancy** surfaced: `LICENSE` copyright holder is `Kakama`, while
  the NuGet `<Authors>` is `Andrea Prestia`. Both declare MIT. Ownership attribution was **not**
  changed — this **requires a maintainer decision before public package release** and is
  intentionally left as-is for now.

### Cleanup pass (post-maturity review)

Corrections found reviewing the maturity pass. No messaging-architecture or delivery-semantics
changes.

- **Benchmark harness fixes (no Hermes behavior change):**
  - Publish benchmark now uses per-iteration store isolation so the LiteDB file no longer grows
    across the run (the old figures over-reported latency and ~10× the allocation).
  - End-to-end and backlog benchmarks/probe now wait for **durable** completion (store backlog = 0),
    not for the handler callback to return.
  - `docs/performance/benchmark-baseline.md` re-run and updated; superseded numbers retained as
    clearly-labelled historical data.
- **Store schema guard hardening:**
  - Schema-compatibility validation now runs **before** any index mutation, so an older build never
    modifies a newer-schema store before refusing it.
  - Schema version is recorded in a small O(1) `hermes_meta` document, avoiding a full-collection
    scan on every open for large stores (legacy stores without the document validate once via
    records, then upgrade). Newer/unknown schema is still never treated as compatible.
- **Packaging / CI:**
  - Package version is derived from the single `<Version>` in `Hermes.Messaging.csproj` (CI + smoke
    test) — no duplicated version literals.
  - CI adds a SourceLink / package-metadata verification step (SDK-provided SourceLink; validates a
    portable PDB with a Source Link document map pointing at the repository + commit).
- **API review doc fix:** corrected a false claim that `ChannelRouteRegistration<T>` and
  `HermesStoreMetrics` were already internal — they remain **public** and are listed under the
  coordinated pre-beta internalization group (not internalized in this pass).

## [0.2.0-alpha] — 2026-09-21

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
