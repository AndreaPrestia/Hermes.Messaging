# ADR: Storage identity and versioning

- Status: Accepted (0.3.0-alpha)
- Scope: durable persistence identity, compatibility, and migration policy
- Non-goal: a generic migration engine (explicitly deferred)

## Context

Hermes persists messages in LiteDB. Two identity concerns exist:

1. **Store/collection identity** — which database file and collection a message type maps to.
   Today: one file per type at `{basePath}/{CLR FullName, '.'/'+' → '_'}.db`, collection
   `messages_{typeof(T).Name}` (short name).
2. **Record schema identity** — the shape of a persisted record, tracked by
   `PersistedMessage<T>.SchemaVersion` (`PersistedMessageSchema.CurrentVersion`, currently `1`).

Risks to durable data include: CLR type rename, namespace rename, assembly move, payload-shape
changes, and enum/state evolution. The guiding principle is:

> **Never silently orphan durable messages.** A clear fail-fast error is preferable to silently
> ignoring an existing backlog.

## Decisions

### What is the stable durable message-type identity?

For 0.3.0-alpha it remains **CLR-derived**: the file name uses `Type.FullName` and the collection
uses `Type.Name`. This is pragmatic but **not** a stable logical identity: renaming the type or its
namespace changes the file path and orphans the old store.

**Direction for beta:** introduce an explicit, stable logical name (e.g. an attribute or
registration-time name) decoupled from the CLR name. This is **NEEDS-DESIGN** and intentionally not
implemented here to avoid a speculative mechanism.

> Note: the file uses `FullName` while the collection uses `Name`. Two types with the same short
> name in different namespaces map to different files (no data collision) but identically-named
> collections *within their own files* — safe today because each file holds one type. This
> inconsistency is recorded and should be unified to `FullName` for both before beta.

### What happens if a CLR type or namespace is renamed?

The derived file path changes; Hermes opens a new empty store and the old backlog is left on disk
untouched (orphaned, not deleted). This is a known limitation until a stable logical identity
exists. **Mitigation today:** document it; operators who rename a type must copy/rename the `.db`
file or drain the old backlog first.

### What happens when `SchemaVersion` changes?

- **Newer store, older Hermes** (store `SchemaVersion` > build `CurrentVersion`): **fail fast.**
  Implemented now via `StoreSchemaMismatchException` at store-open time. Hermes never rewrites or
  discards records; it refuses to open and tells the operator to upgrade or migrate.
- **Older store, newer Hermes** (store `SchemaVersion` < build `CurrentVersion`): permitted for
  **additive** changes (new optional fields deserialize with defaults). A future breaking record
  change must bump `CurrentVersion` and define an explicit migration; until such a change exists,
  additive compatibility is the only supported forward path.
- **Equal versions:** normal operation.

### When should Hermes fail fast?

- Store schema strictly newer than the running build (implemented).
- (Future) Detected incompatible record shape that cannot be read safely.

Fail-fast happens at store construction (host startup), so a misconfiguration surfaces immediately
and publishing never begins against an unsafe store.

### When is automatic migration safe?

Only for **purely additive** schema changes (new optional fields). These require no data rewrite.
Anything that changes the meaning of existing fields, the identity key, or the state enum is **not**
auto-migratable and must use an explicit tool.

### When is an explicit migration tool required?

- identity key change (already happened 0.1 → 0.2: CorrelationId-keyed → MessageId-keyed);
- collection/file naming change;
- payload/record shape breaking change;
- `MessageStatus` value renumbering/semantic change.

0.1 stores are **not** compatible with 0.2+ (identity model changed) and were documented as
requiring a fresh store. No automatic 0.1 migration is provided.

### Compatibility guarantees for beta/1.0

- **Beta (0.5.0):** freeze the record schema and the logical type-identity scheme; guarantee
  additive-only forward compatibility within a minor version; keep fail-fast for newer stores.
- **1.0:** stable durable format within the 1.x line; breaking durable changes require a major
  version and an explicit, documented migration path. Never silently orphan.

## Consequences

- A minimal, well-understood fail-fast guard exists now (`StoreSchemaMismatchException`), satisfying
  the "never silently orphan" principle for the newer-store case without a migration framework.
- Stable logical type identity and any record-shape migration remain deliberate pre-beta design
  work, not implemented speculatively here.
