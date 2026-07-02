# Transient
> Heap-only component storage for scratch data that should never touch disk.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Ecs](../README.md)

## 🎯 What it solves

Some component data is genuinely throwaway between runs — animation state, input buffers, pathfinding scratch,
targeting info. Paying any persistence cost (WAL, checkpoint, page-cache writeback) for data nobody needs to
survive a crash is pure overhead. `Transient` removes that cost entirely: the data lives only in process
memory, is structurally part of the same ECS entity as your durable components, and uses the same
`Read<T>()`/`Write<T>()` API as every other mode.

## ⚙️ How it works (in brief)

A `Transient` component's chunks live in pinned heap memory rather than the memory-mapped file — there is no
WAL record, no checkpoint participation, and no dirty tracking; the JIT eliminates the dirty-tracking branches
entirely for this mode. Reads and writes are direct pointer arithmetic. On crash or restart, `Transient`
component segments come back empty — the application is responsible for re-initializing any state it needs.

## 💻 Usage

```csharp
[Component("Game.AnimState", 1, StorageMode = StorageMode.Transient)]
public struct AnimState
{
    public int ClipId;
    public float Time;
}

[Archetype(12)]
partial class Actor : Archetype<Actor>
{
    public static readonly Comp<AnimState> Anim = Register<AnimState>();
}

using var tx = dbe.CreateQuickTransaction();
var id = tx.Spawn<Actor>(Actor.Anim.Set(new AnimState { ClipId = 3, Time = 0f }));
tx.Commit();

using var tx2 = dbe.CreateQuickTransaction();
var e = tx2.OpenMut(id);
ref var anim = ref e.Write(Actor.Anim);
anim.Time += dt;               // ~3-5 ns, no dirty tracking, no WAL
```

| Option | Default | Effect |
|--------|---------|--------|
| `TransientOptions.MaxMemoryBytes` | 256 MB | `AllocateChunk` throws once total `Transient` allocation across the engine exceeds the cap |

## ⚠️ Guarantees & limits

- All data is lost on crash and on every restart — there is no recovery path, by design.
- Write/read cost ~3-5 ns — the cheapest mode in the engine, comparable to raw Flecs/DOTS arrays.
- No locking, no torn-data protection, no isolation — "developer owns concurrency" (same model as Unity
  DOTS/Flecs); two concurrent writes to the same component are undefined.
- An entity can mix `Transient` components with `Versioned`/`SingleVersion` ones; on recovery only the
  non-`Transient` components come back — the application must re-initialize `Transient` state for surviving
  entities.
- `ComponentCollection<T>` (variable-length) fields are supported on `Transient`.
- `ReadsSnapshot` is rejected for `Transient` components — there is no history to freeze to.

## 🧪 Tests

- [StorageModeReadWriteTests](../../../../test/Typhon.Engine.Tests/Data/StorageModeReadWriteTests.cs) — `Transient` spawn/read/write, no dirty-bitmap tracking, rollback frees chunks
- [ClusterTransientTests](../../../../test/Typhon.Engine.Tests/Data/ECS/ClusterTransientTests.cs) — pure-`Transient` cluster has no page-cache segment (heap-only), mixed SV+Transient bulk iteration

## 🔗 Related

- Sibling: [Entity Clusters](../entity-clusters.md) — `Transient` components live in a parallel heap-backed cluster segment with the same SoA layout
- Sibling: [SingleVersion (Tick-Fence Durable)](./storage-mode-singleversion.md) — same near-zero write cost, but durable to the last tick fence instead of never persisted
- Parent feature: [Storage Modes](./README.md)

<!-- Deep dive: claude/design/Ecs/06-storage-modes.md — TransientStore Memory Management, Transient Concurrency -->
