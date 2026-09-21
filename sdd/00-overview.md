# 00 — Overview

Hermes.Messaging is being hardened from an alpha in-process bus into a reliable single-process durable message bus for .NET 10.

Priorities:
1. correctness;
2. crash safety;
3. explicit delivery semantics;
4. operability;
5. maintainability;
6. performance;
7. feature breadth.

Goals:
- remove accepted-message loss windows;
- deterministic recovery;
- persistent retry/dead-letter behavior;
- simpler lifecycle/concurrency;
- public API matching actual guarantees;
- fault-injection friendly tests;
- clear path to 1.0.

Non-goals:
- distributed broker;
- multi-node queue;
- remote transport;
- exactly-once processing;
- distributed transactions.
