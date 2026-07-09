---
uid: feature-indexing-secondary-index-storage-modes-multi-value-secondary-index
title: 'Multi-Value Secondary Index (AllowMultiple)'
description: 'Many entities share one key — the B+Tree value is a growable HEAD buffer of chunk-ids, at a fixed +4-byte-per-entity cost.'
---

# Multi-Value Secondary Index (AllowMultiple)
> Many entities share one key — the B+Tree value is a growable HEAD buffer of chunk-ids, at a fixed +4-byte-per-entity cost.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Indexing](../README.md)

## 🎯 What it solves

Grouping fields — a guild ID shared by every member, a status or rarity tier with a handful of distinct values —
need many entities mapped to the same key. A unique index's one-value-per-key slot cannot hold that. The
`AllowMultiple` storage mode stores an indirected, variable-length set of chunk-ids per key instead, sized to the
group's actual membership rather than reserved up front for a worst case.

## ⚙️ How it works (in brief)

Each key's B+Tree value is a HEAD buffer ID — a `VariableSizedBufferSegment` holding the current set of entity
chunk-ids that share the key. To remove or relocate a single entity's slot inside that buffer in O(1) instead of
scanning it, every `AllowMultiple`-indexed field carries a hidden 4-byte `ElementId` (the entity's own slot
within the HEAD buffer) added to the component's storage overhead — this is paid by every component instance with
such a field, not just ones currently sharing a key with others. Commit-time mutation runs `MoveValue` (value
change) and `RemoveValue` (deletion) instead of the unique path's `Move`/`Remove`, both addressed directly by that
`ElementId`. On `Versioned` components, every gain or loss additionally appends to a per-key TAIL history buffer
— a single `TailVSBS` segment shared by all `AllowMultiple` indexes on the table, allocated once if any exist and
populated lazily per key on that key's first mutation. `SingleVersion`/`Transient` components get the HEAD buffer
only — no revision chain means there is no history to keep.

## 💻 Usage

```csharp
[Component("Game.GuildMember", 1)]   // Versioned by default
struct GuildMember
{
    [Index(AllowMultiple = true)]
    public long GuildId;
    public String64 Name;
}

[Archetype(43)]
partial class MemberArchetype : Archetype<MemberArchetype>
{
    public static readonly Comp<GuildMember> M = Register<GuildMember>();
}

var guildIndex = dbe.GetIndexRef<GuildMember, long>(m => m.GuildId);

using (var tx = dbe.CreateQuickTransaction())
{
    tx.Spawn<MemberArchetype>(MemberArchetype.M.Set(new GuildMember { GuildId = 7, Name = "Aria" }));
    tx.Spawn<MemberArchetype>(MemberArchetype.M.Set(new GuildMember { GuildId = 7, Name = "Beck" }));
    tx.Commit();
}

// Enumerate every current member of guild 7 — minKey == maxKey selects one key's whole group
using (var tx = dbe.CreateQuickTransaction())
{
    using var members = tx.EnumerateIndex<GuildMember, long>(guildIndex, 7, 7);
    foreach (var entry in members)
    {
        // entry.EntityPK, entry.Key, entry.Component
    }
}
```

## ⚠️ Guarantees & limits

- Every `AllowMultiple`-indexed field costs +4 bytes (`ElementId`) per component instance, regardless of storage
  mode — paid even while the field's current value happens to be unique to one entity.
- TAIL history exists only for `AllowMultiple` indexes on `Versioned` components (see the Storage Mode Feature
  Matrix in `claude/overview/04-data.md`); `SingleVersion`/`Transient` carry the HEAD buffer only, with no history.
- `MoveValue`/`RemoveValue` do strictly more commit-time work than the unique path's `Move`/`Remove`: they splice
  an entry into and/or out of a HEAD buffer, and on `Versioned` tables also append TAIL entries — cost is
  proportional to the change being made, not to the group's size.
- HEAD buffer membership order is not guaranteed (an unordered set with swap-compact removal); only the ordering
  of keys across a range scan is guaranteed ascending.
- TAIL entries are pruned once no active transaction's snapshot can still need them (the `MinTSN` boundary); one
  boundary entry per chain is retained so a reader exactly at the prune point still resolves correctly.

## 🧪 Tests

- [BtreeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/BTreeTests.cs) — `CheckMultipleTree`/`CheckByteMultipleTree`/`CheckFloatMultipleTree`: HEAD buffer growth, `RemoveValue` by `ElementId`, and BTree-entry cleanup once a key's last element is removed
- [BulkEnumerateTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/BulkEnumerateTests.cs) — `SecondaryIndex_AllowMultiple`: multiple entities sharing one key, read back via `GetIndexRef` + `Transaction.EnumerateIndex`

## 🔗 Related

- Parent feature: [Secondary Index Storage Modes](./README.md)
- Sibling: [Unique (single-value) secondary index](./unique-secondary-index.md)
- See also: [Versioned (HEAD/TAIL) Secondary Indexes for MVCC](../versioned-secondary-indexes.md), [Temporal (Point-in-Time) Index Query](../temporal-index-query.md)

<!-- Deep dive: claude/overview/04-data.md §4.7 B+Tree Indexes — Versioned Secondary Indexes (HEAD/TAIL) -->
<!-- Deep dive: claude/design/Indexing/VersionedSecondaryIndexes.md -->
<!-- Deep dive: claude/design/Indexing/public-api.md -->
