# 03 — Delivery Semantics

## Guarantee
A successful publish means the message was durably committed before success was returned.

Within the supported single-process boundary, accepted messages are eligible for at-least-once processing.

Not guaranteed:
- exactly-once handler execution;
- exactly-once external side effects;
- completion order with concurrency > 1;
- durability on ephemeral storage.

## Required publish sequence
```text
Validate runtime + route
Create envelope
Persist Pending
Commit succeeds
Best-effort signal to Channel
Return Accepted
```

A lost signal must never mean a lost accepted message.

## Cancellation
Before durable commit, cancellation may prevent acceptance.

After durable commit, do not return cancellation as if no message exists. Return the accepted result even if notification is skipped/fails.

## Identity
`MessageId` = unique technical identity.

`CorrelationId` = logical correlation, caller-supplied or generated, non-unique.

Never use CorrelationId as the unique persistence key.

## Routing
For 1.0: one handler per `{message type, route}`. No fan-out. Unknown route/type is rejected before durable acceptance.
