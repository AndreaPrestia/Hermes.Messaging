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

> **Methodology note (0.3.0-alpha cleanup).** The publish, end-to-end and backlog measurements
> below were **re-run** after fixing two benchmark-harness defects (they do not reflect any change
> to Hermes itself):
> 1. **Publish** now creates a fresh store per BenchmarkDotNet iteration (`[IterationSetup]`), so the
>    LiteDB file no longer grows across the whole run. The pre-cleanup publish numbers were inflated
>    because they measured a store that accumulated every prior publish. This forces
>    `InvocationCount=1, UnrollFactor=1`.
> 2. **End-to-end / backlog** now wait for **durable** completion (store backlog =
>    `Pending + Processing + RetryScheduled` reaches 0), not for the handler callback to return. A
>    handler returning does not mean the record is durably `Completed`.
>
> The historical pre-cleanup numbers are preserved further below, clearly labelled as historical.

### 1. Durable publish (`PublishAsync`) — re-run

Validation → LiteDB durable insert → best-effort signal → `PublishResult`. Fresh store per
iteration (`InvocationCount=1, UnrollFactor=1`); a no-op handler only drains the wake-up channel.

| PayloadBytes | Mean | StdDev | Allocated |
|-------------:|-----:|-------:|----------:|
| 100 | 299.6 µs | 47.55 µs | 52.92 KB |
| 1024 | 290.9 µs | 51.11 µs | 43.11 KB |
| 10240 | 313.8 µs | 60.03 µs | 53.23 KB |

*Measured fact:* per-publish allocation is ~43–53 KB and mean latency ~290–314 µs, roughly flat
across payload size in this range. *Interpretation (not a guarantee):* the ~10× higher allocation
in the pre-cleanup numbers was a harness artifact of the ever-growing store, not the publish path.
Absolute values are dominated by LiteDB fsync and vary by disk; the run-to-run variance
(bimodal, high StdDev) comes from per-iteration host startup.

### 2. End-to-end processing (Publish → Persist → Signal → Claim → Handler → durable Completed) — re-run

Per-message figures (`OperationsPerInvoke = 1000`), minimal handler, fresh host/store per
iteration, waiting for durable backlog = 0.

| MaxConcurrency | Mean / msg | Median / msg | Allocated / msg |
|---------------:|-----------:|-------------:|----------------:|
| 1 | 538.1 µs | 529.1 µs | 277.27 KB |
| 4 | 516.4 µs | 510.2 µs | 263.76 KB |
| 16 | 539.6 µs | 526.3 µs | 272.69 KB |

*Measured fact:* ~510–540 µs and ~264–277 KB per message end-to-end; increasing `MaxConcurrency`
does not materially improve per-message time here. *Interpretation:* the workload is dominated by
per-message LiteDB writes (insert + claim + complete), which serialize on the single store file, so
extra worker concurrency has little headroom on this synthetic no-op handler.

### 3. Backlog recovery / reconciliation (single-shot probe) — re-run

Pre-seeded durable `Pending` backlog; `MaxConcurrency = 4`. `drain_ms` now measures the time for the
**durable backlog to reach 0**, not the last handler invocation.

| Backlog | Seed time | Time-to-first-message | Drain time | Drain rate |
|--------:|----------:|----------------------:|-----------:|-----------:|
| 1,000 | 480 ms | 170 ms | 1,012 ms | ~987 msg/s |
| 10,000 | 2,540 ms | 75 ms | 5,091 ms | ~1,964 msg/s |
| 50,000 | 13,721 ms | 291 ms | 27,844 ms | ~1,796 msg/s |

*Measured fact:* drain to a fully durable-`Completed` backlog is slower than the pre-cleanup
handler-countdown figures (e.g. 50k: ~27.8 s vs the old ~20.2 s). *Interpretation:* the difference is
the trailing `MarkCompleted` commits that the old probe stopped short of; the new numbers are the
faithful cost of durable drain. Sub-linear rate degradation at larger backlogs is expected as the
store file grows.

### Historical (pre-cleanup, superseded — do NOT compare directly)

Kept for provenance only. These were produced by the defective harness described in the methodology
note above and must not be used as a baseline.

| Bench | Result (historical) |
|-------|---------------------|
| Publish 100 B | 628.0 µs / 600.83 KB |
| Publish 1 KB | 693.2 µs / 621.99 KB |
| Publish 10 KB | 722.3 µs / 668.78 KB |
| E2E MC=1 | 512.4 µs / 256.34 KB |
| E2E MC=4 | 543.2 µs / 254.22 KB |
| E2E MC=16 | 520.9 µs / 256.34 KB |
| Backlog 1k drain | 860 ms (~1,162 msg/s) |
| Backlog 10k drain | 3,959 ms (~2,526 msg/s) |
| Backlog 50k drain | 20,240 ms (~2,470 msg/s) |

### 4. Message-type scaling (host startup vs number of registered types)

| TypeCount | Startup mean | Allocated |
|----------:|-------------:|----------:|
| 1 | 5.1 ms | 0.87 MB |
| 10 | 46.0 ms | 7.4 MB |
| 50 | 196.3 ms | 37.0 MB |
| 100 | 408.2 ms | 74.0 MB |

---

## Observations (interpretations)

These are **plausible interpretations**, not proven claims. They reference the corrected re-run
tables above (sections 1–4); the superseded pre-cleanup numbers are **not** used here.

- **The durable LiteDB write dominates every path.** Publish latency (~290–314 µs, i.e. ~0.29–0.31 ms
  — see section 1) is largely insensitive to payload size (100 B → 10 KB), consistent with a
  per-insert commit/fsync cost that swamps serialization of small payloads.
- **End-to-end throughput is flat across concurrency (1/4/16).** With a trivial handler, the single
  LiteDB store file (writes serialized behind a write-lock + commit) is the gate, not handler
  parallelism. `MaxConcurrency` helps when **handlers** are the bottleneck (I/O-bound work), not
  when the store is. This matches the intended architecture and is *not* a defect.
- **Backlog recovery starts fast regardless of backlog size.** Time-to-first-message stays low
  (~75 ms at 10k, ~170 ms at 1k, ~291 ms at 50k — see section 3) even at 50k, confirming that
  bounded startup seeding (HERMES-007) does **not** block on materializing the whole backlog.
  Durable drain rate is write-bound and, measured to a fully `Completed` backlog, is roughly
  ~1–2k msg/s (~987 msg/s at 1k, ~1,964 msg/s at 10k, ~1,796 msg/s at 50k); it does **not** improve
  with backlog size and degrades slightly at larger backlogs as the store file grows.
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
