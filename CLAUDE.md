# CLAUDE.md — Hermes.Messaging

Repository: `https://github.com/AndreaPrestia/Hermes.Messaging`

Audit baseline:
- branch: `master`
- commit: `551bcf2791097629f1092a20a6fab3e6dffbdd5b`

Before changing code, inspect current HEAD. Code is authoritative for current behavior; these SDDs are authoritative for intended target behavior.

## Reading order
Read `sdd/00-overview.md` through `sdd/14-open-questions.md`, then execute only the requested file under `tasks/`.

## Product rules
Hermes remains an in-process, single-process/single-instance .NET 10 message bus with local durability. Do not turn it into RabbitMQ, Azure Service Bus, Kafka, MassTransit, NServiceBus, Redis Streams, a remote transport, or a distributed broker.

Correctness beats compatibility during alpha.

## Core invariant
If `PublishAsync` returns an accepted result, the message has already been durably committed.

The durable store is the source of truth. The Channel is only a wake-up/acceleration mechanism.

Duplicates are possible. Never claim exactly-once.

## Working method
For each phase:
1. inspect current code;
2. add characterization/failing tests;
3. implement the smallest coherent change;
4. run targeted tests;
5. run the full suite;
6. update only docs affected by the semantic change;
7. stop at the phase boundary.

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
