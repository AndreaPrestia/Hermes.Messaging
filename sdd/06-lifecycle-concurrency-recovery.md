# 06 — Lifecycle, Concurrency and Recovery

Runtime states:
```text
Created
Starting
Ready
Stopping
Stopped
Faulted
```

Publish is allowed only in `Ready`.

Startup:
```text
Created
 -> Starting
 -> open/validate store
 -> recover interrupted Processing
 -> discover due work
 -> start scheduler/workers
 -> Ready
```

Shutdown:
```text
Ready
 -> Stopping
 -> reject new publishes
 -> stop scheduler
 -> await in-flight handlers up to grace period
 -> leave durable backlog for restart
 -> Stopped
```

Do not drain the entire backlog during shutdown.

Concurrency: prefer N fixed async worker loops consuming `Channel<MessageId>`. Do not spawn `Task.Run` for every async message.

Ordering:
- concurrency 1: claim order FIFO among due work where practical;
- retries may allow later messages to progress;
- concurrency > 1: completion order is not guaranteed.

Startup recovery under the single-process model:
```text
Processing -> Pending
```
No distributed lease/heartbeat system is needed.
