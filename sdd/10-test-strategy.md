# 10 — Test Strategy

The most important tests prove crash/state behavior.

## Layers

Unit:
- route lookup;
- state transitions;
- lifecycle guards;
- retry calculations;
- identity semantics;
- cancellation boundaries.

LiteDB integration:
- insert/get;
- claim;
- state transitions;
- recovery;
- cleanup;
- schema/version behavior;
- dead-letter persistence/replay.

Process crash tests:
Use child processes. Do not substitute graceful shutdown for a real crash.

Required crash scenarios:
```text
C1 crash after durable commit before signal
C2 crash after signal before claim
C3 crash after claim before handler
C4 crash during handler
C5 crash after external side effect before Completed
C6 crash after RetryScheduled
C7 crash after DeadLettered
```

Expected principle:
- accepted messages remain discoverable;
- duplicates may occur;
- messages do not silently disappear.

Fault injection should cover store failures:
```text
insert
claim
complete
retry schedule
dead-letter transition
read
cleanup
```

Also test where practical:
```text
disk full / IOException
permission denied
corrupt/incompatible store
```

Concurrency:
```text
multiple publishers
duplicate signal
concurrency 1
concurrency > 1
shutdown with in-flight work
shutdown timeout
unknown route
duplicate route
```

Cancellation:
```text
before commit
after commit
during handler because of shutdown
```

DLQ:
```text
poison -> DeadLettered
survives restart
replay -> Pending
observer failure does not delete
purge/delete behavior
```

Benchmarks only after correctness:
```text
durable publish throughput
p50/p95/p99 publish latency
end-to-end latency
payload size impact
concurrency sweep
1/10/50/100 message types
1k/10k/100k backlog
store contention
cleanup
shutdown
```
