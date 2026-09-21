# HERMES-007 — Bounded Reconciliation and Backlog Hardening

## Context

HERMES-001 through HERMES-006 are already implemented.

Reviewed baseline:

```text
master
94761d9bf7a08d851465a7f50e40d021c778274a
```

GitHub Actions is green on this baseline:

```text
.NET 10
108 tests passed
0 failed
0 warnings
0 errors
```

This task is intentionally narrow.

Do not add new product features.
Do not redesign the durable state machine.
Do not change the single-process product boundary.

## Objective

Eliminate the remaining large-backlog inefficiency in reconciliation.

Today the in-memory wake-up channel is bounded and deduplicated, which prevents unbounded notification growth.

However reconciliation still effectively does:

```text
GetDueMessages(now)
 -> materialize every Pending / due RetryScheduled record
 -> iterate every record
 -> TrySignal(MessageId)
```

`PersistentMessageStore.GetDueMessages()` materializes the full result set with `ToList()`.

With a large backlog this can repeatedly load very large numbers of persisted messages, including payload bodies, even though the wake-up channel has capacity for only a bounded number of IDs.

Potential effects:

- large temporary allocations;
- excessive LiteDB reads;
- unnecessary payload deserialization;
- GC pressure;
- avoidable CPU usage;
- reconciliation latency spikes.

# P1 — Add bounded due-message ID query

Add a store API equivalent to:

```csharp
IReadOnlyList<Guid> GetDueMessageIds(
    DateTimeOffset now,
    int limit);
```

Required semantics:

```text
Status == Pending

OR

Status == RetryScheduled
AND NextAttemptAt <= now
```

Return at most `limit` IDs, in deterministic oldest-first order consistent with Hermes semantics.

Critical requirement: enforce the limit at the database/query level before materializing the result. Avoid loading full `PersistedMessage<T>` payloads only to obtain IDs when LiteDB projection can reasonably avoid it.

For `limit <= 0`, either return empty or throw a clear argument exception. Pick one behavior and test it. Never turn it into an unlimited query.

# P2 — Query only useful notification capacity

Preserve the bounded wake-up channel and `_outstanding` dedup set.

Before querying the durable store:

```text
available = WakeupCapacity - outstandingCount

if available <= 0:
    skip durable due-work query

ids = store.GetDueMessageIds(now, available)

for each id:
    TrySignal(id)
```

A small bounded overscan is acceptable if justified, but never scan the entire durable backlog.

Correctness constraints:

- durable store remains source of truth;
- if signal fails because the channel fills concurrently, keep the message durable;
- release any failed outstanding reservation;
- reconciliation must be able to pick the message up later;
- no permanent suppression.

# P3 — Bound startup seeding

Startup due-work seeding must use the same bounded ID query.

Startup does not need to enqueue the whole durable backlog. It only needs enough wake-ups to start workers; reconciliation refills as workers make progress.

Preserve:

```text
fast startup
bounded memory
durable backlog
eventual processing
```

# P4 — Refill behavior

The existing ~1 second reconciliation interval is acceptable for 0.2 alpha.

Confirm:

```text
worker progresses
outstanding entry is removed
next reconciliation fills newly available capacity
```

Do not add a complicated scheduler.

# P5 — Required tests

Add deterministic store tests:

```text
GetDueMessageIds_RespectsLimit
GetDueMessageIds_ReturnsPendingAndDueRetriesOnly
GetDueMessageIds_OrdersOldestFirst
GetDueMessageIds_LimitZero_DoesNotScanAll
```

Add reconciliation/integration tests:

```text
Reconciliation_DoesNotQueryMoreThanAvailableWakeupCapacity
DuplicateDueScans_DoNotAccumulateUnboundedWakeups
MissedSignal_IsEventuallyRecovered
LargeBacklog_IsEventuallyProcessed_InBoundedBatches
```

Use internal/test-only hooks or counters if needed. Prove behavior; avoid fragile reflection into implementation details.

# P6 — Large-backlog stress-style test

Add a non-benchmark integration/stress test.

Suggested CI scale:

```text
5,000 to 20,000 durable Pending messages
```

Choose a size large enough to require multiple bounded refill cycles but stable on GitHub Actions.

Verify:

```text
startup succeeds without signalling/materializing the full backlog
processing proceeds in multiple bounded batches
all messages eventually complete
peak requested due batch size never exceeds the configured/bounded limit
```

If 20k is too slow for CI, use a smaller number and document why.

Do not add BenchmarkDotNet here.

# P7 — Full replay retry-budget integration test

HERMES-006 resets the replay state correctly. Add the missing end-to-end test:

```text
ReplayedMessage_GetsFullRetryBudgetAgain
```

Scenario:

```text
MaxAttempts = 3

first cycle:
  fail x3
  -> DeadLettered
  AttemptCount == 3

Replay

second cycle:
  fail x3
  -> DeadLettered again

total handler invocations == 6
second-cycle durable AttemptCount == 3
```

Use short retry delays and deterministic waiting.

# P8 — Documentation cleanup

Fix the stale README TOC entry:

```text
Retry & Circuit Breaker
```

Use the actual section name such as:

```text
Retry Semantics
```

Search current documentation for active claims that Hermes still includes a core circuit breaker:

```text
circuit breaker
CircuitBreaker
circuit breaking
```

Historical changelog/audit references are allowed when clearly historical. Current feature descriptions must be accurate.

# P9 — GitHub repository description

The repository description still mentions `circuit breaking`.

If tooling permits, update it to something equivalent to:

```text
An in-process, single-process durable message bus for .NET built on System.Threading.Channels and LiteDB, with crash recovery, persistent retry, durable dead-lettering, and OpenTelemetry-compatible observability.
```

If metadata editing is unavailable, do not fail the task. Put the exact desired replacement in the completion report.

# P10 — Keep public semantics unchanged

Do not change:

```text
Accepted => durable
at-least-once
duplicates possible
one handler per {T, route}
single process / single instance
durable DLQ
explicit replay
MaxAttempts total invocation semantics
publish only when Ready + recovery complete
```

No distributed behavior. No alternate storage provider. No broker abstraction.

# P11 — Performance note

Add a short note in the relevant SDD or README architecture section:

Before:

```text
reconciliation could materialize the entire due backlog
```

After:

```text
reconciliation queries only a bounded number of due MessageIds based on available wake-up capacity
```

Do not claim benchmarked speedups. Say only that this reduces avoidable allocations/store scans for large backlogs.

# Validation

Run:

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

Push, then verify the resulting GitHub Actions run.

The completion report must distinguish local tests from GitHub Actions. Do not call CI green before the run completes.

# Recommended commits

```text
perf: bound durable reconciliation queries
test: cover bounded reconciliation and large backlog
test: verify replay receives a fresh retry budget
docs: remove stale circuit breaker references
```

# Acceptance criteria

HERMES-007 is complete only when:

```text
1. Reconciliation never requests the entire due backlog when only bounded wake-up capacity is useful.
2. Startup due-work seeding is bounded.
3. Store due-work query accepts and enforces a limit.
4. Due-work reconciliation operates on IDs rather than full message bodies where reasonably possible.
5. Duplicate scans do not grow notification memory.
6. Missed signals are eventually recovered.
7. Large backlog processing progresses through bounded batches.
8. Explicit DLQ replay receives a complete fresh retry budget.
9. Current documentation contains no active claim that Hermes core has a circuit breaker.
10. GitHub Actions is green.
11. Existing delivery/lifecycle/DLQ semantics remain unchanged.
```

# Explicit non-goals

Do NOT implement:

- BenchmarkDotNet;
- distributed broker support;
- multi-process support;
- alternate durable stores;
- public scheduler API;
- priority queues;
- partitioning;
- fan-out/pub-sub;
- exactly-once;
- broad public API internalization;
- NuGet publishing workflow;
- SourceLink;
- persistence schema migration framework.

# Completion report

Return exactly:

```text
## Summary
## Reviewed HEAD
## Branch
## Commits
## Reconciliation behavior before
## Reconciliation behavior after
## Store API changes
## Tests added/changed
## Large-backlog test
## Replay-budget test
## Documentation changes
## Repository metadata change
## Commands executed
## Local test results
## GitHub Actions run
## GitHub Actions result
## Remaining risks
## Recommendation for 0.2.0-alpha
```

Then stop.

Do not start HERMES-008.
