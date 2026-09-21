# Benchmark Baseline — 0.3.0-alpha prep

This document records a **baseline** set of BenchmarkDotNet measurements for Hermes.Messaging.
It is intended to catch regressions and inform (not justify by itself) future work. These are
**synthetic developer-machine measurements**, not production throughput guarantees.

## Environment

| Item | Value |
|------|-------|
| CPU | 12th Gen Intel Core i9-12900 |
| OS | Microsoft Windows 10.0.26200 |
| .NET SDK | 10.0.401 |
| Runtime | .NET 10 |
| Store | LiteDB 5.0.21 (one `.db` file per message type, on local disk) |
| Build | Release, deterministic |
| Tool | BenchmarkDotNet 0.15.4 (`[MemoryDiagnoser]`) |

> Numbers depend heavily on disk (LiteDB fsync), CPU, and OS file caching. Re-run on the target
> host before drawing conclusions. Different machines will produce materially different absolute
> values; the **shapes/relationships** below are the durable takeaways.

## How to run

```bash
# All BDN benchmarks
dotnet run -c Release --project benchmarks/Hermes.Messaging.Benchmarks

# A subset
dotnet run -c Release --project benchmarks/Hermes.Messaging.Benchmarks -- --filter *PublishBenchmarks*
dotnet run -c Release --project benchmarks/Hermes.Messaging.Benchmarks -- --filter *EndToEndBenchmarks*
dotnet run -c Release --project benchmarks/Hermes.Messaging.Benchmarks -- --filter *TypeScalingBenchmarks*

# Long-running backlog recovery (BDN)
dotnet run -c Release --project benchmarks/Hermes.Messaging.Benchmarks -- --filter *BacklogRecoveryBenchmarks*

# Fast single-shot backlog probe (deterministic wall-clock, not BDN statistics)
dotnet run -c Release --project benchmarks/Hermes.Messaging.Benchmarks -- --backlog-probe 1000 10000 50000
```

---

## Measured facts

### 1. Durable publish (`PublishAsync`)

Validation → LiteDB durable insert → best-effort signal → `PublishResult`. A no-op handler drains
the channel during the run.

| PayloadBytes | Mean | Gen0 | Gen1 | Gen2 | Allocated |
|-------------:|-----:|-----:|-----:|-----:|----------:|
| 100 | 628.0 µs | 10.7422 | 2.9297 | 0.9766 | 600.83 KB |
| 1024 | 693.2 µs | 11.7188 | 1.9531 | 0.9766 | 621.99 KB |
| 10240 | 722.3 µs | 9.7656 | 1.9531 | – | 668.78 KB |

### 2. End-to-end processing (Publish → Persist → Signal → Claim → Handler → Completed)

Per-message figures (`OperationsPerInvoke = 1000`), minimal handler.

| MaxConcurrency | Mean / msg | Allocated / msg |
|---------------:|-----------:|----------------:|
| 1 | 512.4 µs | 256.34 KB |
| 4 | 543.2 µs | 254.22 KB |
| 16 | 520.9 µs | 256.34 KB |

### 3. Backlog recovery / reconciliation (single-shot probe)

Pre-seeded durable `Pending` backlog; `MaxConcurrency = 4`.

| Backlog | Seed time | Time-to-first-message | Drain time | Drain rate |
|--------:|----------:|----------------------:|-----------:|-----------:|
| 1,000 | 454 ms | 209 ms | 860 ms | ~1,162 msg/s |
| 10,000 | 2,561 ms | 75 ms | 3,959 ms | ~2,526 msg/s |
| 50,000 | 10,461 ms | 116 ms | 20,240 ms | ~2,470 msg/s |

### 4. Message-type scaling (host startup vs number of registered types)

| TypeCount | Startup mean | Allocated |
|----------:|-------------:|----------:|
| 1 | 5.1 ms | 0.87 MB |
| 10 | 46.0 ms | 7.4 MB |
| 50 | 196.3 ms | 37.0 MB |
| 100 | 408.2 ms | 74.0 MB |

---

## Observations (interpretations)

These are **plausible interpretations**, not proven claims.

- **The durable LiteDB write dominates every path.** Publish latency (~0.6–0.7 ms) is largely
  insensitive to payload size (100 B → 10 KB), consistent with a per-insert commit/fsync cost that
  swamps serialization of small payloads.
- **End-to-end throughput is flat across concurrency (1/4/16).** With a trivial handler, the single
  LiteDB store file (writes serialized behind a write-lock + commit) is the gate, not handler
  parallelism. `MaxConcurrency` helps when **handlers** are the bottleneck (I/O-bound work), not
  when the store is. This matches the intended architecture and is *not* a defect.
- **Backlog recovery starts fast regardless of backlog size.** Time-to-first-message stays ~75–210 ms
  even at 50k, confirming that bounded startup seeding (HERMES-007) does **not** block on
  materializing the whole backlog. Drain rate stabilises around ~2.5k msg/s (write-bound).
- **Per-type startup cost is roughly linear (~4 ms + ~4 ms/type; ~0.75 MB/type).** Each message type
  opens its own LiteDB database and hosted service. 100 types start in ~0.4 s — acceptable for the
  intended single-process scope. This is a known characteristic of the one-store-per-type model.

## Known limitations

- Synthetic; single machine; local disk. Not representative of production hardware or workloads.
- Handlers are trivial; real handlers usually dominate end-to-end time and change the concurrency story.
- Allocation figures are managed-only (BDN `MemoryDiagnoser`), inclusive.
- The BDN backlog-recovery benchmark is long-running (re-seeds per iteration); the table above uses
  the fast single-shot probe for representative numbers. The BDN class remains for rigorous local runs.

## Conclusion for this phase

No benchmark demonstrated a **correctness** problem or a clear, well-understood performance defect
that a safe change would fix. Per the task's architecture rule, **no performance change was
implemented** — the durable-store-as-source-of-truth design is left intact. The measurements are
recorded here as a regression baseline for future work.
