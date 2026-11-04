---
uid: feature-indexing-transaction-local-index-overlay
title: 'Transaction-Local Index Overlay (Read-Your-Own-Writes)'
description: 'Planned per-transaction overlay so index lookups see that transaction''s own uncommitted writes.'
---

# Transaction-Local Index Overlay (Read-Your-Own-Writes)
> Planned per-transaction overlay so index lookups see that transaction's own uncommitted writes.

**Status:** 📋 Planned · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Secondary indexes are maintained on the commit path, not as part of the transaction body. Today `EnumerateIndex` answers at the transaction's snapshot with its own spawns and destroys counted (#1247), but not its own value writes: an entity whose indexed field this still-open transaction changed is still found at its old key, and not at its new one — even though direct component reads of that entity see the new value. This asymmetry is surprising: code that re-keys an entity and immediately looks it up by the new key gets a miss.

## ⚙️ How it works (in brief)

A per-transaction, in-memory overlay records the index-key additions and removals implied by the transaction's own `Spawn`/update/delete calls, without touching the persistent B+Tree. Index reads (`EnumerateIndex` and friends) merge the overlay with the persistent tree before returning results, so uncommitted same-transaction changes appear alongside committed data. On `Commit()`, the overlay's changes are written into the persistent B+Tree exactly as they are today; on rollback or dispose, the overlay is simply discarded — no B+Tree undo is needed. Point lookups stay cheap (an O(1) dictionary check plus the existing tree lookup); range scans against the overlay's uncommitted entries may be more limited than committed-data range scans (e.g. unordered until merged) — final behavior depends on the implementation.

## 💻 Usage

```csharp
// Illustrative only — not implemented yet. Today, this pattern misses:
var nameIndex = engine.GetIndexRef<PlayerData, String64>(p => p.Name);

using var tx = engine.CreateQuickTransaction();
var e = tx.OpenMut(id);
var data = e.Read(Player.Data);
data.Name = "Renamed";
e.Set(Player.Data, data);                             // component written; index NOT yet updated

using var hit = tx.EnumerateIndex<PlayerData, String64>(nameIndex, data.Name, data.Name);
hit.MoveNext();   // false today — overlay would make this true once shipped

tx.Commit();
```

## ⚠️ Guarantees & limits

- **Not implemented.** No `TransactionIndexOverlay`, `_additions`/`_removals` tracking, or overlay-aware merge path exists in `src/Typhon.Engine/Indexing/` today; this entry documents a planned fix, not shipped behavior.
- **Current behavior:** `EnumerateIndex` sees the transaction's own spawns and hides its own destroys, but not its own writes to an indexed field: until commit, a re-keyed entity is found at its old key, not its new one. Workaround today: track entity IDs directly instead of relying on an index lookup for an entity whose key you just changed in the same transaction.
- Once delivered, intended to give full read-your-own-writes semantics for index reads, matching the guarantee component reads already have via the transaction cache.
- Designed to preserve today's rollback simplicity (discard an in-memory structure, no B+Tree undo) and today's write amplification (one physical B+Tree write per field at commit, not one per intermediate update).
- Memory cost scales with the indexed fields a transaction actually touches — expected small for typical transactions (tens to low hundreds of entities).
- Primary-key lookups are unaffected by this gap: PK access goes through the transaction's component cache, not the secondary-index path.

## 🔗 Related

- Related feature: [Lookup and Range-Scan Operations](./lookup-and-range-scan.md) — the read path this overlay would augment
- Related feature: [Batched Index Maintenance for Bulk Commits](./batched-index-maintenance.md) — another planned change to the same commit-path index machinery

<!-- Deep dive: claude/overview/04-data.md §Index Maintenance Timing (Known Limitation) and §Planned Fix: Transaction-Local Index Overlay -->
