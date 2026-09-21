# 14 — Open Questions

Resolve only when the relevant phase is reached.

## Q1 Store layout
Keep one LiteDB file per T or move to one DB/multiple collections?

Recommendation: keep current per-type layout behind `IMessageStore<T>` until benchmarks/operations justify change.

## Q2 Circuit breaker
Recommendation: remove from core before 1.0. Dependency resilience normally belongs inside the handler/client.

## Q3 Stable message type identity
Need an identity surviving namespace/assembly refactors. Candidates:
- configured logical name;
- attribute;
- registration-time name.

Do not rely solely on CLR short name.

## Q4 Retry classification
Possible mechanisms:
- exception policy;
- handler result;
- registration predicate;
- classifier service.

Keep it simple.

## Q5 DLQ admin surface
Recommendation: separate operational service/store rather than crowding `IMessageBus`.

## Q6 Scheduler interval
Correctness first, then benchmark. A periodic scan is required to recover missed notifications.

## Q7 Legacy store migration
Before changing persistence identity/schema choose:
- automatic migration;
- migration tool;
- explicit alpha reset.

Never silently orphan backlog.
