# Revision Chain Storage (on-disk layout)
> The fixed-size, append-friendly layout that holds every live revision of a `Versioned` component on disk.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Revision](./README.md)

## 🎯 What it solves

MVCC needs somewhere to put the multiple concurrently-live revisions a `Versioned` component can accumulate while older snapshots still depend on them. That history has to be cheap to append to on every commit, cheap to walk on every read, and bounded in size so a hot entity doesn't grow without limit. A naive per-revision allocation (or a single growable array per entity) would fragment storage and make both append and reclaim expensive. Revision Chain Storage is the physical layout that makes append, walk, and reclaim all O(1)-ish operations instead of O(n) ones.

## ⚙️ How it works (in brief)

Each entity's component history for a given `Versioned` component type lives in a singly-linked chain of fixed 64-byte chunks — a circular buffer, so the oldest slot is reused once a chunk fills and its entries are no longer needed. The first chunk carries a small header (lock, item/chain counts, owning entity's primary key, a monotonic commit counter) plus a handful of revision entries; once those fill up, the chain grows by linking in additional 64-byte overflow chunks. Each revision entry is tiny — just enough to point at the actual component payload (stored separately, sized to the component) and to record when and by which transaction it was written. Capacity per chunk is derived from the entry's struct size at compile/runtime, not hardcoded, so the layout self-adjusts if the entry ever changes shape.

## 💻 Usage

This is a storage primitive, not something application code allocates or walks directly — it exists transparently under any `Versioned` component (the default storage mode), created on first write and grown automatically as revisions accumulate:

```csharp
[Component("Game.Health", 1)]                 // StorageMode.Versioned is the default
struct Health { public int Current; public int Max; }

[Archetype(7)]
partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Health> Health = Register<Health>();
}

using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
var soldier = tx.Spawn<Unit>(Unit.Health.Set(new Health { Current = 100, Max = 100 }));
tx.Commit();                                   // allocates the entity's first revision-chain chunk

using var hit = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
hit.OpenMut(soldier).Write(Unit.Health).Current -= 25;
hit.Commit();                                  // appends a new revision entry into the same chain
```

Read-only introspection of the storage footprint goes through the same segment enumeration as every other structure:

```csharp
foreach (var seg in dbe.EnumerateStorageSegments())
{
    if (seg.Kind == StorageSegmentKind.Revision)
    {
        Console.WriteLine($"revision table root={seg.RootPageIndex} stride={seg.Stride} " +
                           $"allocChunks={seg.AllocatedChunkCount} freeChunks={seg.FreeChunkCount}");
    }
}
```

## ⚠️ Guarantees & limits

- Every chunk is exactly 64 bytes (one cache line); chunk capacity (3 entries in the first chunk, 5 per overflow chunk today) is computed from the entry struct's size, not hardcoded — a layout change flows through automatically.
- One revision-chain chunk is the fixed minimum cost of a `Versioned` component per entity, even if it's never updated again after creation.
- A short revision history (up to the first chunk's capacity) needs no overflow allocation at all — this is the steady state most entities should stay in; the revision garbage collector (see GC entry) is what keeps long-lived entities from drifting out of it.
- The chain is singly-linked and circular by design: appending past capacity links a new chunk rather than copying existing entries, and reclaiming the oldest slot is an index bump, not a deallocation.
- A component payload (`ComponentChunkId == 0`) inside a revision entry means a tombstone — a delete, not a missing record — so older snapshots can still resolve what an entity looked like before it was deleted.
- Layout details (header/entry field order, packed bit layout) are internal and not part of the application-facing API surface; only the aggregate footprint is exposed, via `EnumerateStorageSegments`.

## 🧪 Tests

- [CompRevStorageElementTests](../../../test/Typhon.Engine.Tests/Data/CompRevStorageElementTests.cs) — layout-level unit tests: `Sizeof_Is12Bytes`, `ChunkCapacity_Root_Is3`/`ChunkCapacity_Overflow_Is5`, bitfield independence of `TSN`/`IsolationFlag`/`UowId`/`ComponentChunkId`, `Void`/`IsVoid` tombstone semantics

## 🔗 Related

- Deep dive: [doc/in-depth-overview/05-revision.md](../../in-depth-overview/05-revision.md)
- Related feature: [Revision Append & Chain Growth](./revision-append-write-path.md) (what populates this layout),
  [MVCC Snapshot Visibility](./mvcc-snapshot-visibility.md) (the reader that walks this chain)
- Sibling: [Storage Mode: Versioned](../Ecs/storage-modes/storage-mode-versioned.md) — Versioned mode is the ECS-facing side of this on-disk layout
- Source: [`ComponentRevisionManager`](../../../src/Typhon.Engine/Revision/internals/ComponentRevisionManager.cs), [`CompRevStorageHeader`/`CompRevStorageElement`](../../../src/Typhon.Engine/Ecs/public/ComponentTable.cs), [`RevisionEnumerator`](../../../src/Typhon.Engine/Revision/internals/RevisionEnumerator.cs)

<!-- Deep dive: claude/design/Revision/01-revision-chain-storage.md, claude/overview/04-data.md §4.6 Revision Chains -->
<!-- ADR: claude/adr/023-circular-buffer-revision-chains.md, claude/adr/027-even-sized-hot-path-structs.md -->
