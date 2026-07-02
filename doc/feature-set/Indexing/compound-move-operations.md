# Compound Move/MoveValue (field-update fast path)
> Atomic remove+insert for indexed-field updates — one traversal, one lock on the common same-leaf case.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Updating an indexed field's value (a position changing, a status enum flipping) requires the index to
relocate the entity from its old key to its new key. A naive implementation does this as two independent
operations — remove the old key, insert the new key — each paying its own lock acquisition and full
root-to-leaf traversal. For `AllowMultiple` indexes with version-history (TAIL) tracking, the naive path is
worse still: two extra lookups to recover the HEAD buffer IDs needed for TAIL linking. Most field updates are
small (a counter increment, a position nudge), so the old and new keys frequently land in the same leaf —
making the second traversal pure waste.

## ⚙️ How it works (in brief)

`Move` (unique indexes) and `MoveValue` (`AllowMultiple` indexes) descend for both the old and new key in one
pass, sharing the common root-to-fork path. If both keys resolve to the same leaf, the operation takes a
single write-lock and performs the remove and insert as one in-node operation — no entry-count change, so no
split/merge risk. If the keys resolve to different leaves, both are locked in ascending chunk-id order
(deadlock-free) and the remove/insert happen under that paired lock; if either leaf can't safely absorb the
change in place, the call falls back to a pessimistic, structurally-aware path. For `MoveValue`, the HEAD
buffer IDs for both the old and new key are read inline while the leaf is already locked, instead of issuing
separate lookups afterward. This is entirely internal — there is no separate API to invoke; any commit that
changes an indexed field's value uses this path automatically.

## 💻 Usage

```csharp
[Component("Game.Unit", 1, StorageMode = StorageMode.SingleVersion)]
struct Unit
{
    [Index]                         // unique index → uses compound Move
    public int Id;

    [Index(AllowMultiple = true)]   // secondary, multi-value → uses compound MoveValue
    public int Status;
}

[Archetype(7)]
partial class UnitArchetype : Archetype<UnitArchetype>
{
    public static readonly Comp<Unit> U = Register<Unit>();
}

using var tx = dbe.CreateQuickTransaction();
EntityRef e = tx.OpenMut(id);
ref Unit u = ref e.Write(UnitArchetype.U);
u.Status = (int)UnitStatus.Engaged;   // indexed field change
tx.Commit();                          // index relocation runs as a single compound Move/MoveValue — no app code involved
```

There is no tuning knob — the engine always attempts the same-leaf fast path first, falls back to the
dual-leaf path, and finally to the pessimistic path; the choice is made per-call based on where the old and
new keys land.

## ⚠️ Guarantees & limits

- Triggered automatically whenever a transaction commits a change to a field carrying `[Index]` — no separate
  API call exists, and there is nothing to opt into or out of.
- Same-leaf updates take exactly one write-lock and one traversal; cross-leaf updates take two write-locks,
  acquired in ascending chunk-id order to avoid deadlocks, after a single shared traversal prefix.
- The old key's entry is only removed once the new key is confirmed absent (unique indexes) or the buffer
  operations succeed (multi-value indexes) — a failed move leaves the index unchanged, not half-applied.
- If a cross-leaf move would overflow the destination leaf or underflow the source leaf, it bails out of the
  optimistic path and falls back to a pessimistic, structurally-aware move (handles split/merge correctly);
  this fallback is rare in practice.
- For `AllowMultiple` indexes, `MoveValue` returns both the old and new HEAD buffer IDs in one call, used
  inline for TAIL (version-history) maintenance — no standalone lookups are issued.
- Design-time estimate: ~57-62% faster than Remove+Add for same-leaf moves, ~30-41% faster cross-leaf, and
  ~64-71% faster than the four-operation multi-value path; actual savings depend on how often field updates
  keep the entity in the same leaf.

## 🧪 Tests

- [OlcBTreeTests](../../../test/Typhon.Engine.Tests/Data/OlcBTreeTests.cs) — `#114 — Compound Move/MoveValue` region: same-leaf/cross-leaf `Move`, deadlock-free opposite-direction moves, `MoveValue` same-leaf/cross-leaf/last-element-removes-key, and the old-key-not-found/new-key-exists failure paths
- [ClusterIndexTests](../../../test/Typhon.Engine.Tests/Data/ECS/ClusterIndexTests.cs) — `TickFence_FieldMutation_BTreeMoveExecuted`: end-to-end exercise of the same `Move` operation via the cluster/SV tick-fence deferred commit path

## 🔗 Related

- Sibling features: [Unique (single-value) secondary index](./secondary-index-storage-modes/unique-secondary-index.md), [Multi-value secondary index (AllowMultiple)](./secondary-index-storage-modes/multi-value-secondary-index.md)

<!-- Deep dive: claude/design/Indexing/concurrent-index-scaling.md §5.5, §6 -->
<!-- Deep dive: claude/design/Indexing/public-api.md -->
