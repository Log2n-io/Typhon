---
uid: feature-indexing-versioned-secondary-indexes
title: 'Element-Precise Secondary Indexes for MVCC'
description: 'The mechanism that keeps AllowMultiple index membership correct across updates and deletes on Versioned components.'
---

# Element-Precise Secondary Indexes for MVCC
> The mechanism that keeps `AllowMultiple` index membership correct across updates and deletes on `Versioned` components.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Naively maintained secondary indexes are destructive: when an indexed field changes from value A to B, the entity
is unlinked from A and linked to B, and when an entity is deleted its index entries must be cleaned up too. Done
carelessly, both steps break MVCC guarantees — a value change can leave a brief window where the entity is in
neither key's result set, and a deleted entity's stale entries can linger and get handed back to callers, who then
dereference a chain that's gone.

There is a sharper failure mode specific to `AllowMultiple`. The leaf value under a multi-value key is not an
entity location but a *buffer* holding many of them. Removing an entity with a key-level `Remove(key)` deletes the
whole buffer, silently evicting every sibling that happened to share the value. Every mutation path therefore has
to operate on the entity's own element within the buffer, never on the key.

## ⚙️ How it works (in brief)

Each `AllowMultiple` key owns one buffer: the current entity set, which is what `Transaction.EnumerateIndex`
reads. Entities are addressed inside it by an **element id**, stored alongside the entity so a later mutation can
find its own entry again.

Two compound operations do the work, each in a single tree traversal:

- `MoveValue(oldKey, newKey, elementId, …)` — removes this entity's element from the old key's buffer and inserts
  it under the new one atomically, returning the new element id. There is no window in which the entity belongs to
  neither key.
- `RemoveValue(key, elementId, …)` — removes exactly this entity's element, leaving every sibling under that key
  in place.

Both run inside `Transaction.Commit` as part of ordinary commit processing. There is no separate API to call and
no way to opt out for an `AllowMultiple` field.

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

// Move Aria to guild 9 — one traversal removes her element from key 7 and inserts it under key 9.
// Any other member of guild 7 is untouched. No extra calls: it happens as part of Write + Commit.
using (var tx = dbe.CreateQuickTransaction())
{
    ref var m = ref tx.OpenMut(aria).Write(MemberArchetype.M);
    m.GuildId = 9;
    tx.Commit();
}

using var tx2 = dbe.CreateQuickTransaction();
using var g7 = tx2.EnumerateIndex<GuildMember, long>(guildIndex, 7, 7); // Aria gone; her guild-mates remain
using var g9 = tx2.EnumerateIndex<GuildMember, long>(guildIndex, 9, 9); // Aria
```

## ⚠️ Guarantees & limits

- Maintenance is unconditional and automatic for every create, update-into/out-of a value, and delete on a
  qualifying field — there is no method to call, no flag to set, and no way for a commit to skip it.
- A value change is a single compound traversal, so the entity is never absent from both keys, and never present
  in both.
- Delete is first-class and element-precise: the entity's element is removed from the key's buffer in the same
  commit, so a deleted entity never reappears in an `EnumerateIndex` result, never requires a follow-up cleanup
  pass, and never takes its siblings with it.
- `Transaction.EnumerateIndex` reflects committed current state at the same O(K) cost as a non-versioned
  `AllowMultiple` index.
- **Current state only.** Typhon does not reconstruct past index membership: there is no "who held this value at
  TSN T" query, and no API ever exposed one. An append-only version-history TAIL was built for this and removed in
  #666 — nothing called it, nothing pruned it, and it charged every `AllowMultiple` mutation on a `Versioned`
  component for a capability no caller could reach. Point-in-time reads work through the revision chain (see the
  related temporal-query feature), not through the index.

## 🧪 Tests

- [VersionedIndexTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/VersionedIndexTests.cs) — spawn/update/read on a `Versioned` component with `AllowMultiple` indexed fields, and a key move that must leave siblings in place

## 🔗 Related

- Sibling feature: [Multi-value secondary index (AllowMultiple)](./secondary-index-storage-modes/multi-value-secondary-index.md)
- Sibling feature: [Secondary Index Storage Modes](./secondary-index-storage-modes/README.md)
- Sibling feature: [Compound Move Operations](./compound-move-operations.md) — the single-traversal `MoveValue` this feature relies on

<!-- Deep dive: claude/design/Indexing/VersionedSecondaryIndexes.md -->
<!-- ADR: claude/adr/039-versioned-secondary-index-architecture.md -->
<!-- Deep dive: claude/overview/04-data.md §Versioned Secondary Indexes -->
