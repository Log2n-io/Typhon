---
uid: overview-ecs
title: '06 — ECS'
description: 'Typhon''s data model is ECS — Entity, Component, System. Entities are 64-bit identifiers, components are blittable unmanaged structs, and archetypes declare…'
---

# 06 — ECS

**Code:** [`src/Typhon.Engine/Ecs/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Ecs)

Typhon's data model is ECS — Entity, Component, System. Entities are 64-bit identifiers, components are blittable `unmanaged` structs, and archetypes declare which components an entity has. The ECS API in this folder is what application code actually touches: `Spawn`, `Destroy`, `Open`, `OpenMut`, plus the supporting types (`EntityId`, `EntityRef`, `Comp<T>`, `EntityLink<T>`, `PointInTimeAccessor`, `ClusterRef<TArch>`).

If you've used Unity DOTS, Bevy, or flecs the shape will feel familiar — but Typhon's ECS gives you a **per-component storage-mode choice** that other ECSs don't: each component declares whether it's **Versioned** (full MVCC snapshot isolation, [05-revision](05-revision.md)), **SingleVersion** (in-place, no isolation), or **Transient** (in-memory, lost on restart). Versioned components flow through the **transaction model** ([08-transactions](08-transactions.md)) and the WAL; SV/Transient bypass MVCC entirely and write straight to the slot. The storage layer ([02-storage](02-storage.md)) backs all three. §8 below covers how to pick a mode and what each gives up.

<a href="assets/typhon-data-engine-overview.svg">
  <img src="assets/typhon-data-engine-overview.svg" width="1200" alt="Data engine overview">
</a>
<br>
<sub>The ECS data engine: application calls (<code>Spawn</code> / <code>Destroy</code> / <code>Open</code> / <code>OpenMut</code>) run on a <code>Transaction</code>; entities route through the per-archetype <code>EntityMap</code> → <code>EntityRecord</code> (no PK index); <code>ComponentTable</code> owns segments per storage mode (Versioned → <code>ComponentSegment</code> + <code>CompRevTableSegment</code>; SV → <code>ComponentSegment</code>; Transient → <code>TransientComponentSegment</code>); secondary B+Tree indexes and the schema system sit alongside.</sub>

---

## 1. The model

| Concept | What it is |
|---|---|
| **Entity** | A unique identifier ([`EntityId`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EntityId.cs)). Holds no data of its own. |
| **Component** | An `unmanaged` struct attached to an entity. Components are *data*, not behaviour. |
| **Archetype** | A typed shape — the set of components an entity has. Declared as a C# class inheriting [`Archetype<TSelf>`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/Archetype.cs). |
| **System** | Behaviour — runs in the scheduler ([10-runtime](10-runtime.md)). Reads/writes components via accessors. |

Every entity belongs to exactly one archetype, fixed at spawn time. Components are stored archetype-major: all `Position` data for archetype `Ant` is contiguous, separately from the `Position` data for archetype `Building`. This is the standard ECS layout — branch-free iteration, cache-friendly scans.

### Archetype declaration

```csharp
[Archetype(Id = 42)]
public sealed partial class Ant : Archetype<Ant>
{
    public static readonly Comp<Position>  Position  = Register<Position>();
    public static readonly Comp<Velocity>  Velocity  = Register<Velocity>();
    public static readonly Comp<Health>    Health    = Register<Health>();
}
```

The `[Archetype(Id = N)]` attribute gives the archetype a stable 12-bit ID (max 4095). The `Register<T>()` calls declare components — the runtime resolves slot indices lazily on first access. CRTP (`Archetype<Ant>`) gives compile-time type identity without static-init ordering hazards.

The class is declared **`partial`** so a source generator can extend it — see **§5 Generated accessors** below. Without `partial` the archetype still works for `Spawn` / `Open` / `OpenMut`, but the generated typed bulk accessors aren't emitted.

Inheritance is single-parent:

```csharp
public sealed partial class FlyingAnt : Archetype<FlyingAnt, Ant>
{
    public static readonly Comp<Wings> Wings = Register<Wings>();
}
```

Parent components are inherited; slot indices start with the parent's. No diamond inheritance.

---

## 2. Identity

### `EntityId`

[`Ecs/public/EntityId.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EntityId.cs)

8 bytes, packed:

| Bits | Field |
|---|---|
| 0–11 | ArchetypeId (12 bits, max 4095) — routes to per-archetype storage |
| 12–63 | EntityKey (52 bits, ~4.5 × 10¹⁵) — monotonic per-archetype, never recycled |

```csharp
public readonly struct EntityId : IEquatable<EntityId>
{
    public long  EntityKey   { get; }
    public ushort ArchetypeId { get; }
    public bool   IsNull      { get; }
    public static readonly EntityId Null;
}
```

**Why monotonic, never recycled:** no ABA hazards — a stale `EntityId` cached anywhere in the system either matches a still-live entity (correct) or matches nothing (correct). No version field needed, no recycling races.

### `EntityLink<T>` — typed references

[`Ecs/public/EntityLink.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EntityLink.cs)

Compile-time-safe wrapper. `EntityLink<Building>` accepts `Building`, `House`, or any descendant — polymorphic at the archetype level.

```csharp
public sealed partial class Worker : Archetype<Worker>
{
    public static readonly Comp<EntityLink<Building>> Home = Register<EntityLink<Building>>();
}
```

Stored inline — implicit conversions to and from `EntityId` keep the call sites clean.

---

## 3. Component handles

### `Comp<T>` — typed component handle

[`Ecs/public/Comp.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/Comp.cs)

```csharp
public readonly struct Comp<T> where T : unmanaged
{
    public ComponentValue Set(in T value);      // for use in Spawn
    public ComponentValue Default();
}
```

`Comp<T>` carries the global `ComponentTypeId`. The same component type used in multiple archetypes shares the same `ComponentTypeId`, so the engine can route data correctly regardless of which archetype holds it. Slot resolution happens at runtime via `ArchetypeMetadata.GetSlot(componentTypeId)`.

### `ComponentValue` — spawn-time payload

[`Ecs/public/ComponentValue.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/ComponentValue.cs)

128-byte struct: 12-byte header (`ComponentTypeId` + `DataSize` + pad) + 112-byte inline payload + 4-byte tail pad. `MaxPayloadSize = 112`.

Components larger than 112 bytes don't fit in a `ComponentValue` — for those, spawn with the smaller defaults, then `OpenMut` and write the payload incrementally. (In practice, most ECS components are small; if you're routinely over 112 B, that's a sign to split the component.)

---

## 4. Lifecycle: Spawn / Destroy

These live on `Transaction` ([08-transactions](08-transactions.md)) — they're mutations and need transactional context.

### `Spawn<TArch>`

[`Transactions/public/Transaction.ECS.cs:147`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/public/Transaction.ECS.cs)

```csharp
EntityId id = tx.Spawn<Ant>(
    Ant.Position.Set(new Position(10, 20)),
    Ant.Health.Set(new Health(100))
);
```

- Allocates a fresh `EntityKey` (monotonic `Interlocked.Increment` on the per-archetype `NextEntityKey`).
- Allocates chunks in the relevant `ComponentTable`s (Versioned components go into `ComponentSegment`, Transient into `TransientComponentSegment`).
- Copies provided `ComponentValue` payloads; unspecified components are zero-initialized and disabled (see EnabledBits below).
- The entity is held in a **pending spawn list** on the `Transaction`; insertion into the per-archetype `EntityMap` happens at **commit** with `BornTSN = TSN`.

So a spawn is visible to *this* transaction immediately, visible to *other* transactions only after commit.

### `Destroy(EntityId)`

[`Transactions/public/Transaction.ECS.cs:562`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/public/Transaction.ECS.cs)

```csharp
tx.Destroy(id);
```

Marks the entity for destruction. The `EntityRecord` is stamped with a `DeadTSN`. Other transactions still see the entity until their snapshot TSN passes `DeadTSN`. Cleanup of revision storage happens lazily via the [`DeferredCleanupManager`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/DeferredCleanupManager.cs) once no active snapshot can see the entity any more.

### `SpawnBatch<TArch>`

For mass spawning (e.g., level load). Amortizes the per-call overhead: one `EnsureMutable` check, one `Interlocked.Add` for all entity keys, one epoch refresh at the end.

```csharp
Span<EntityId> ids = stackalloc EntityId[1000];
tx.SpawnBatch<Ant>(ids, Ant.Health.Set(new Health(100)));
```

---

## 5. Access: Open / OpenMut

Reading and mutating components flows through accessors. Three flavours, depending on the context.

### `EntityAccessor` — base read accessor

[`Ecs/public/EntityAccessor.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EntityAccessor.cs), [`EntityAccessor.ECS.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EntityAccessor.ECS.cs)

```csharp
EntityRef e = accessor.Open(id);                    // read-only
if (accessor.TryOpen(id, out EntityRef e2)) { ... }
EntityRef em = accessor.OpenMut(id);                // SV/Transient writes only
```

- `Open` and `TryOpen` resolve the entity at the accessor's `TSN`, applying MVCC visibility (`BornTSN ≤ TSN < DeadTSN`) and `EnabledBits` overrides.
- The returned `EntityRef` is a `ref struct` — stack-allocated, must not outlive the accessor that created it.
- `OpenMut` on the **base** `EntityAccessor` is for **SingleVersion / Transient** components only. Versioned writes need a `Transaction.OpenMut` override (which adds `EnsureMutable` + state transition).

### `Transaction` (extends `EntityAccessor`)

Inside a `Transaction`, `OpenMut` upgrades to the full mutating path: the transaction is marked `InProgress`, revision chains get extended on commit, the `UowId` from the transaction's `UnitOfWorkContext` ([01-foundation](01-foundation.md)) gets stamped on every new revision element.

### `PointInTimeAccessor` — parallel reads

[`Ecs/public/PointInTimeAccessor.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/PointInTimeAccessor.cs)

For parallel query systems that read at a single frozen MVCC snapshot across many worker threads.

```csharp
var pta = new PointInTimeAccessor();
pta.Attach(dbe, workerCount);     // allocates fresh TSN, resets per-worker accessors
// dispatch parallel work — each worker uses pta.GetAccessor(workerId).Open(...)
```

- One per-worker `EntityAccessor` is stored in a flat `EntityAccessor[]` indexed by `workerId` — no per-entity dictionary lookup.
- Reused across ticks: `Attach()` allocates a fresh TSN and resets accessors *while preserving the warm `ChunkAccessor` page caches* — zero allocation after first-tick warmup.
- Cannot Spawn / Destroy / Commit / Rollback. Cannot write Versioned components (throws). SV/Transient writes are supported.

PTA is the right tool when the workload is "read a lot, in parallel, at one consistent snapshot" — e.g., a `QuerySystem` that computes statistics over all entities of an archetype.

### `ArchetypeAccessor<TArch>` — fast path

[`Ecs/public/ArchetypeAccessor.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/ArchetypeAccessor.cs)

```csharp
ArchetypeAccessor<Ant> ants = accessor.For<Ant>();
EntityRef ant = ants.Open(id);
```

Pre-bound to a specific archetype. Bypasses epoch checks, archetype lookup, and MVCC visibility on every `Open` call — intended for PTA workers in parallel `QuerySystem`s where these checks are amortized to once per dispatch, not once per entity.

### Generated accessors — `ReadAll` / `ReadWriteAll`

`EntityRef` resolves components one at a time (`e.Read(Ant.Position)`). When you want *all* of an archetype's components at once with compile-time field names, Typhon generates them. [`ArchetypeAccessorGenerator`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Generators/ArchetypeAccessorGenerator.cs) is an incremental Roslyn source generator that, for every `[Archetype]` **`partial`** class, emits:

- a `Refs` ref struct (read-only) and a `MutRefs` ref struct (mutable), one typed field per component, and
- static `ReadAll(tx, id)` → `Refs` and `ReadWriteAll(tx, id)` → `MutRefs` methods,

into `{ArchetypeName}.g.cs`.

```csharp
// read every component of the entity in one call
var refs = Ant.ReadAll(tx, id);
float x = refs.Position.X;
int hp  = refs.Health.Current;

// mutate in place
var mut = Ant.ReadWriteAll(tx, id);
mut.Position.X = 999;
mut.Health.Current = 50;
```

Inheritance flows through: `FlyingAnt.ReadAll(tx, id)` exposes the parent's `Position` / `Velocity` *and* `FlyingAnt`'s own `Wings`. The generated structs are `ref struct`s — same stack-only, no-copy rules as `EntityRef`. The generator fires only when the class is `partial`; a non-`partial` archetype compiles fine but gets no `ReadAll` / `ReadWriteAll`.

(Two sibling generators live alongside it: `SourceLocationGenerator` and `TraceEventGenerator`, both for the profiler — see [12-observability](12-observability.md).)

---

## 6. `EntityRef` — the working handle

[`Ecs/public/EntityRef.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EntityRef.cs)

A `ref struct` returned by `Open` / `OpenMut`. Carries the entity's location data inline (a `fixed int[16]` array of chunk IDs, one per component slot) plus optional cluster-storage fields.

```csharp
EntityRef ant = accessor.Open(id);
ref readonly var pos = ref ant.Read(Ant.Position);  // zero-copy
ant.Write(Ant.Position, new Position(x, y));        // requires writable EntityRef
```

- `Read<T>(Comp<T>)` returns a `ref readonly T` directly into the chunk page (or cluster slot). Zero copy.
- `Write<T>(Comp<T>, T)` mutates in place; for Versioned components, this is the *initial write before commit* — the actual revision-chain extension happens during commit.
- `IsValid`, `IsWritable` properties. `IsWritable` reflects whether the `EntityRef` was obtained via `OpenMut` (true) vs `Open` (false).
- `EnabledBits` tracks per-component enable state (16-bit mask, up to 16 components per archetype). A disabled component is present in storage but logically absent from queries — used for fast component toggles without re-archetyping.

---

## 7. Cluster storage

For high-density archetypes (think hundreds of thousands of Ants), Typhon supports **cluster storage**: groups of 8–64 entities share a single physical chunk, components stored Structure-of-Arrays *within* the cluster. This dramatically improves cache density for full-scan systems.

### `ClusterRef<TArch>`

[`Ecs/public/ClusterRef.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/ClusterRef.cs)

```csharp
foreach (var cluster in accessor.GetClusterEnumerator<Ant>())
{
    ulong bits = cluster.OccupancyBits;
    while (bits != 0)
    {
        int idx = BitOperations.TrailingZeroCount(bits);
        bits &= bits - 1;
        ref var pos = ref cluster.Get(Ant.Position, idx);
        // mutate or read
    }
}
```

The TZCNT loop walks only live slots — branch-free, very fast. `OccupancyBits` is a 64-bit mask at offset 0 of the cluster chunk; `EnabledBits(slot)` masks for component-disabled entities; `ActiveBits(slot) = OccupancyBits & EnabledBits(slot)` is the common case.

Cluster storage is opt-in per archetype (`IsClusterEligible` on `ArchetypeMetadata`). Eligible archetypes report `ClusterRef`-accessible storage; others use legacy per-entity storage.

---

## 8. Storage modes

[`Typhon.Schema.Definition/StorageMode.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/StorageMode.cs) — set per component type via the `StorageMode` argument on its `[Component]` attribute (`[Component("name", rev, StorageMode = StorageMode.SingleVersion)]`); the default is `Versioned`. Three modes, very different contracts.

### The contract per mode

| | **Versioned** (default) | **SingleVersion** | **Transient** |
|---|---|---|---|
| Write path | Allocates a new revision chunk; stamps the writer's `UowId`; chain head extended at commit | Returns a `ref T` into the live SoA chunk page; **the store *is* the page mutation** | Same as SV — `ref T` into the live page |
| Visibility | Snapshot-isolated; readers at TSN ≤ commit don't see the write | **Immediate** — every reader (any TSN, any PTA worker) sees the new bytes the moment the store retires | Same as SV — immediate |
| Conflict detection | Yes (write-write at commit, see [08-transactions](08-transactions.md)) | None — last writer wins, silently | None |
| Rollback (`tx.Rollback`) | Voids the new revision element; chain head untouched | **Cannot be reverted** — no before-image is captured anywhere¹ | Cannot be reverted |
| Durability | WAL on commit (per `DurabilityMode`), then checkpointed | **Tick-fence WAL** — dirtied chunks are persisted at the next tick boundary, regardless of whether the transaction committed | **Never persisted** — lost on shutdown / crash |
| Crash recovery | Re-applied from WAL records | Replayed from tick-fence WAL records | None — starts zero on every open |
| Cost | Highest (chain walk on read, allocation on write, cleanup) | Low (one indirect store) | Lowest (one indirect store, no I/O ever) |
| Storage | `ComponentSegment` + `CompRevTableSegment` | `ComponentSegment` (no rev table) | `TransientComponentSegment` (separate, in-memory only) |

¹ The lone exception: if you `Spawn<T>` an entity whose component set includes SV fields, and then `Rollback`, the freshly-allocated SV chunks are freed and the entity is never published to the `EntityMap`, so its SV bytes effectively vanish with it. *Mutating an existing entity's SV slot* is the unrollbackable case.

### `DurabilityDiscipline` — a second, orthogonal axis on SingleVersion

The table above describes `SingleVersion` under its default discipline, [`DurabilityDiscipline.TickFence`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/DurabilityDiscipline.cs). A second discipline, `Commit`, is selected **per transaction** and is **not a fourth `StorageMode`** — it shares the identical SV cluster layout and only changes the rows below for the writes it covers:

| Aspect | `TickFence` (default) | `Commit` |
|---|---|---|
| Visibility to other transactions | immediate — dirty read, before your tx commits | only at `Transaction.Commit` — read-committed, no partial visibility |
| Rollback | cannot be reverted | **O(1)** — the staged value is discarded; HEAD was never touched |
| Durability | tick-fence WAL, batched at tick boundary (≤1-tick loss) | zero-loss, atomic — WAL'd at `Transaction.Commit` (per `DurabilityMode`), no checkpoint required |
| Conflict detection | none — last-writer-wins | none — last-writer-wins (unchanged) |
| Revision chain | none | none |

`Commit` stages writes into a per-transaction buffer (the SV slot itself is untouched until commit), then publishes them in place — read-committed isolation and O(1) rollback without paying for MVCC. It applies only to `SingleVersion` (`Versioned` is always commit-scoped; `Transient` is never durable). See [11-durability](11-durability.md) for the wire format and `claude/design/Ecs/committed-storage-mode.md` for the full spec.

### What this really means

**Versioned is the only mode inside the transaction's ACID envelope — under the default `TickFence` discipline.** SV and Transient writes leak out of the transaction in three ways:

1. **No isolation**: another transaction (or a PTA worker) reading the same SV/Transient slot during your transaction sees the post-write value immediately, not a snapshot. Multiple concurrent writers on the same SV slot race with no synchronization — "developer owns concurrency" for SV/Transient mutations.
2. **No atomicity**: if you write to several SV components and then crash or rollback, the writes that already retired stay. There is no "all or nothing".
3. **Durability follows the tick, not the commit**: SV writes ride the tick-fence WAL pipeline (`DirtyBitmap` → tick-boundary persistence). Whether your transaction commits or rolls back doesn't change what's persisted.

A transaction around SV writes still gives you *thread affinity* and a *consistent read snapshot for any Versioned components in the same archetype*, but it does **not** give you the right to undo an SV mutation. If that matters, use Versioned — or escalate the SV component to `DurabilityDiscipline.Commit`, above, if you need atomicity and rollback but not snapshot isolation or temporal queries.

### Picking a mode

- **Versioned** — anything that needs ACID. Account balances, inventory, persistent game state, anything where "did this commit?" is a question you care about answering.
- **SingleVersion** — hot in-place fields whose history doesn't matter and where last-writer-wins is acceptable. Examples: cached AI navigation cost, animation tween state, cached query results. SV is persisted across restarts (via tick-fence WAL), so use it when you want durability without isolation — *not* for ephemeral state.
- **Transient** — runtime-only fields that should *not* survive a restart. Per-tick scratch space, ephemeral coordination flags, dispatch-only metadata.

### Mixing modes in one archetype

A single archetype can mix modes freely — because the mode lives on each **component type** (its `[Component]` attribute), the archetype just registers them:

```csharp
[Component("Position", 1)]                                       // Versioned (default)
public struct Position { public float X, Y, Z; }

[Component("NavCost", 1, StorageMode = StorageMode.SingleVersion)]
public struct NavCost { public float Value; }

[Component("TickScratch", 1, StorageMode = StorageMode.Transient)]
public struct TickScratch { public int Flags; }

public sealed partial class Ant : Archetype<Ant>
{
    public static readonly Comp<Position>     Position    = Register<Position>();      // Versioned
    public static readonly Comp<NavCost>      NavCost     = Register<NavCost>();       // SingleVersion
    public static readonly Comp<TickScratch>  TickScratch = Register<TickScratch>();   // Transient
}
```

Each `Register<T>()` simply picks up the mode declared on `T`'s `[Component]`. Internal layout: `Position` lives in the archetype's main `ComponentSegment` with a revision chain in `CompRevTableSegment`; `NavCost` in the same `ComponentSegment` but no rev table; `TickScratch` in a separate `TransientComponentSegment` that's never persisted. `ArchetypeMetadata.VersionedSlotMask` and `.TransientSlotMask` drive the per-slot routing.

### Who can write what

| Accessor | Versioned writes | SV writes | Transient writes |
|---|---|---|---|
| `Transaction.OpenMut` | ✓ | ✓ | ✓ |
| `EntityAccessor.OpenMut` (base) | ✗ throws | ✓ | ✓ |
| `PointInTimeAccessor` workers | ✗ throws | ✓ | ✓ |
| `ArchetypeAccessor` (fast path) | follows the underlying accessor's rules |

The Versioned-write restriction on the base `EntityAccessor` and PTA is structural: Versioned writes need a `Transaction` to allocate a `UowId`, extend the revision chain, and participate in commit. PTA was designed for parallel reads at a frozen snapshot; SV/Transient writes are tolerated because they don't need any of that machinery.

---

## 9. Behind the curtain

You don't need this to *use* the ECS API, but here's what's happening underneath. Detail in [05-revision](05-revision.md) and [02-storage](02-storage.md).

- **`EntityMap`** — per-archetype, persistent hash map keyed by `EntityKey`. Stores `EntityRecord` (chunk IDs per slot + `BornTSN` / `DeadTSN` + `EnabledBits`). Implemented via [`PagedHashMap`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Collections/internals/PagedHashMap.cs) on top of `ComponentSegment`.
- **`EntityRecord`** ([file](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EntityRecord.cs)) — the per-entity row in the EntityMap. The `EnabledBits`, `BornTSN`, `DeadTSN`, and the locations of each component chunk.
- **`ArchetypeMetadata`** ([file](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/ArchetypeMetadata.cs)) — runtime descriptor: slot count, per-slot component type IDs, Versioned/Transient slot masks, cluster layout if eligible.
- **`ArchetypeRegistry`** ([file](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/ArchetypeRegistry.cs)) — global registry; assigns slot indices on first finalization, handles parent-child slot ordering.
- **`EnabledBitsOverrides`** ([file](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/EnabledBitsOverrides.cs)) — MVCC-aware override map for per-entity enable bit changes that haven't been committed to the `EntityRecord` yet. `ResolveEnabledBits(entityKey, baseEnabledBits, TSN)` gives the snapshot-correct view.
- **`EnabledBitsHistory`** ([file](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/EnabledBitsHistory.cs)) — historical record of EnabledBits changes for MVCC visibility queries.
- **`FieldShadowBuffer`** ([file](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/FieldShadowBuffer.cs)) — captures the *old index key* (8 bytes, `ChunkId / EntityPK / OldKey`) for each indexed-field mutation so the deferred B+Tree maintenance pass can locate and update the entry. **Does not buffer the new value itself** — the value mutation is direct.
- **`DirtyBitmap`** + **`DirtyBitmapRing`** ([dirty](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/DirtyBitmap.cs)) — track which entities have been mutated this tick; consumed by view-system delta computation ([09-querying](09-querying.md)).
- **`ZoneMapArray`** ([file](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/ZoneMapArray.cs)) — per-cluster min/max summaries used by query planning for skip-on-mismatch optimization.
- **`SimdPredicateEvaluator`** ([file](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/SimdPredicateEvaluator.cs)) — vectorized predicate evaluation over cluster contents.
- **`DeferredCleanupManager`** ([file](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/DeferredCleanupManager.cs)) — tail-driven garbage collection of revision chain elements that no active snapshot can see any more.

### Commit pipeline (where ECS hits transactions)

The transaction commit ([08-transactions](08-transactions.md)) calls:

1. **`PrepareEcsDestroys`** — finalize `DeadTSN` for entities being destroyed; clear `EnabledBits` overrides.
2. **`FlushEcsPendingOperations`** — walk the spawned-entity list, allocate final `EntityRecord`s, insert into the per-archetype `EntityMap`.
3. **`FinalizeSpawns`** — stamp `BornTSN = TSN` on all new entries.
4. **Cluster-versioned slot commit** — for cluster-storage archetypes, the cluster slots need their per-slot revision pointer updated; this is the equivalent of revision-chain extension for the cluster layout.

After commit, the changes are visible to any new snapshot. Active snapshots with TSN < commit-TSN still see the pre-commit state — that's MVCC working as designed.

### Epoch interaction

The ECS resolve path enters an `EpochGuard` ([01-foundation §4](01-foundation.md)) on every entity read — page accessors need this protection so the page cache can't evict a page mid-read. There's an `EpochRefreshInterval = 128` constant ([`EntityAccessor.cs:32`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/EntityAccessor.cs)) — every 128 entity ops on a `Transaction`, the engine refreshes the pinned epoch and flushes the change set to release excess dirty marks ([02-storage §5](02-storage.md)). This bounds how long a single transaction can hold pages dirty.

---

## 10. Quick usage map

```csharp
// Setup
public sealed partial class Ant : Archetype<Ant>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Health>   Health   = Register<Health>();
}

// Spawn
using var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit);
using var tx  = uow.CreateTransaction();
EntityId ant = tx.Spawn<Ant>(
    Ant.Position.Set(new Position(0, 0)),
    Ant.Health.Set(new Health(100)));
tx.Commit();
// uow flushes / fsyncs per DurabilityMode

// Read (single thread)
using var readTx = dbe.CreateTransaction(readOnly: true);
EntityRef a = readTx.Open(ant);
ref readonly var pos = ref a.Read(Ant.Position);

// Read (parallel system)
var pta = new PointInTimeAccessor();
pta.Attach(dbe, workerCount: 8);
// dispatch — each worker uses pta.GetAccessor(workerId)

// Iterate clusters (high density, full scan)
foreach (var cluster in accessor.GetClusterEnumerator<Ant>())
{
    ulong bits = cluster.OccupancyBits;
    while (bits != 0)
    {
        int idx = BitOperations.TrailingZeroCount(bits);
        bits &= bits - 1;
        ref readonly var pos = ref cluster.Get(Ant.Position, idx);
        // ...
    }
}
```

---

## See also

- [01-foundation](01-foundation.md) — `UnitOfWorkContext`, `EpochGuard`, allocators that ECS sits on
- [02-storage](02-storage.md) — `PagedMMF`, `ComponentSegment`, chunk allocation, dirty tracking
- [04-schema](04-schema.md) — `FieldType`, attribute system, schema persistence and evolution
- [05-revision](05-revision.md) — MVCC mechanics, `CompRevStorageElement`, revision chains
- [08-transactions](08-transactions.md) — `Transaction`, `UnitOfWork`, commit pipeline
- [09-querying](09-querying.md) — `EcsQuery`, `EcsView`, statistics
