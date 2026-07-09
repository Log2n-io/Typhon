---
uid: feature-ecs-component-collections
title: 'Component Collections'
description: 'Per-entity variable-length lists — owned data or entity-reference lists — without breaking fixed-size component layout.'
---

# Component Collections
> Per-entity variable-length lists — owned data or entity-reference lists — without breaking fixed-size component layout.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Ecs](./README.md)

## 🎯 What it solves

Components are fixed-size blittable structs stored column-major (SoA) — there's no room for a per-entity list of
unknown length: a path's waypoints, a player's inventory slots, a parent's child-entity list. `ComponentCollection<T>`
adds exactly that, for both owned value data (`ComponentCollection<Waypoint>`) and entity-reference lists
(`ComponentCollection<EntityLink<TArch>>`, the "collection on parent" side of a 1:N relationship — see
[Entity Relationships](./entity-relationships.md)), without growing the component's stride or wasting space on the
common short case.

## ⚙️ How it works (in brief)

A `ComponentCollection<T>` field is 4 bytes — a buffer id, nothing else. The elements live in a separate pool, shared
by every `ComponentCollection<T>` field across every archetype that uses that element type `T`. Mutation goes through
`Transaction.CreateComponentCollectionAccessor` (append-only `Add`, plus `ElementCount`/`GetAllElements` for bulk
read); read-only iteration goes through `Transaction.GetReadOnlyCollectionEnumerator` (cheap `foreach`, no write
intent). On a `SingleVersion` component the buffer is owned in place by the one committed slot, mutated directly. On
a `Versioned` component an overwrite duplicates the buffer id into the new revision and bumps a reference count; the
first mutation through that still-shared buffer clones it (copy-on-write), so an older MVCC snapshot keeps observing
the contents it originally read. `T` must be `unmanaged` — the same blittability constraint as a component field.

## 💻 Usage

```csharp
public struct PathData
{
    public float TotalLength;
    public ComponentCollection<Waypoint> Waypoints;   // owned value data, not entity refs
}

public struct Waypoint   // plain struct, not an archetype — no identity, no independent lifecycle
{
    public Vector3 Position;
    public float Speed;
}

// Append elements
EntityRef path = tx.OpenMut(pathId);
ref PathData data = ref path.Write<PathData>();
using (var cca = tx.CreateComponentCollectionAccessor(ref data.Waypoints))
{
    cca.Add(new Waypoint { Position = p0, Speed = 4.5f });
    cca.Add(new Waypoint { Position = p1, Speed = 3.0f });
}

// Bulk read
var read = tx.Open(pathId).Read<PathData>();
using var cca2 = tx.CreateComponentCollectionAccessor(ref read.Waypoints);
Span<Waypoint> all = stackalloc Waypoint[cca2.ElementCount];
cca2.GetAllElements(all);

// Lightweight foreach (no write intent)
foreach (ref readonly Waypoint wp in tx.GetReadOnlyCollectionEnumerator(ref read.Waypoints))
{
    // process wp
}
```

## ⚠️ Guarantees & limits

- Zero cost for components without a collection field — the field is 4 bytes, and the per-table bookkeeping that
  drives append/read/destroy is gated on the table actually declaring one.
- `SingleVersion`: the cluster slot is the buffer's sole owner; destroying the entity frees it automatically.
- `Versioned`: O(1) per overwrite (a reference-count bump, not an element copy) until a shared buffer is actually
  written, then the clone is O(K) where K = element count at that point. Storage is reclaimed when the owning
  revision is garbage-collected — a long-lived revision chain pins every distinct buffer it still references.
- **Not supported on `Transient` components** — registering one with a `ComponentCollection<T>` field throws at
  startup (`InvalidOperationException`). A Transient component doesn't survive restart; its collection buffer would,
  leaving an orphaned buffer with nothing to reference it.
- Public API is append-and-bulk-read (`Add`, `GetAllElements`, the read-only enumerator) — no per-element
  remove/replace through `ComponentCollectionAccessor<T>`.
- Insertion order is preserved; there is no secondary index over collection contents — finding "the entity whose
  collection contains X" requires an application-level scan, not a query.
- **Crash safety caveat:** collection buffer *content* reaches disk only at checkpoint, not WAL-redo-logged. A crash
  between a collection write and the next checkpoint can lose that write (the field's buffer id is recovered; the
  new elements are not). Collection durability is checkpoint-bounded, not commit-bounded — a known gap tracked
  separately, distinct from the rest of the commit path.
- `T` must be `unmanaged` (no references) — same constraint as any component field.

## 🧪 Tests

- [ComponentCollectionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ComponentCollectionTests.cs) — `Versioned` create/read/update, reference-count bump, copy-on-write clone on shared-buffer mutation, destroy frees the buffer
- [SvComponentCollectionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/SvComponentCollectionTests.cs) — `SingleVersion` in-place update (no new buffer), migrate/rollback buffer lifecycle, registering one on `Transient` throws
- [ClusterComponentCollectionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterComponentCollectionTests.cs) — collection field on a clustered `Versioned` archetype: spawn/update/migrate/destroy

## 🔗 Related

- Source: `src/Typhon.Engine/Ecs/public/ComponentCollection.cs` (`ComponentCollectionAccessor<T>`),
  `src/Typhon.Engine/Transactions/public/Transaction.cs` (`CreateComponentCollectionAccessor`, `GetReadOnlyCollectionEnumerator`)
- Related features: [Entity Relationships](./entity-relationships.md)

<!-- Deep dive: ADR-056 (claude/adr/056-cluster-componentcollection-storage.md), overview/04-data.md §4.16 (claude/overview/04-data.md), design/Ecs/08-entity-relationships.md (claude/design/Ecs/08-entity-relationships.md) -->
