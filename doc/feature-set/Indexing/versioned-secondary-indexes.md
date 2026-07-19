---
uid: feature-indexing-versioned-secondary-indexes
title: 'Versioned (HEAD/TAIL) Secondary Indexes for MVCC'
description: 'The mechanism that keeps AllowMultiple index membership correct across updates and deletes on Versioned components.'
---

# Versioned (HEAD/TAIL) Secondary Indexes for MVCC
> The mechanism that keeps `AllowMultiple` index membership correct across updates and deletes on `Versioned` components.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Naively maintained secondary indexes are destructive: when an indexed field changes from value A to B, the entity
is unlinked from A and linked to B, and when an entity is deleted its index entries must be cleaned up too. Done
carelessly, both steps break MVCC guarantees — a value change can leave a brief window where the entity is in
neither key's result set, and a deleted entity's stale entries can linger and get handed back to callers, who then
dereference a chain that's gone. On `Versioned` components, every `[Index(AllowMultiple = true)]` field is
protected against both failure modes by construction, as part of ordinary commit processing.

## ⚙️ How it works (in brief)

Each `AllowMultiple` key holds two buffers. The HEAD buffer is the current entity set — unchanged from a plain
multi-value index, and what `Transaction.EnumerateIndex` reads. The TAIL buffer is an append-only log of
`(ChainId, TSN, Active/Tombstone)` entries: one Active entry whenever an entity gains the key's value (create, or
update into it), one Tombstone whenever it loses it (update away, or delete). TAIL is allocated lazily on a key's
first mutation and linked from the HEAD buffer's header, so a key that's never been touched costs nothing extra.
Both HEAD and TAIL are updated together, automatically, inside `Transaction.Commit` — there is no separate API to
call and no way to opt out for an `AllowMultiple` field on a `Versioned` component.

## 💻 Usage

```csharp
[Component("Game.GuildMember", 1)]   // Versioned by default
struct GuildMember
{
    [Index(AllowMultiple = true)]
    public long GuildId;
    public String64 Name;
}

[Archetype]
class MemberArchetype : Archetype<MemberArchetype>
{
    public static readonly Comp<GuildMember> M = Register<GuildMember>();
}

var guildIndex = dbe.GetIndexRef<GuildMember, long>(m => m.GuildId);

EntityId aria;
using (var tx = dbe.CreateQuickTransaction())
{
    aria = tx.Spawn<MemberArchetype>(MemberArchetype.M.Set(new GuildMember { GuildId = 7, Name = "Aria" }));
    tx.Commit();
}

// Move Aria to guild 9 — HEAD(7) loses her, HEAD(9) gains her, and TAIL records both transitions
// with this commit's TSN. No extra calls: it happens as part of Write + Commit.
using (var tx = dbe.CreateQuickTransaction())
{
    ref var m = ref tx.OpenMut(aria).Write(MemberArchetype.M);
    m.GuildId = 9;
    tx.Commit();
}

// Current-state membership is exactly the HEAD set — guild 7 is now empty, guild 9 has Aria.
using var tx2 = dbe.CreateQuickTransaction();
using var g7 = tx2.EnumerateIndex<GuildMember, long>(guildIndex, 7, 7); // empty
using var g9 = tx2.EnumerateIndex<GuildMember, long>(guildIndex, 9, 9); // Aria
```

## ⚠️ Guarantees & limits

- HEAD/TAIL maintenance applies only to `[Index(AllowMultiple = true)]` fields on `Versioned` components.
  `SingleVersion` and `Transient` `AllowMultiple` indexes keep the HEAD set only — without a revision chain there
  is no history to append, so TAIL is never allocated for them.
- Maintenance is unconditional and automatic for every create, update-into/out-of a value, and delete on a
  qualifying field — there is no method to call, no flag to set, and no way for a commit to skip it.
- `Transaction.EnumerateIndex` reads HEAD only, at the same O(K) cost as a non-versioned `AllowMultiple` index —
  adopting `Versioned` storage for the extra correctness costs nothing on this read path.
- Delete is first-class: the HEAD entry is removed and a TAIL Tombstone is appended in the same commit, so a
  deleted entity never reappears in a current-state `EnumerateIndex` result and never requires a follow-up cleanup
  pass.
- Each TAIL entry is 12 bytes and is written once per value transition, not once per entity — cost scales with how
  often the field changes, not with how many entities currently hold the value.
- TAIL is what powers [point-in-time reconstruction](./temporal-index-query.md) ("who held this value at TSN T")
  and is bounded by a not-yet-auto-triggered [pruning algorithm](./tail-garbage-collection.md) — today the
  directly usable guarantee is that current-state `EnumerateIndex` results are always accurate, including
  immediately after deletes and value changes.

## 🧪 Tests

- [VersionedIndexTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/VersionedIndexTests.cs) — Phase 1 (`AllocateTailBuffer_AddAndReadEntry`, lazy TAIL allocation) and Phase 2 (`Update_IndexedField_TailHasTombstoneAndActive`, `Delete_Entity_TailHasTombstone`, `Update_BackfillsTail_AllEntriesPresent`): HEAD+TAIL maintenance on create/update/delete

## 🔗 Related

- Sibling feature: [Multi-value secondary index (AllowMultiple)](./secondary-index-storage-modes/multi-value-secondary-index.md)
- Sibling feature: [Secondary Index Storage Modes](./secondary-index-storage-modes/README.md)
- Sibling feature: [Temporal (Point-in-Time) Index Query](./temporal-index-query.md) — reads this feature's TAIL history to answer point-in-time queries

<!-- Deep dive: claude/design/Indexing/VersionedSecondaryIndexes.md -->
<!-- ADR: claude/adr/039-versioned-secondary-index-architecture.md -->
<!-- Deep dive: claude/overview/04-data.md §Versioned Secondary Indexes (HEAD/TAIL Architecture) -->
