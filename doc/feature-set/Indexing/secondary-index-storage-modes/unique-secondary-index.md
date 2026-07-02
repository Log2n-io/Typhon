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

## 💻 Usage

```csharp
[Component("Game.Player", 1)]
struct Player
{
    [Index]   // unique — AllowMultiple defaults to false
    public int PlayerId;
    public String64 Name;
}

[Archetype(42)]
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
- A duplicate key on create or update throws `UniqueConstraintViolationException` (`TyphonErrorCode.UniqueConstraintViolation`,
  4001; non-transient) at `Commit()`, not at `Spawn`/`Write` time — the key is only resolved against the B+Tree at commit.
- `Transaction.EnumerateIndex` is the only read path; there is no separate `TryGet`-style point-lookup entry point
  in the public API — pass `minKey == maxKey` for a point lookup.
- Switching a field to `AllowMultiple` later is a schema change: it adds the 4-byte `ElementId` overhead to every
  existing component instance and reroutes commit-path operations from `Move`/`Remove` to `MoveValue`/`RemoveValue`.

## 🧪 Tests

- [BtreeTests](../../../../test/Typhon.Engine.Tests/Data/BTreeTests.cs) — `ForwardInsertionTest`/`ReverseInsertionTest`/`CheckTree`/`CheckRemove` family: Add/Remove correctness for the single-value B+Tree across all key widths, no `ElementId` overhead
- [BulkEnumerateTests](../../../../test/Typhon.Engine.Tests/Data/BulkEnumerateTests.cs) — `SecondaryIndex_UniqueField`: engine-level round trip through `GetIndexRef` + `Transaction.EnumerateIndex`

## 🔗 Related

- Parent feature: [Secondary Index Storage Modes](./README.md)
- Sibling: [Multi-value secondary index (AllowMultiple)](./multi-value-secondary-index.md)

<!-- Deep dive: claude/overview/04-data.md §4.7 B+Tree Indexes -->
<!-- Deep dive: claude/design/Indexing/public-api.md -->
<!-- Deep dive: claude/design/Errors/05-public-exception-catalog.md — Index chain -->
