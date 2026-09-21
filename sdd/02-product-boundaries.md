# 02 — Product Boundaries

Supported:
```text
one application process
one Hermes runtime
one local durable storage domain
restart against the same persistent filesystem
```

Unsupported:
- multiple app instances sharing queue semantics;
- competing consumers across processes;
- distributed leases/consensus;
- broker replication;
- remote consumers/transports;
- distributed transactions;
- exactly-once claims.

Durability requires persistent local storage. Ephemeral container filesystems cannot provide restart durability.

Introduce a store abstraction only to hide LiteDB, support tests/fault injection, and decouple core semantics. Do not add speculative providers. LiteDB remains the only production provider until a real requirement exists.
