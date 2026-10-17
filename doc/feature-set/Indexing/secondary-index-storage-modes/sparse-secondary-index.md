---
uid: feature-indexing-secondary-index-storage-modes-sparse-secondary-index
title: 'Sparse Secondary Index'
description: 'Key 0 means "no data" and is never indexed: an index over a field most entities leave unset costs only the entities that set it.'
---

# Sparse Secondary Index
> Key 0 means "no data" and is never indexed: an index over a field most entities leave unset costs only the entities that set it.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Indexing](../README.md)

## 🎯 What it solves

An optional integer field — a guild, a group, an owner, a target — is 0 for most entities. A dense index files every
one of them under key 0: on an `AllowMultiple` index that is one value buffer holding most of the archetype,
rewritten by every entity that joins or leaves; on a unique index it is impossible, since two entities cannot share
key 0. Declaring the index sparse makes 0 what it means everywhere else in Typhon — no data — and the index holds
only the entities that set the field.

## ⚙️ How it works (in brief)

The tree itself treats key 0 as the state each write asks for: an insert of 0 adds nothing, a removal of 0 succeeds,
a change from 0 to a value inserts, a change from a value to 0 removes, and a lookup of 0 finds nothing. The commit,
tick fence (including the batched moves), destroy and rebuild paths go through those entry points, so none of them tests
for 0; a migration stages no index update for an entity holding 0. On a unique sparse index any number of entities may
hold 0, and two entities still cannot hold one real key: a change from 0 onto a key another entity holds is refused like
any duplicate.

Queries stay correct without the 0s in the tree: a predicate whose range admits 0 over a sparse index is answered
from component data (the zone-map scan, or the sort fallback for an ordered query), and a range that excludes 0 uses
the tree as before.

## 💻 Usage

```csharp
[Component("Game.Member", 1, StorageMode = StorageMode.SingleVersion)]
struct Member
{
    [Index(AllowMultiple = true, Sparse = true)] public int Group;   // 0 = in no group
    [Index(Sparse = true)] public int Badge;                         // unique when set; any number hold 0
}
```

## ⚠️ Guarantees & limits

- Integer key types only — `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `char` and enums over
  them. `float`, `double` (whose `-0` equals `0`) and `String64` are refused at registration.
- 0 is fixed as the excluded key. It is not declarable: a field whose "none" is another value should be stored with
  0 meaning none.
- Turning `Sparse` on or off is a schema change: the flag is persisted, and the index is rebuilt from the data when
  the database opens with the changed declaration.
- `Transaction.EnumerateIndex` over a sparse index never returns key 0.

## 🧪 Tests

- [SparseBTreeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SparseBTreeTests.cs) — every entry point on both index kinds, and the tick fence's move batch on and off 0, whole and partitioned
- [SparseIndexTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/SparseIndexTests.cs) — spawn, writes on and off 0, destroy and unique enforcement on SingleVersion and Versioned archetypes, every query path checked against the data
- [SparseSchemaToggleTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Schema/SparseSchemaToggleTests.cs) — turning the flag on or off rebuilds the index at open

## 🔗 Related

- See also: [Multi-Value Secondary Index (AllowMultiple)](./multi-value-secondary-index.md), [Unique (Single-Value) Secondary Index](./unique-secondary-index.md)
- Rule: [IX-09](https://github.com/Log2n-io/Typhon/blob/main/rules/indexing.md#ix-09-a-sparse-index-never-files-key-0-and-nothing-reads-one-as-if-it-had-silent)
