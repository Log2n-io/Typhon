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
read from the handle; on dispose the accessor stores the buffer it ended on into the component, so no `Set` is needed for
the collection. A value not stored yet (one being built for a spawn) and bulk reads go
through `Transaction.CreateComponentCollectionAccessor` (append-only `Add`, plus `ElementCount`/`GetAllElements`).
Read-only iteration goes through `Transaction.GetReadOnlyCollectionEnumerator` (cheap `foreach`, no write intent). On a `SingleVersion` component the buffer is owned in place by the one committed slot, mutated directly. On
a `Versioned` component an overwrite duplicates the buffer id into the new revision and bumps a reference count; the
first mutation through that still-shared buffer clones it (copy-on-write), so an older MVCC snapshot keeps observing
the contents it originally read. The handle's accessor creates the new revision before it hands out the accessor:
that is what makes the buffer shared, so the clone happens (#1199). `T` must be `unmanaged` — the same blittability constraint as a component field.

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
- Public API is append-and-bulk-read (`Add`, `GetAllElements`, the read-only enumerator) — no per-element
  remove/replace through `ComponentCollectionAccessor<T>`.
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

## 🔗 Related

- Source: `src/Typhon.Engine/Ecs/public/ComponentCollection.cs` (`ComponentCollectionAccessor<T>`),
  `src/Typhon.Engine/Ecs/public/EntityRefMut.cs` (`CreateComponentCollectionAccessor`),
  `src/Typhon.Engine/Transactions/public/Transaction.cs` (`CreateComponentCollectionAccessor`, `GetReadOnlyCollectionEnumerator`)
- Related features: [Entity Relationships](./entity-relationships.md)

<!-- Deep dive: ADR-056 (claude/adr/056-cluster-componentcollection-storage.md), overview/04-data.md §4.16 (claude/overview/04-data.md), design/Ecs/08-entity-relationships.md (claude/design/Ecs/08-entity-relationships.md) -->
