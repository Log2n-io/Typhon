---
uid: feature-indexing-secondary-index-storage-modes-unique-secondary-index
title: 'Unique (Single-Value) Secondary Index'
description: 'One key maps to exactly one entity — the B+Tree value is a chunk-id directly, no buffer indirection, no per-entity overhead.'
---

# Unique (Single-Value) Secondary Index
> One key maps to exactly one entity — the B+Tree value is a chunk-id directly, no buffer indirection, no per-entity overhead.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Indexing](../README.md)

## 🎯 What it solves

Fields that are inherently 1:1 with the entity that owns them — a player ID, an item SKU, a session token — need
a lookup whose stored value *is* the entity reference, not a pointer to a set that happens to contain one entry.
Modeling such a field as unique gets exactly that representation, with no indirection and no per-entity
bookkeeping cost paid for a "set" that can never hold more than one member.

## ⚙️ How it works (in brief)

For a unique field, the B+Tree value at each key is the entity's component chunk-id, stored directly in the leaf
— there is no separate HEAD buffer and no hidden `ElementId` added to the component's storage layout. On commit,
`Add` inserts a brand-new key, `Move` atomically relocates an existing key in a single tree traversal when the
indexed field's value changes (write-locking at most two leaves — the old key's and the new key's), and `Remove`
deletes the key outright on entity deletion. A second entity attempting to claim an already-mapped key is
rejected at commit, since the value slot has room for exactly one chunk-id.

## 📏 Scope of the guarantee

**A unique index is unique within one archetype tree, not database-wide.** The reason is where the index lives: there
is one B+Tree per (archetype, indexed field), so a unique field gets one tree per archetype tree and the duplicate
check is a single descent into it.

Two archetypes in *unrelated* trees may each declare the same unique-indexed component. Each already owns its own
B+Tree, so the two constraints are independent, cost nothing extra, and cover disjoint sets of entities — a key value
may legitimately appear once in each. No query can return both either, since a query names an archetype and matches
only its own subtree.

Declaring the component on **two archetypes of the same tree** is rejected at **build time** (`TPH1003`): they would
own two separate trees under one root with nothing spanning them, so enforcing uniqueness between them would mean
probing every sibling tree on each insert — and that probe is not atomic with the insert, so two concurrent inserts of
the same key would both pass. The error names both archetypes, their tree root, and the two fixes: declare the
component on their common ancestor so one tree covers the whole subtree, or use `[Index(AllowMultiple = true)]`. A
component re-declared inside one inheritance chain is rejected the same way (`TPH1004`) — the duplicate would silently
consume a second component slot that nothing can address.

> [!IMPORTANT]
> **Within a tree, the constraint is still enforced per archetype today.** Each archetype owns its own B+Tree for the
> indexed field and the duplicate check is a lookup in *that* tree, so two entities in different archetypes of one
> subtree can still hold the same key, and a query over the ancestor returns both. The schema rule above closes the
> half that can be settled at declaration time; enforcing the subtree scope itself needs a subtree-scoped structure and
> is tracked by [#678](https://github.com/Log2n-io/Typhon/issues/678). The per-archetype trees and the storage layout
> described above are unaffected by that work.

## 💻 Usage

```csharp
[Component("Game.Player", 1)]
struct Player
{
    [Index]   // unique — AllowMultiple defaults to false
    public int PlayerId;
    public String64 Name;
}

[Archetype]
partial class PlayerArchetype : Archetype<PlayerArchetype>
{
    public static readonly Comp<Player> P = Register<Player>();
}

// Cold path — resolve once, reuse on the hot path
var idIndex = dbe.GetIndexRef<Player, int>(p => p.PlayerId);

using (var tx = dbe.CreateQuickTransaction())
{
    tx.Spawn<PlayerArchetype>(PlayerArchetype.P.Set(new Player { PlayerId = 42, Name = "Nova" }));
    tx.Commit();
}

// Point lookup — minKey == maxKey; the value found is the entity's own chunk-id, no indirection to resolve
using (var tx = dbe.CreateQuickTransaction())
{
    using var hit = tx.EnumerateIndex<Player, int>(idIndex, 42, 42);
    foreach (var entry in hit)
    {
        // entry.EntityPK, entry.Key, entry.Component
    }
}
```

## ⚠️ Guarantees & limits

- Zero storage overhead beyond the field itself — no hidden `ElementId`, and no TAIL history segment is allocated
  on account of this index.
- `Move` is the commit-time operation for a value change: one descent, at most two leaf write-locks, never a
  separate remove-then-insert pair.
- A duplicate key **within one archetype** on create or update throws `UniqueConstraintViolationException`
  (`TyphonErrorCode.UniqueConstraintViolation`, 4001; non-transient) at `Commit()`, not at `Spawn`/`Write` time — the
  key is only resolved against the B+Tree at commit.
- **A duplicate key across two archetypes sharing the component is accepted, and both entities remain reachable.**
  The scope of the constraint is the archetype, not the archetype tree — see the note above and
  [#678](https://github.com/Log2n-io/Typhon/issues/678). Model a field as unique only when the entities carrying it
  all live in one archetype, or accept that the check does not span siblings.
- `Transaction.EnumerateIndex` is the only read path; there is no separate `TryGet`-style point-lookup entry point
  in the public API — pass `minKey == maxKey` for a point lookup.
- Switching a field to `AllowMultiple` later is a schema change: it adds the 4-byte `ElementId` overhead to every
  existing component instance and reroutes commit-path operations from `Move`/`Remove` to `MoveValue`/`RemoveValue`.

## 🧪 Tests

- [BtreeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/BTreeTests.cs) — `ForwardInsertionTest`/`ReverseInsertionTest`/`CheckTree`/`CheckRemove` family: Add/Remove correctness for the single-value B+Tree across all key widths, no `ElementId` overhead
- [BulkEnumerateTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/BulkEnumerateTests.cs) — `SecondaryIndex_UniqueField`: engine-level round trip through `GetIndexRef` + `Transaction.EnumerateIndex`

## 🔗 Related

- Parent feature: [Secondary Index Storage Modes](./README.md)
- Sibling: [Multi-value secondary index (AllowMultiple)](./multi-value-secondary-index.md)

<!-- Deep dive: claude/design/Indexing/index-scope-and-uniqueness.md — subtree scope, #678 -->
<!-- Deep dive: claude/overview/04-data.md §4.7 B+Tree Indexes -->
<!-- Deep dive: claude/design/Indexing/public-api.md -->
<!-- Deep dive: claude/design/Errors/05-public-exception-catalog.md — Index chain -->
