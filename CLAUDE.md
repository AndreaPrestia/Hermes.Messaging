# CLAUDE.md — Hermes.Messaging

Repository: `https://github.com/AndreaPrestia/Hermes.Messaging`

Current compatibility baseline:
- tag: `v0.5.0-beta`
- commit: `9ff5d93e599919aa0d96a318ef751cf494e87cd3`

This tagged beta commit is the **compatibility reference**, not an instruction to work from an old commit. **Before every task, inspect the current HEAD; never assume this documented commit is current HEAD.** Code is authoritative for current behavior; these SDDs are authoritative for intended target behavior.

## Reading order
Read `sdd/00-overview.md` through `sdd/14-open-questions.md`, then execute only the requested file under `tasks/`.

## Product rules
Hermes remains an in-process, single-process/single-instance .NET 10 message bus with local durability. Do not turn it into RabbitMQ, Azure Service Bus, Kafka, MassTransit, NServiceBus, Redis Streams, a remote transport, or a distributed broker.

Correctness remains the highest priority. Starting with `v0.5.0-beta`, the public API is a tracked compatibility contract. Breaking public API changes must be exceptional, explicitly justified, documented in CHANGELOG/release notes, reflected through `PublicApiAnalyzers`, and accompanied by an appropriate versioning decision. Do not break the shipped API merely because a refactor would be cleaner.

## Public API contract
The public surface is guarded by `Microsoft.CodeAnalysis.PublicApiAnalyzers`:
- `src/Hermes.Messaging/PublicAPI.Shipped.txt` = the beta compatibility floor.
- `src/Hermes.Messaging/PublicAPI.Unshipped.txt` = deliberate API changes made after the beta freeze.

Rules: never delete a shipped API entry just to silence the analyzer; record new API in `PublicAPI.Unshipped.txt`; breaking changes require explicit justification; do not weaken RS0016/RS0017; prefer internal refactors that leave the public surface unchanged. See `docs/api/public-api-review.md` for the full baseline workflow.

## Core invariant
If `PublishAsync` returns an accepted result, the message has already been durably committed.

The durable store is the source of truth. The Channel is only a wake-up/acceleration mechanism.

Duplicates are possible. Never claim exactly-once.

## Working method
For each phase:
1. inspect current HEAD;
2. inspect relevant SDD/docs;
3. characterize current behavior (add/adjust tests);
4. before changing any public type/member, inspect `PublicAPI.Shipped.txt`;
5. implement the smallest coherent change;
6. run targeted tests;
7. run the full build/suite;
8. inspect the public API analyzer delta — verify `PublicAPI.Unshipped.txt` contains only intentional deltas;
9. update only docs affected by the change;
10. stop at the task boundary.

Do not jump ahead because a larger refactor feels cleaner.

## Validation
Run:
```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
```
Never claim a command passed unless it was executed.

## Completion report
Return:
```text
## Summary
## Branch
## Commits
## Files changed
## Public API changes
## Delivery semantics before/after
## Tests added
## Commands executed
## Test results
## Known limitations
## Migration notes
## Risks discovered
## Recommended next task
```
