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
by every `ComponentCollection<T>` field across every archetype that uses that element type `T`. A stored component's
collection is changed through `EntityRefMut.CreateComponentCollectionAccessor(comp, ref copy, ref copy.Field)` on a copy
read from the handle — `Add`, `Replace(span)`, `Clear()`. Opening it records the write as a `Set` would, and whenever the
accessor moves to another buffer it stores that buffer into the component at once, so no `Set` is needed for the
collection — and `Set` never changes one: a value's collection handle is ignored when empty and refused when it names a
buffer the entity does not hold. A value not stored yet (one being built for a spawn) and bulk reads go through
`Transaction.CreateComponentCollectionAccessor`. Read-only iteration goes through `Transaction.GetReadOnlyCollectionEnumerator`
(cheap `foreach`, no write intent). On a `SingleVersion` component the buffer is owned in place by the one committed
slot, mutated directly — except under the `Commit` discipline, where the write is staged: the buffer the committed value
names is cloned or replaced, never edited, and released when the commit publishes. On a `Versioned` component an
overwrite duplicates the buffer id into the new revision and bumps a reference count; the first mutation through that
still-shared buffer clones it (copy-on-write), so an older MVCC snapshot keeps observing the contents it originally
read. The handle's accessor creates the new revision before it hands out the accessor: that is what makes the buffer
shared, so the clone happens (#1199). `Replace` writes a fresh buffer and releases the previous one — one reference when
it is shared, so older snapshots keep it. `T` must be `unmanaged` — the same blittability constraint as a component field.

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

[Archetype]
partial class Path : Archetype<Path>
{
    public static readonly Comp<PathData> Data = Register<PathData>();
}

// Build the collection of a new value, then spawn it
var fresh = new PathData { TotalLength = 0 };
using (var cca = tx.CreateComponentCollectionAccessor(ref fresh.Waypoints))
{
    cca.Add(new Waypoint { Position = p0, Speed = 4.5f });
}
EntityId pathId = tx.Spawn<Path>(Path.Data.Set(in fresh));

// Append to a stored component: through the handle — disposing the accessor stores the collection
EntityRefMut path = tx.OpenMut(pathId);
PathData data = path.Read(Path.Data);
using (var cca = path.CreateComponentCollectionAccessor(Path.Data, ref data, ref data.Waypoints))
{
    cca.Add(new Waypoint { Position = p1, Speed = 3.0f });
}
// Changing another field too? Set the copy afterwards — it carries the same buffer.
data.TotalLength += Vector3.Distance(p0, p1);
path.Set(Path.Data, data);

// Remove or reorder: read, edit, replace the whole content (an empty span clears it)
data = path.Read(Path.Data);
using (var cca = path.CreateComponentCollectionAccessor(Path.Data, ref data, ref data.Waypoints))
{
    var kept = Array.FindAll(cca.GetAllElements(), w => w.Speed > 0);
    cca.Replace(kept);
}

// Bulk read
var read = tx.Open(pathId).Read(Path.Data);
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
- Public API is append, whole-content replace and bulk read (`Add`, `Replace`, `Clear`, `GetAllElements`, the read-only
  enumerator). There is no per-element remove: removing is read → filter → `Replace`, O(n) — as a per-element remove
  would be, since elements are stored in insertion order across chained chunks.
- A cleared collection holds no buffer (handle 0).
- `SingleVersion` under the `TickFence` discipline: `Replace`/`Clear` free the previous buffer at once, and a rollback
  does not bring it back — TickFence writes are not undone. Under the `Commit` discipline they are, like any staged write.
- `Set` (and `ClusterRef.Set`, `Teleport`) keeps the stored collection: an empty handle in the value is ignored, a
  handle naming a buffer the entity does not hold (a copy read before the collection changed) throws
  `InvalidOperationException`. `Enable(comp, value)` adopts the value's collection into a component that had none.
  `ClusterRef.Get`/`GetSpan` are raw memory: never write a collection handle through them.
- Opening an accessor on a stale copy throws: read the component again after its collection changed. The
  transaction's own accessor (`Transaction.CreateComponentCollectionAccessor`) refuses `Replace`/`Clear` over a
  collection it did not build.
- Dispose every accessor (`using`): it holds pages until disposed.
- Change a stored component's collection through the handle, never through `Transaction.CreateComponentCollectionAccessor`
  on a copy: on a `Versioned` component the transaction's accessor would edit, in place, the buffer the committed
  revision still points to, and an older snapshot would see the change.
- Insertion order is preserved; there is no secondary index over collection contents — finding "the entity whose
  collection contains X" requires an application-level scan, not a query.
- **Crash safety:** collection content is WAL-logged at commit alongside the component value — durability follows
  the unit of work's `DurabilityMode`, the same as any other component field. A recovered buffer gets a fresh
  buffer id (the id is not stored in the WAL; the *elements* are what recovery preserves).
- `T` must be `unmanaged` (no references) — same constraint as any component field.

## 🧪 Tests

- [ComponentCollectionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ComponentCollectionTests.cs) — `Versioned` create/read/update, reference-count bump, copy-on-write clone on shared-buffer mutation, an older snapshot unaffected by a handle-made change, destroy frees the buffer
- [SvComponentCollectionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/SvComponentCollectionTests.cs) — `SingleVersion` in-place update (no new buffer), migrate/rollback buffer lifecycle, registering one on `Transient` throws
- [ClusterComponentCollectionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterComponentCollectionTests.cs) — collection field on a clustered `Versioned` archetype: spawn/update/migrate/destroy
- [ComponentCollectionReplaceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ComponentCollectionReplaceTests.cs) — `Replace`/`Clear` in every storage mode and discipline: an older snapshot keeps its content, rollback with or without dispose, publish/rollback/destroy under the `Commit` discipline, `Set` keeping the stored collection
- [CollectionDurabilityTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CollectionDurabilityTests.cs) — content across a hard crash, including a replaced and a cleared collection, and a `SingleVersion` append through the handle with no `Set`

## 🔗 Related

- Source: `src/Typhon.Engine/Ecs/public/ComponentCollection.cs` (`ComponentCollectionAccessor<T>`),
  `src/Typhon.Engine/Ecs/public/EntityRefMut.cs` (`CreateComponentCollectionAccessor`),
  `src/Typhon.Engine/Transactions/public/Transaction.cs` (`CreateComponentCollectionAccessor`, `GetReadOnlyCollectionEnumerator`)
- Related features: [Entity Relationships](./entity-relationships.md)

<!-- Deep dive: ADR-056 (claude/adr/056-cluster-componentcollection-storage.md), overview/04-data.md §4.16 (claude/overview/04-data.md), design/Ecs/08-entity-relationships.md (claude/design/Ecs/08-entity-relationships.md) -->
