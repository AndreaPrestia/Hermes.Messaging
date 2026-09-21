# HERMES-006 — Hardening Review Fixes

## Context

HERMES-001 through HERMES-005 have already been implemented and pushed to `master`.

Current reviewed HEAD:

```text
8705e3a8fe234c3db81a231183d5a0520682df28
```

This task is **not** a feature phase.

It is a correctness and release-hardening pass based on a post-implementation review of the current repository.

Do not add new product features.

Do not re-architect the whole project.

Fix the concrete issues listed below with the smallest coherent changes.

---

# Priority 0 — Re-validate current HEAD

Before editing:

1. inspect current `master`;
2. confirm HEAD has not moved materially from the reviewed commit;
3. re-run the existing test suite;
4. verify each issue below against the current implementation.

If an issue is already fixed in newer HEAD, document that and skip it.

---

# P1 — Runtime must not become Ready before recovery completes

## Problem

`HermesLifecycle.StartAsync()` currently sets:

```text
Created -> Starting -> Ready
```

immediately.

However startup recovery happens inside each:

```text
PersistentChannelRouterSubscriber<T>.ExecuteAsync()
```

after hosted service startup begins.

`InMemoryMessageBus.PublishAsync()` currently checks only:

```text
HermesRuntimeState.EnsureReady()
```

and does not require `HermesReadiness.RecoveryComplete`.

Therefore Hermes may accept a new durable publish while startup recovery is still incomplete.

This violates the intended lifecycle contract:

```text
Created
 -> Starting
 -> store validation/recovery
 -> workers/scheduler ready
 -> Ready
```

## Required behavior

Publishing must be rejected until **all registered subscriber recoveries are complete**.

Acceptable implementation approaches:

### Preferred

Make the process-wide runtime transition to `Ready` happen only after all expected subscribers have completed startup recovery.

This may require:

- `HermesLifecycle` to remain `Starting`;
- `HermesReadiness` to signal completion;
- the final subscriber recovery completion to transition the runtime to `Ready`.

### Alternative

Keep the lifecycle state mechanics but make `PublishAsync` require both:

```text
runtime == Ready
AND
readiness.RecoveryComplete
```

This is acceptable only if state semantics remain coherent and diagnostics do not disagree with publishability.

## Required tests

Add deterministic tests for:

```text
Publish_WhileStartupRecoveryIncomplete_IsRejected
Publish_AfterAllStartupRecoveryCompletes_IsAccepted
Diagnostics_IsReady_MatchesPublishability
```

Add a slow/blocking startup-recovery test seam if needed.

Do not rely on timing sleeps alone.

---

# P2 — Prevent unbounded duplicate wake-up accumulation

## Problem

`PersistentChannelRouterSubscriber<T>` uses:

```csharp
Channel<Guid> _wakeups = Channel.CreateUnbounded<Guid>(...)
```

and every reconciliation interval executes:

```text
GetDueMessages(now)
 -> TryWrite(MessageId) for every due message
```

If processing is slower than scanning, the same due MessageIds can be appended repeatedly every second.

`TryClaim` prevents duplicate execution, but it does **not** prevent duplicate wake-up accumulation.

A large backlog can therefore cause unbounded memory growth.

## Required behavior

The notification layer must remain bounded in memory.

Accepted approaches include:

### Option A — Bounded wake-up channel + dedup set

Maintain a process-local set of MessageIds currently signalled/enqueued.

Conceptually:

```text
TrySignal(id):
    if id already outstanding:
        return

    if bounded channel accepts:
        mark outstanding

worker reads id:
    remove outstanding marker
    TryClaim(id)
```

Carefully handle races so an ID cannot become permanently suppressed.

### Option B — Bounded wake-up channel + paginated reconciliation

Use a bounded Channel and a reconciliation scan that only fills available notification capacity.

### Option C — Another simple equivalent

Any solution is acceptable if:

- notification memory is bounded;
- duplicate signals remain harmless;
- missed notifications are recovered by reconciliation;
- messages cannot become permanently stuck due to dedup bookkeeping.

## Required constraints

Do not make Channel the source of truth again.

The durable store remains authoritative.

## Required tests

At minimum:

```text
Reconciliation_DoesNotGrowWakeupsUnbounded_ForSlowConsumer
DuplicateDueScans_DoNotQueueUnboundedDuplicateIds
MissedSignal_IsEventuallyRecovered
DuplicateSignals_StillProcessOnce
```

A stress-style test with a large backlog is strongly preferred.

---

# P3 — Remove split-brain between IMessageStore<T> and PersistentMessageStore<T>

## Problem

Publishing resolves:

```csharp
IMessageStore<T>
```

but `PersistentChannelRouterSubscriber<T>` currently depends directly on:

```csharp
PersistentMessageStore<T>
```

This allows DI to be configured so that:

```text
publisher -> store A
subscriber -> store B
```

which breaks the core durability invariant.

The existing persistence-failure test already replaces `IMessageStore<T>` only, which demonstrates that the abstraction can diverge from the runtime consumer.

## Required behavior

There must be exactly one authoritative store instance per message type inside Hermes.

## Preferred implementation

Make all runtime components depend on:

```csharp
IMessageStore<T>
```

including the subscriber, diagnostics where practical, and administration surfaces.

If implementation-specific methods are still required, move those operations into the abstraction rather than downcasting.

`PersistentMessageStore<T>` remains the single production implementation.

## Alternative

If the abstraction is intended only for tests/internal seams, make that design explicit and impossible to misconfigure publicly.

Do not retain a public abstraction that permits publisher/subscriber store divergence.

## Required tests

Add:

```text
PublisherAndSubscriber_UseSameConfiguredStoreInstance
CustomStoreReplacement_IsUsedByWholeRuntime
StoreOverride_CannotCreatePublisherSubscriberSplitBrain
```

---

# P4 — Define and fix dead-letter replay retry budget

## Problem

Current dead-letter replay transitions:

```text
DeadLettered -> Pending
```

but leaves:

```text
AttemptCount
LastError
```

unchanged.

Example:

```text
MaxAttempts = 3
message dead-letters at AttemptCount = 3
operator replays
claim increments to 4
next failure immediately dead-letters again
```

That effectively gives a replay only one new attempt.

## Required decision

For Hermes 0.2 alpha, use this semantic:

> Explicit dead-letter replay starts a new processing cycle with a fresh retry budget.

Therefore replay must:

```text
DeadLettered -> Pending
AttemptCount = 0
NextAttemptAt = null
LastError = null
UpdatedAt = now
```

Retain historical dead-letter information only if there is already a simple separate audit mechanism. Do not introduce a new history subsystem in HERMES-006.

## Required tests

Add:

```text
ReplayDeadLetter_ResetsAttemptCount
ReplayDeadLetter_ClearsLastError
ReplayedMessage_GetsFullRetryBudgetAgain
```

---

# P5 — Clarify MaxAttempts semantics

## Problem

Current option is named:

```text
MaxRetryAttempts
```

but code behavior means the value is effectively:

```text
maximum total handler attempts
```

including the first attempt.

## Required behavior

Rename toward:

```text
MaxAttempts
```

with explicit semantics:

```text
MaxAttempts = total handler invocations before dead-lettering
```

Default remains equivalent to current behavior unless there is a compelling reason to change it.

Because this is still alpha, a breaking rename is acceptable.

If a temporary obsolete compatibility property is retained, it must map exactly to the same semantics and emit no conflicting behavior.

## Required tests

Add/adjust tests proving:

```text
MaxAttempts_1_DeadLettersAfterFirstFailure
MaxAttempts_3_AllowsExactlyThreeHandlerInvocations
```

---

# P6 — Stop using short CLR type names as internal identity keys

## Problem

Several components currently use:

```csharp
typeof(T).Name
```

for keys or metrics registration, including readiness/store metrics/circuit breaker logic.

Two different types with the same short name in different namespaces can collide.

The persistence file path already uses `FullName`, so internal identity behavior is inconsistent.

## Required behavior

Introduce one internal helper for stable runtime type identity.

For this hardening phase, acceptable minimum:

```csharp
typeof(T).FullName ?? typeof(T).Name
```

Use it consistently for:

- readiness keys;
- store metric provider registration;
- circuit breaker keys if the breaker remains;
- any other internal dictionaries requiring uniqueness.

Do not solve long-term schema identity here with a new public message-name registry.

The persisted stable logical message type name remains a future versioning concern.

## Required tests

Create two types with the same short name in different namespaces and prove:

```text
Readiness_DoesNotCollideForSameShortTypeName
MetricsRegistry_DoesNotCollideForSameShortTypeName
CircuitKeys_DoNotCollideForSameShortTypeName
```

where applicable.

---

# P7 — Fix cancellation race test

## Problem

The current test named similarly to:

```text
Publish_CancelAfterCommit_ReturnsAccepted
```

cancels the token only after `PublishAsync` has already returned.

That does not prove the important race:

```text
commit succeeds
token cancels
signal/result path continues
caller still receives Accepted
```

## Required behavior

Add a deterministic test seam around the publish path.

Possible approaches:

- test store callback after Insert commit;
- injected publish hook used only internally/tests;
- controllable store implementation that blocks immediately after successful insert.

Test sequence:

```text
publish starts
store commits
test detects commit completed
cancel token
release publish path
assert PublishResult returned
assert durable record exists
```

Do not use arbitrary timing sleeps.

---

# P8 — README must match the implementation

## Problem

The current README mixes new and old semantics.

Known stale/incorrect examples include:

- uses `AddHermesMessageBus` instead of current registration API;
- examples pass `CancellationToken` as the third positional argument although third parameter is now `PublishOptions`;
- claims publish backpressure waits on a full Channel;
- still documents `Failed`;
- still documents old retry loop behavior;
- still mentions `SemaphoreSlim`;
- still describes `ReplayPendingMessagesAsync`;
- still describes DLQ as volatile and permanently losing messages;
- still states dead letters are auto-cleaned;
- still references old meter name `Hermes.MessageBus`;
- project structure still lists removed `ChannelPublishExtensions`.

## Required behavior

Rewrite the README sections needed so every documented API and guarantee matches current code.

At minimum verify:

```text
registration
subscription
publishing
PublishOptions
PublishResult
delivery guarantee
lifecycle
state machine
retry semantics
DLQ semantics
diagnostics
metrics/tracing
project structure
limitations
```

Add a clear:

```text
When not to use Hermes
```

section covering:

- multi-process consumers;
- multi-instance services;
- distributed messaging;
- cross-service transport;
- exactly-once requirements;
- ephemeral storage if durability is required.

All code samples must compile conceptually against the current public API.

---

# P9 — Add CI

## Problem

Current repository has no GitHub Actions workflow and HEAD has no status checks.

Local test claims in commit messages are not enough for release confidence.

## Required behavior

Add a minimal GitHub Actions workflow:

```text
.github/workflows/ci.yml
```

Trigger on:

```text
push to master
pull_request
```

Use a .NET 10 SDK setup.

Run:

```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

Ensure the crash harness project is built before the test project attempts to invoke it.

If normal solution build already guarantees this, document that.

Prefer one simple job initially.

Do not add publishing to NuGet in HERMES-006.

---

# P10 — Clean observability duplication

## Problem

Both:

```text
HermesTelemetry
ChannelMetrics
```

emit metrics under `Hermes.Messaging`.

`ChannelMetrics` also hardcodes meter version:

```text
1.0.0
```

while package version is currently alpha.

Some old Channel metrics represent volatile notification state rather than durable backlog.

## Required behavior

Do not perform a large telemetry rewrite.

At minimum:

- remove misleading hard-coded `1.0.0`;
- clearly separate volatile notification metrics from durable store metrics;
- make `IMessageBusDiagnostics.GetBacklogCount<T>()` return a durable backlog concept, or rename/deprecate it if it remains Channel-only;
- avoid duplicate/conflicting metric semantics.

Preferred durable backlog:

```text
Pending + Processing + RetryScheduled
```

Dead letters should remain separate.

## Required tests

Verify diagnostics backlog reflects durable state, not only Channel enqueue/dequeue events.

---

# P11 — Circuit breaker cleanup

## Problem

The circuit breaker remains largely legacy code.

Known issues:

- internal key uses short type name;
- HalfOpen transition is computed but not atomically gated to one probe;
- resilience policy is arguably better owned by handlers/dependency clients.

## Scope for HERMES-006

Do **not** build a more complex circuit breaker.

Preferred action:

### Option A — Remove it from core

Remove automatic circuit breaker behavior from the subscriber and clean related diagnostics/tests.

This is the recommended direction.

### Option B — Keep temporarily but make safe

Only if removal would cause disproportionate breaking work:

- fix type key identity;
- ensure HalfOpen allows a single probe;
- document it as legacy/deprecated.

Do not expand feature surface.

---

# P12 — HERMES-005 completion gap

The original HERMES-005 scope included:

- CI/release checks;
- benchmark project;
- public API cleanup/internalization.

For HERMES-006:

### Required now

- CI;
- README correctness;
- obvious implementation-internal type exposure cleanup where low-risk;
- telemetry version/backlog cleanup.

### Explicitly allowed to defer

- BenchmarkDotNet project;
- broad public API internalization;
- package publishing workflow;
- SourceLink if it adds non-trivial complexity.

If deferred, update the roadmap/CHANGELOG honestly.

Do not claim HERMES-005 is fully complete if these items remain deferred.

---

# Additional review checks

While implementing the above, verify these invariants have not regressed:

```text
Accepted => persisted
unknown route => not persisted
duplicate signal => one simultaneous execution
Processing crash => recoverable
RetryScheduled survives restart
DeadLettered survives restart
observer failure does not delete durable DLQ
shutdown leaves unfinished work durable
```

Do not broaden scope unless a newly discovered issue threatens one of those invariants.

---

# Validation

Run:

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

Then, if GitHub Actions is added and push permissions are available, verify the CI workflow itself reaches green.

Do not claim CI is green merely because the YAML exists.

---

# Commit strategy

Prefer focused commits such as:

```text
fix: gate readiness on startup recovery
fix: bound and deduplicate reconciliation wakeups
refactor: use message store abstraction consistently
fix: reset retry budget on dead-letter replay
refactor: clarify max attempts semantics
fix: avoid short type-name identity collisions
test: cover post-commit cancellation race
docs: align README with durable runtime
ci: add .NET 10 build and test workflow
refactor: clean telemetry backlog semantics
refactor: remove legacy core circuit breaker
```

Do not squash unrelated correctness fixes into one opaque commit unless the environment requires it.

---

# Acceptance criteria

HERMES-006 is complete only when:

```text
1. Publish cannot succeed before startup recovery is complete.
2. Reconciliation notification memory is bounded.
3. Publisher and subscriber cannot use different authoritative stores.
4. DLQ replay gets a fresh retry budget.
5. MaxAttempts semantics are explicit and tested.
6. Internal type keys do not collide on same short CLR name.
7. The post-commit cancellation race is tested deterministically.
8. README matches the actual current API/semantics.
9. GitHub Actions CI exists and runs restore/build/test on .NET 10.
10. Durable backlog diagnostics are not based only on the Channel.
11. Circuit breaker is removed or explicitly hardened/deprecated.
12. Existing durability/recovery/DLQ invariants remain green.
```

---

# Completion report

Return:

```text
## Summary
## Reviewed HEAD
## Branch
## Commits
## Findings fixed
## Findings deferred
## Files changed
## Breaking changes
## README/API corrections
## CI workflow
## Tests added/changed
## Commands executed
## Test results
## CI result
## Remaining risks
## Recommendation for 0.2.0-alpha release
```

Then stop.

Do not start a new feature phase.
