---
uid: feature-indexing-temporal-index-query
title: 'Temporal (Point-in-Time) Index Query'
description: 'Reconstructs which entities held a key''s value at a past TSN by replaying the index''s append-only version history.'
---

# Temporal (Point-in-Time) Index Query
> Reconstructs which entities held a key's value at a past TSN by replaying the index's append-only version history.

**Status:** 🚧 Partial · **Visibility:** Internal · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Current-state secondary indexes only ever answer "who matches this value *now*". Once a matching entity's field
changes or the entity is deleted, the old membership fact is gone from the index — there is no way to ask "which
entities had `Status = Active` as of last Tuesday's snapshot" without falling back to a full scan and walking every
candidate entity's revision chain by hand. Temporal index query answers that question directly off the index
itself: given a key and a historical TSN, it returns exactly the entities that were associated with that key at
that moment, in time proportional to how often the key's membership has changed rather than how many entities
exist.

## ⚙️ How it works (in brief)

[Versioned secondary indexes](./versioned-secondary-indexes.md) already record, for every `AllowMultiple` key, an
append-only TAIL log of `(ChainId, TSN, Active/Tombstone)` transitions alongside the current-state HEAD set. A
temporal query looks up the key's TAIL buffer, scans every entry with `TSN <= targetTSN`, and keeps only the
most-recent entry per chain ID — chains whose latest qualifying state is Active are the answer. If the key has
never been mutated since it was created (no TAIL buffer allocated yet), the current HEAD set is returned directly,
since it has been correct for every TSN since the key's creation.

## 💻 Usage

There is no public entry point yet — `TemporalIndexQuery`, the `IndexedFieldInfo`/`TailVSBS` it reads, and
`VersionedIndexEntry` are all `internal` types with no `InternalsVisibleTo` grant to application assemblies, and no
`Transaction`/query-builder method calls into them. The shape below is the actual call the engine's own test suite
exercises — shown to illustrate the contract, not as code an application can compile against today:

```csharp
// Engine-internal only — illustrative, not callable from application code.
var ifi = componentTable.IndexedFieldInfos[fieldIndex];   // the AllowMultiple field's index metadata
float key = 1.0f;

List<int> chainIdsActiveAtTsn = TemporalIndexQuery.Query(
    ifi, (byte*)&key, targetTSN: snapshotTsn, componentTable.TailVSBS, changeSet: null);

// Results are revision-chain handles, not EntityIds — resolving a chain ID back to an
// entity/component is also internal-only today.
```

## ⚠️ Guarantees & limits

- **No public API surface yet.** Nothing in `Transaction`, the query builder, or `PointInTimeAccessor` calls this
  today — it is verified, tested engine plumbing without an application-facing entry point.
- Applies only to `[Index(AllowMultiple = true)]` fields on `Versioned` components — the same scope as the TAIL
  history it reads; `SingleVersion`/`Transient` indexes carry no TAIL to query.
- Returns revision-chain IDs, not `EntityId`s — turning a chain ID into the entity/component data as of the target
  TSN is a separate step with no public surface either.
- Cost is O(H) per query, where H is the total number of TAIL transitions ever recorded for that key — not the
  number of entities currently or historically matching it. A key churned by frequent updates costs more to query
  than a stable one, independent of result size.
- A boundary-sentinel-preserving TAIL pruning algorithm exists but isn't wired to an automatic trigger yet, so TAIL
  history is effectively unbounded today; there is also no "target TSN older than retained history" error path —
  that case is only meaningful once pruning is actually scheduled.
- Chain ID reuse is handled correctly: if an old entity is deleted and a new one is later created reusing the same
  chain ID under the same key, the Tombstone between them separates the two in the TAIL stream, so a query never
  conflates them — but it does mean chain-ID-level history isn't the same thing as one entity's history.

## 🧪 Tests

- [VersionedIndexTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/VersionedIndexTests.cs) — Phase 3 `Temporal Query Tests` region: `TemporalQuery_NoMutation_FallsBackToHead`, `TemporalQuery_AtCreate_ReturnsEntity`, `TemporalQuery_OldKey_AfterUpdate_SingleEntity_StillVisible`/`_MultiEntity_CorrectCount`, `TemporalQuery_AfterBackfill_ReturnsCorrectSnapshot`

## 🔗 Related

- Sibling feature: [Versioned (HEAD/TAIL) Secondary Indexes for MVCC](./versioned-secondary-indexes.md)
- Sibling feature: [Multi-value secondary index (AllowMultiple)](./secondary-index-storage-modes/multi-value-secondary-index.md)
- Source: `src/Typhon.Engine/Indexing/internals/TemporalIndexQuery.cs`, `src/Typhon.Engine/Indexing/internals/TailGarbageCollector.cs`

<!-- Deep dive: claude/design/Indexing/VersionedSecondaryIndexes.md §8 Read Path / Query Algorithm -->
<!-- Deep dive: claude/overview/04-data.md §Versioned Secondary Indexes (HEAD/TAIL Architecture) -->
