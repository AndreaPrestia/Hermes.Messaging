# 11 — Migration and Compatibility

Since the `v0.5.0-beta` freeze the public API is a tracked compatibility contract. Breaking public API changes must be exceptional, required for correctness, explicit, documented in CHANGELOG/release notes, reflected through `PublicApiAnalyzers` (`PublicAPI.Unshipped.txt`), and versioned appropriately.

Persisted data can be orphaned by:
- changing CLR-derived file names;
- namespace/type renames;
- collection names;
- index changes;
- payload structure changes;
- status enum changes.

Never silently create a new empty store and ignore an existing backlog.

The identity migration is:
```text
CURRENT: CorrelationId effectively unique
TARGET:  MessageId unique, CorrelationId non-unique
```

Acceptable alpha strategies:
1. safe automatic migration;
2. explicit one-time migration tool;
3. explicit fail-fast/reset requirement.

Unacceptable:
```text
silent data loss
silent backlog abandonment
```

Before beta persist:
```text
schema version
stable message type identity
```

Suggested SemVer path:
```text
0.2.0-alpha  durability/correctness
0.3.x-alpha  operational hardening if needed
0.5.0-beta   API/observability freeze
1.0.0        stable contract
```
