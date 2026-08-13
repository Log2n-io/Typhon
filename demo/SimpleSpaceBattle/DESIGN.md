# SimpleSpaceBattle — Design

**Date:** 2026-08-13
**Status:** Implemented
**Location:** `demo/SimpleSpaceBattle/`
**Engine:** `src/Typhon.Engine` (cluster storage, spatial grid, DAG runtime)

---

## 1. Purpose

A headless, deterministic fleet-combat simulation whose only job is to be **as fast as Typhon allows**, and to show
what that costs in code. It is a reference for three things the engine does well and that game code usually gets
wrong:

1. **Cluster-native iteration** — walk SoA cluster spans, never per-entity `Open`/`Read`.
2. **Wide systems, not wide DAGs** — Amdahl counts serial fraction, not node count.
3. **Spatial partitioning that earns its keep** — the index is queried every tick by every ship, not maintained and
   ignored.

### Design constraint that shapes everything

> **Zero cross-entity writes.** Every system writes only the entity it is currently iterating. All cross-entity data
> flow is read-only, and every such read is of a lane that no system writes in the same phase.

That single rule is what makes the whole tick parallel with no locks, no atomics, no checkerboard and no
single-threaded drain — **including real target acquisition and aimed fire**, which is the part that usually forces
serialization. Sections 5 and 6 are the consequences.

### Non-goals

- Rendering, UI, networking. Headless only; the observable output is a console line and the database.
- Pause / resume, hand-rolled checkpoint components, crash-consistency protocol on top of the engine.
- Ship classes, factions, weapon inventories, formations, AI state machines.
- Beating a specific wall-clock number. Budget compliance at 25 Hz is the bar; headroom is the result.

---

## 2. Simulation model

50,000 ships in a flattened, disc-shaped volume, no factions — a free-for-all. Each ship runs a four-beat loop:

- **Acquire** — with no current target, scan for the **nearest other ship within 50 units** and lock onto it.
- **Pursue** — steer toward the locked target while it stays in sight.
- **Fire** — every 8 ticks (phase-offset per ship), if the target is within **30 units**, take an aimed shot.
  Accuracy falls with range: certain at point blank, ~50 % at maximum range. A hit removes 25 HP.
- **Re-acquire** — when the target dies or drifts out of weapon range it is dropped, and a new one is locked on the
  next tick.

Acquisition range (50) deliberately exceeds weapon range (30), so ships close on their target before they can shoot —
the pursuit is visible in the data, not just implied.

Ships never spawn after bootstrap, so the population decreases monotonically and the run terminates: one survivor,
mutual annihilation, or the tick cap.

| Parameter | Value | Note |
|---|---|---|
| `ShipCount` | 50 000 | configurable |
| `WorldExtent` | 1 000 × 1 000 × **200** | a disc-shaped galaxy, not a cube — §3.1 |
| `TickRate` | 25 Hz | `DeltaTime` = 0.04 s fixed |
| `TickBudget` | 40 ms | 1 / TickRate |
| `MaximumHealth` | 1 000 | 40 hits to kill |
| `AcquisitionRange` | 50 units | ~131 candidates — §3.3 |
| `WeaponRange` | 30 units | ~28 candidates — §3.3 |
| `FireIntervalTicks` | 8 | power of two; 3.125 shots/s per ship |
| `DamagePerHit` | 25 | at any range; range affects *accuracy*, not damage |
| `HitChance` | 100 % → 50 % | linear in `distSq / WeaponRange²` |
| `CruiseSpeed` | 50 units/s | 2 units per tick |
| `TurnRate` | 2 rad/s | pursuit steering clamp |
| `MaximumCompletedTicks` | 45 000 | 30 min of sim time |
| `ResolutionChunksPerWorker` | 2 | Chunk oversubscription for `Resolution` only — §15.2d |
| `Seed` | fixed | splitmix64 |

---

## 3. World and spatial partitioning

### 3.1 Grid geometry — a disc, not a cube

The world is **1 000 × 1 000 × 200**. Real galaxies are overwhelmingly planar, so a 1:5 flattening is physically
honest rather than a concession — and it happens to be the single most effective thing that can be done about §3.2,
because the engine's cells only partition XY. A 1:5 world under a 2D grid is a far smaller distortion than a 1:1 one.

```csharp
dbe.ConfigureSpatialGrid(new SpatialGridConfig(
    worldMin: Vector2.Zero,
    worldMax: new Vector2(1000f, 1000f),
    cellSize: 50f));                      // ≈ AcquisitionRange; see §3.3 and §10.4
```

| Derived | Value |
|---|---|
| `GridWidth` × `GridHeight` | 20 × 20 = **400 occupied cells** |
| `KeySpaceDim` | `NextPow2(20)` = 32 |
| `CellCount` (descriptor slots) | 32² = 1 024 |
| `CellDescriptor[]` footprint | 1 024 × 16 B = **16 KB** — L1-resident |
| Cell shape | 50 × 50 × 200 — aspect **1:4** (was 1:10) |
| Ships per cell at 50 000 | ~125 |

`cellSize` is a config knob, not a constant, precisely because §10.4 measures it.

### 3.2 🔴 The cell grid is 2D — cells are columns, not cubes

`SpatialGridConfig` takes `Vector2` bounds (`SpatialGridConfig.cs:20-26`) and Morton-XY cell keys. **Typhon has no 3D
cell grid**, by an explicit and repeated decision:

- **ADR-046 §1** — "Shared 2D Grid, Single Cell Size Across All Archetypes."
- **ADR-046 §4** — "Unified 3D Storage with 2D Sentinel Z": every AABB is 6 floats; 2D archetypes fill Z with ±∞.
- `claude/design/Spatial/SpatialTiers/01-spatial-clusters.md:475`, `02-cluster-rtree.md:515-516` — *"cells are always
  2D (XY) and 3D archetypes bucket by their XY center, ignoring Z, with Z filtering happening at the query
  narrowphase."*

So the split is **R-Tree = fully 3D, cell grid = XY projection**. A query's candidate set is therefore an XY box
extruded through the **entire Z range**, whatever `cellSize` is:

```
candidates(2D cells)  =  ρ · (cellSize + 2R)² · worldDepthZ
candidates(ideal 3D)  =  ρ · (cellSize + 2R)³
```

That ratio — `worldDepthZ / (cellSize + 2R)` — is the whole cost of the missing dimension, and it is why flattening
the world to 200 is the single most effective lever available (§3.1):

| World | `cellSize` | R | candidates (2D) | candidates (ideal 3D) | **penalty** |
|---|---|---|---|---|---|
| 1000³ | 100 | 50 | 2 000 | 169 | **11.8×** |
| **1000×1000×200** | **50** | **30** | **605** | **182** | **3.3×** |

`Tier` / `CellAmortize` / `Checkerboard` remain unable to discriminate along Z at all — but this demo uses none of
them (§3.4).

**The residual 3.3× is not worked around**; §10.4 measures it, and the number is the argument for a 3D cell grid
expressed in milliseconds.

#### Correction: the per-cell cluster R-Tree does *not* help inside a cell

An earlier draft of this document credited the per-cell cluster broadphase with admitting ~12 of ~18 clusters. That
is wrong. Clusters are per-cell, and within a cell a ship claims **whatever slot is free, not one chosen by
position** — so every cluster in a cell holds a random subset of it and their AABBs all approximate the cell extent.
The broadphase discriminates at *cell* granularity; the cluster tier buys cache locality and dispatch granularity, not
spatial selectivity.

Two consequences that drive the design: `cellSize` is the dominant tuning knob (§10.4), and the classic uniform-grid
result applies — cell size wants to be on the order of the interaction radius.

**No 3D-grid work is planned or filed** — searched `claude/design/`, `claude/research/`, `claude/adr/` and every open
and closed issue. The blocker is not Morton (a 3-way `PDEP` is the same instruction); it is that `CellDescriptor[]` is
a **dense array padded to `KeySpaceDim^D`**. At 16 B/cell, `KeySpaceDim = 256` costs 1 MB in 2D and **268 MB** in 3D,
and 32-bit Morton drops from 16 bits/axis to 10. A 3D grid needs a *sparse* cell store — a design change, not a knob.

### 3.2b 🔴 `WriteSpatial` is also 2D-only — the write barrier is unavailable

A second, independent 3D gap. `ClusterRef.WriteSpatial` is the canonical write path for a spatial component: it
updates the cluster AABB inline on grow, flags shrink axes, flags cell migration and sets the cluster's bit in
`ClusterProcessBitmap` — all O(1), no scan. It **supports `SpatialFieldType.AABB2F` and nothing else**; `AABB3F` hits
`throw new NotSupportedException` (`ClusterRef.cs:250-258`, comment: *"TODO: specialize AABB3F / BSphere2F / BSphere3F
/ double variants"*). **No 3D game can use the barrier at all.**

The fallback is the pre-barrier path and it is correct: `Movement` writes `Hull` through `GetSpan`, and the tick fence
picks it up because `RecomputeDirtyClusterAabbs` **discards its `dirtyBits` argument** (`_ = dirtyBits;`,
`ArchetypeClusterState.cs:2052`) and rescans **every active cluster** unconditionally whenever `SpatialBarrierOnly` is
false — recomputing each AABB from stored slot values and enqueueing migrations via `FlagOutliersForMigration`. This
is the "unconditional refresh" AntHill's own comments credit with closing a small-radius query footgun.

Two consequences, both accepted:

- **`SpatialBarrierOnly` must stay `false`.** Its doc is explicit: *"Setting this on an archetype whose spatial field
  is mutated via raw `GetSpan` / `OpenMut + Write` will cause those mutations to be invisible to the engine's spatial
  maintenance"* (`ArchetypeClusterState.cs:843-845`). Setting it here would silently freeze the index.
- **The fence pays a full rescan instead of O(1) updates** — ~46 000 slot reads plus AABB math per tick, ~150 µs
  serial and less on the parallel `AabbRefresh` path. Against a 40 ms budget it does not matter; in a
  positions-dominated 3D workload at 10× this scale it would.

### 3.3 Ranges are derived from density, not chosen

Flattening Z to 200 makes the world **5× denser**: ρ = 50 000 / (1000 · 1000 · 200) = **2.5 × 10⁻⁴** ships/unit³.
Ranges must come down with it or the melee becomes a mob. Holding the *engaged-neighbour counts* roughly constant is
what fixes them (ρ · 4/3 π R³):

| R | neighbours | used for |
|---|---|---|
| 300 | **147 000** | — (SpaceBattle's range; why it cannot afford a real scan) |
| **50** | **131** | `AcquisitionRange` |
| **30** | **28** | `WeaponRange` |
| 15 | 3.5 | — |

28 in weapon range is a dense melee a real neighbour scan can afford. The 50-unit acquisition scan runs only for the
fraction of ships that lost a target last tick (§10.2). The 1.67 acquisition-to-weapon ratio is preserved from the
cube configuration, so the pursuit behaviour is unchanged.

### 3.4 Cells, clusters, and what parallelism actually partitions

Three granularities, routinely conflated:

| Level | Count here | Role |
|---|---|---|
| **Cell** | 400 | Spatial bucket. Owns a per-archetype `CellSpatialIndex`. **The only level with spatial selectivity** (§3.2). |
| **Cluster** | ~1 200 | Storage + **unit of parallel dispatch**. ≤ 46 entities, SoA, one cell. |
| **Entity** | 50 000 | |

Work is partitioned **per cluster**, never per cell. The engine's own `ctx.ClusterIds` /
`StartClusterIndex` / `EndClusterIndex` slicing (`TickContext.cs:85-108`) is what a `QuerySystem` would use; these
systems compute the same partition themselves in `ClusterWork`, because taking an entity-view input to get it cost
394 ms/tick (§15.2 fix #1). At `ResolutionChunksPerWorker = 2` on 30 workers that is 60 chunks over ~1 250
clusters, ~21 per chunk — fine enough for work-stealing to smooth a slow chunk, coarse enough that dispatch
overhead disappears.

Note the division of labour: **cells give selectivity, clusters give locality and dispatch granularity.** Choosing
`cellSize` for the first and letting the engine choose `N` for the second is the whole tuning story.

`.Tier()` is deliberately **not** used: tiering trades fidelity for time against an observer, and a headless
free-for-all has none. Hooks are noted in §14.

---

## 4. Data model

### 4.1 Components — all `SingleVersion`

```csharp
[Component("SimpleSpaceBattle.Hull", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct HullComponent
{
    [Field]
    [SpatialIndex(margin: 8f)]
    public AABB3F Bounds;                  // 24 B — position IS the AABB
}

[Component("SimpleSpaceBattle.Motion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MotionComponent
{
    [Field] public float X;                // 12 B — velocity, units/second
    [Field] public float Y;
    [Field] public float Z;
}

[Component("SimpleSpaceBattle.Vitals", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct VitalsComponent
{
    [Field] public uint Health;            // 4 B — integer: damage summation is order-independent (§9)
}

[Component("SimpleSpaceBattle.Targeting", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct TargetingComponent
{
    [Field] public long TargetRawId;         // 8 B — the raw packed EntityId; 0 = unlocked
}
```

**Position is the AABB.** A ship is a point with a fat margin; `Bounds.MinX == Bounds.MaxX`. AntHill established this
(`WorldBounds.X` is a property over `Bounds.MinX`) and it removes a whole component plus the copy that syncs the two.

**No lock counter in `Targeting`.** A `ushort` beside the 8-byte link would pad the struct to 16 B and cost 14
entities per page. It is unnecessary: the lock is dropped by *observation* — `Resolution` scans the neighbourhood
anyway, so "my target was not among my neighbours" (dead, or out of weapon range) is free, and it writes `Null` to
itself. Event-driven, cheaper, and better behaviour than a fixed timer.

**No `Steer` component.** `Resolution` applies pursuit steering directly to `Motion`; `Movement` integrates it. Two
writers of `Motion` in *different phases* is legal — the same-phase W×W check does not fire across a phase boundary
(`SystemBuilder.cs:248`).

### 4.2 Archetype

```csharp
[Archetype(SimpleSpaceBattleSchemaIds.Ship, ClusterDurability = ClusterDurability.Checkpoint)]
public sealed partial class Ship : Archetype<Ship>
{
    public static readonly Comp<HullComponent>      Hull      = Register<HullComponent>();
    public static readonly Comp<MotionComponent>    Motion    = Register<MotionComponent>();
    public static readonly Comp<VitalsComponent>    Vitals    = Register<VitalsComponent>();
    public static readonly Comp<TargetingComponent> Targeting = Register<TargetingComponent>();
}
```

One archetype. No `TargetLock` archetype, no membership index, no checkpoint components — the target is a *field on
the ship*, not a separately-indexed entity, which is where SpaceBattle spends two multi-value indexes and a whole
archetype.

### 4.3 Cluster sizing — the payoff, computed

`ArchetypeClusterInfo.Compute` (`ArchetypeClusterInfo.cs:142-190`) with `componentCount = 4`:

```
fixedHeader    = 8 + 8×4                     = 40 B   (OccupancyBits + EnabledBits[4])
perEntitySize  = 8 + (24 + 12 + 4 + 8)       = 56 B   (EntityKey + components)
SelectClusterSize scans N ∈ [8..64] maximising clustersPerPage × N against
    stride = AlignStride(40 + 56N),  clustersPerPage = 8000 / stride
```

Best N in each clusters-per-page band:

| clusters/page | max N | stride (64 B-aligned) | **entities/page** |
|---|---|---|---|
| 4 | 34 | 1 984 | 136 |
| **3** | **46** | **2 624** | **138** ← selected |
| 2 | 64 | 3 648 | 128 |

**N = 46, 3 clusters/page, 138 entities per 8 KB page.** SpaceBattle's ten-component `Ship` gives
`perEntitySize = 230` → N = 34, 1 cluster/page, 34 entities/page. **4.06× more entities resident per page**, from
deleting components rather than tuning anything. `PauseShipCheckpointComponent` alone was 112 of its 218 payload
bytes.

At 50 000 ships in 400 cells (~125 ships/cell → 3 clusters/cell): **~1 200 clusters, ~400 pages, ~3.2 MB** of live
cluster state. The whole simulation is L3-resident on a modern server part.

Note the interaction with `cellSize`: clusters never span cells, so a smaller `cellSize` means more partially-filled
clusters. At `cellSize 30` (46 ships/cell, N = 46) most cells hold exactly one cluster at ~100 % occupancy — a happy
coincidence worth checking in the §10.4 sweep, since a half-empty cluster wastes both page residency and narrowphase
work.

*(The targeting component costs 24 entities/page against a hypothetical untargeted variant at 162/page. That is the
price of the feature, and it is worth it.)*

### 4.4 Durability

`ClusterDurability.Checkpoint` (implemented, gated at `DatabaseEngine.TickFence.cs:916`, covered by
`ClusterDurabilityTests`) stops the per-tick fence WAL emission for this archetype. Values reach disk through the
checkpoint — the same path cluster *structure* has always used.

The window is **`ResourceOptions.CheckpointIntervalMs`, 30 s by default**, and that is the correct trade: every field
here is regenerable simulation state the sim itself rewrites 25 times a second. Ship *existence* is unaffected — spawn
and destroy are lifecycle records and stay fully durable. Read the setting as "thirty seconds", not "fast".

> Worth knowing: the `FenceWal` default's ≤1-tick promise is **nominal, not delivered** — `RecoveryDriver` has no
> `ApplySlotToExisting`, so it discards fence records for any entity that existed at the last checkpoint, which in
> steady state is all of them (`ClusterDurability.cs:31-34`, issue #569). Choosing `Checkpoint` costs no real
> durability here, only the write amplification.

---

## 5. The tick — DAG design

### 5.1 Access analysis

Every lane, every access, per phase. `_targetLane` is the read-optimised mirror of `Targeting.Target` (§6.2).

| Lane | **Acquire** / `Targeting` | **Fire** / `Resolution` | **Move** / `Movement` | **Reap** / `Reaper` |
|---|---|---|---|---|
| `Hull` | Reads — self *+ neighbours* | Reads — self *+ neighbours* | **Writes** | — |
| `Targeting` | **Writes** — lock on | **Writes** — drop lost target | — | — |
| `Vitals` | — | **Writes** — damage taken | Reads | Reads → `Destroy` |
| `Motion` | — | **Writes** — pursuit steering | **Writes** — integrate + bounce | — |
| `_targetLane` | **Writes** — own slot | Reads — *neighbours' slots* | — | — |

The schedule falls out of that table mechanically:

1. **`_targetLane` forces `Acquire` before `Fire`.** A ship can only ask "does E shoot me?" once every E has published
   its choice. Publish phase, then read phase — the same discipline `Hull` gets.
2. **`Hull` forces `Move` last.** Two phases read neighbours' `Hull`; one writes it. Merging any of them means a
   worker writing cluster *k*'s 24-byte AABB while another reads it — a torn `AABB3F`. AntHill accepts an analogous
   race for pheromone deposit and justifies it by the decay factor; a torn position yields a garbage distance and
   therefore garbage combat, so it is **rejected here**.
3. **`Acquire < Fire < Move` is therefore forced, not chosen** — three sim phases is the minimum this data flow
   admits, and each is internally conflict-free.
4. **No intra-phase edges exist.** Every lane has exactly one writer per phase, so each parallel system is alone in
   its phase and occupies every worker for its whole duration.
5. **`Targeting` and `Motion` each have two writers in different phases** — legal, and already sequenced by the phase
   order.

### 5.2 Wide systems, not a wide DAG

The DAG is a straight line of five nodes, and that is the point. Amdahl's law counts the **serial fraction**, not the
node count. SpaceBattle has ten systems and a 1.02× parallel ceiling because nine of them are single-threaded
`CallbackSystem`s over the whole roster. Here three nodes carry ~99.9 % of the work and all three scale linearly with
cores; the serial tail is proportional to *deaths per tick*, not to N.

A wide DAG of narrow systems is the shape to avoid. Fusing work into the fewest systems the access matrix allows is
the shape to want — it also collapses redundant cluster walks, which is what AntHill's merge of 14 systems into
`AntUpdateSystem` was for.

### 5.3 A happy alignment: the spatial index is not stale

Typhon accepts a one-tick-stale spatial index between movement and the migration fence (SpatialTiers decision Q3).
This DAG dodges it entirely:

- the migration fence rebuilds cluster AABBs and cell assignment at the **end** of tick *T−1*;
- `Movement` is the only writer of `Hull`, and it is the **last** thing to touch positions in tick *T−1*;
- `Targeting` and `Resolution` run in the **first two** phases of tick *T*, before `Movement`.

So the index they query and the `Hull` values they read describe **the same instant**. Putting combat before movement
is not an aesthetic choice — it is what makes the query exact.

### 5.4 Systems

> **Implementation note (M4).** All three parallel systems are `ChunkedParallel` **`CallbackSystem`s**, not
> `QuerySystem`s, and take **no `Input` view**. A `QuerySystem` requires an `EcsView`, and the runtime refreshes
> every pull-mode input view at tick start at a measured **8.3 µs per entity** — 394 ms of the original 440 ms tick
> (§15.2 fix #1, issue #797). These systems iterate clusters and never read `ctx.Entities`, so the view was pure
> cost. `ClusterWork` computes each chunk's cluster range in its place.

#### `TargetingSystem` — `ChunkedParallel`, phase `Acquire`

```csharp
protected override void Configure(SystemBuilder b) => b
    .Name("Targeting")
    .Phase(BattlePhases.Acquire)
    .ChunkedParallel(_world.WorkerCount)
    .Reads<HullComponent>()             // self + neighbours; no same-phase writer
    .Writes<TargetingComponent>()       // SELF only
    .WritesResource("TargetLane");
```

Walks every cluster; **only ships whose `Target.IsNull` run a spatial query** (`AcquisitionRange`, nearest wins, ties
broken by lower entity key for determinism). Every ship — locked or not — republishes its slot in `_targetLane`, which
costs one 8-byte store and removes all staleness reasoning.

#### `ResolutionSystem` — `ChunkedParallel`, phase `Fire` — the bulk of the tick

```csharp
protected override void Configure(SystemBuilder b) => b
    .Name("Resolution")
    .ShouldRun(() => !_world.IsTerminal)
    .Phase(BattlePhases.Fire)
    .ChunkedParallel(_world.WorkerCount * _world.Config.ResolutionChunksPerWorker)   // ×2 — §15.2d
    .Reads<HullComponent>()             // self + neighbours
    .Writes<VitalsComponent>()          // damage taken — SELF only
    .Writes<MotionComponent>()          // pursuit steering — SELF only
    .Writes<TargetingComponent>()       // drop a lost target — SELF only
    .ReadsResource("TargetLane");
```

One cluster walk, one spatial query per **cell**, and a binned sweep per ship yielding three results at once:
incoming damage, whether the ship's own target is still in sight (and where, for pursuit), and the death flag.
Detail in §6.4–6.5.

Alone among the three, this system **oversubscribes chunks** (`ResolutionChunksPerWorker = 2`). Chunk cost is not
uniform — a chunk's work is the sum of its clusters' candidate counts — so a spare chunk to steal is worth ~10 %
(§15.2d).

#### `MovementSystem` — `ChunkedParallel`, phase `Move`

```csharp
protected override void Configure(SystemBuilder b) => b
    .Name("Movement")
    .Phase(BattlePhases.Move)
    .ChunkedParallel(_world.WorkerCount)
    .Reads<VitalsComponent>()           // written in an earlier phase — plain Reads is correct
    .Writes<HullComponent>()
    .Writes<MotionComponent>();
```

Uniquely among the three, this system issues **no spatial query**, so it needs no ambient `EpochGuard` and therefore
no `Transaction` — it uses the shared `PointInTimeAccessor` instead. That distinction is worth 8.7 ms: a per-chunk
transaction made this system cost 8.88 ms for what is 50 000 multiply-adds, because 30 workers allocating a TSN at
once serialise on the transaction chain (§15.2 fix #3).

Pure SoA arithmetic, no queries, no branches beyond the wall reflection. `p += v·dt`; on crossing a wall, mirror the
position and negate that velocity component.

`Hull` is written through `cluster.GetSpan(Ship.Hull)`, **not** `WriteSpatial` — the barrier does not accept `AABB3F`
(§3.2b). Spatial maintenance is therefore the fence's unconditional rescan, which requires `SpatialBarrierOnly` to
remain `false`. No `MarkDirty` call is needed: the spatial rescan ignores dirty bits, this archetype emits no fence
WAL (§4.4), and no system uses a change filter. Test 7 (§13) pins that values still reach disk.

#### `ReaperSystem` — `CallbackSystem`, sequential, phase `Reap`

Merges the per-worker death buffers in `WorkerId` order, opens one transaction, `Destroy`s each, commits, updates the
alive count and terminal state. Sequential by necessity: transaction affinity, and CLUSTERWALK-01 forbids a cluster
walk concurrent with `Destroy` + `Commit` on the same archetype — which is exactly why destruction is deferred out of
the parallel phases rather than done in place.

#### `ObserverSystem` — `CallbackSystem`, sequential, phase `Reap`, `TickDivisor(25)`

One console line per simulated second: tick, alive, deaths/s, shots, hits, locked fraction, tick p50/p95/p99,
overruns. Reads counters, never entities.

---

## 6. Combat: aimed fire without cross-entity writes

### 6.1 Why damage is pulled, not pushed

Pushing damage to a target is a **cross-entity write**, and every way of making it safe costs the thing being
demonstrated:

| Approach | Cost |
|---|---|
| Event queue, drained by one system | Single-threaded drain — SpaceBattle's actual bottleneck. |
| `Interlocked.Add` into the target's field | Parallel, but an undeclared cross-cluster write the scheduler cannot verify; dirty-bitmap marking races. |
| **Pull: each ship computes the damage it receives** | **Zero cross-entity writes.** No queue, no atomics, no `Checkerboard`. |

Pull requires that *"does E shoot me?"* be answerable by the defender. The naive objection is that aimed fire makes
that unanswerable — the defender would need E's target choice, which needs E's own neighbourhood.

**It is answerable if E publishes the choice one phase earlier.** That is the entire trick, and it costs one phase:

```
Acquire :  every E writes its own target                    (write-self)
Fire    :  S reads the targets of everyone near it,         (read-only, no concurrent writer)
           and takes damage from those pointing at S        (write-self)
```

`E.Target` becomes exactly as safe to read cross-entity as `E.Hull`: written in one phase, read in the next, never
both at once. Aimed fire and full parallelism are not in tension — they only look that way if acquisition and
resolution happen in the same pass.

The scan is still done from both ends of each pair, which is the real cost of pull. It buys ~32× parallelism.

### 6.2 The target lane — a read-optimised mirror

`ClusterSpatialQueryResult` carries `ClusterChunkId` and `SlotIndex` precisely so a caller can locate a hit in O(1)
— but there is **no public `GetCluster(chunkId)`** on `EntityAccessor`; cluster access is enumerator-only
(`EntityAccessor.ECS.cs:38-77`). Reading a neighbour's `Targeting` component through the ECS would therefore cost a
per-candidate enumerator setup (~50–100 ns × 26 candidates × 50 000 ships ≈ 3 ms/tick of pure overhead).

So `Targeting.Target` is mirrored into a flat side lane indexed by the dense, tick-stable coordinate the hit struct
already provides:

```csharp
long[] _targetLane;                                   // clusterCapacity × ClusterSize, ~1 MB
// Acquire  writes:  _targetLane[cluster.ChunkId * N + slot] = target.Id.EntityKey;
// Fire     reads:   _targetLane[hit.ClusterChunkId * N + hit.SlotIndex]
```

`ChunkId × N + SlotIndex` is stable for the whole tick — slot assignment changes only at spawn and destroy, both of
which happen in `Reap` or bootstrap. One L2/L3 array read per candidate, ~3 ns.

This is a deliberate denormalisation, and it is the one place the design steps outside the ECS. **The component
remains the source of truth** — persisted, queryable, visible in the Workbench; the lane is derived, in-memory and
rebuilt every tick. A `GetCluster(chunkId)` accessor on the engine would delete it entirely (§14).

### 6.3 Stateless firing cadence

A mutable cooldown would be a cross-entity read of a concurrently-written field. Deriving it removes both the
component and the hazard:

```csharp
// splitmix64 finalizer — a stable per-ship phase offset so the fleet doesn't fire in unison
static ulong Mix(long key)
{
    ulong z = (ulong)key + 0x9E3779B97F4A7C15UL;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
    return z ^ (z >> 31);
}

static bool Fires(long key, ulong tick) => ((tick + Mix(key)) & (FireIntervalTicks - 1)) == 0;

// Accuracy: certain at point blank, ~50 % at maximum range. Symmetric in the pair, so attacker
// and defender would compute the same roll — only the defender actually does.
static bool Hits(long shooter, long target, ulong tick, float distSq)
{
    ulong roll = Mix(shooter ^ target ^ (long)tick) & 1023UL;
    return roll < (ulong)(1024f - 512f * distSq / WeaponRangeSq);
}
```

Both are pure functions of `(keys, tick)` — evaluable by anyone, for anyone, with no memory access.

### 6.4 One query per cell, gathered and binned — not one per ship

The sketch in §6.5 issues one `Radius` query per ship. **That is what was built first, and it cost 60–73 ms** — the
engine's narrowphase is not free per candidate: every hit goes through `ReadAndValidateBoundsFromPtr` into a
`double[6]` scratch and back to float, inside an enumerator state machine. At ~1 125 candidates × 50 000 ships that
is 56 M trips through it.

The shipped implementation took that in two steps:

1. **One query per cluster** instead of per ship (50 000 → ~1 500), gathering hits into flat float SoA arrays
   (`NeighbourGather`). Per-test cost collapses from ~30 ns of enumerator work to ~2 ns of arithmetic on
   cache-resident memory. Total distance tests go *up*; wall-clock fell to **16–24 ms**.
2. **Binning the gathered set** into a uniform grid (`b = 25`) so each ship sweeps only the bins its own sphere
   touches — 1 687 candidates examined becomes ~488. **11.7 ms** (§15.2c).

The query box is keyed on the **cell**, not the cluster: a cell holds ~3 clusters with near-identical AABBs, so a
per-cluster gather ran the same query ~3× over. That keying is in place but currently yields nothing — the active
cluster list is not cell-ordered, so the one-entry cache misses ~100 % of the time (§15.8). It costs one
`WorldToCell` per cluster and starts paying the moment cluster ordering or a cell-iteration API exists.

The per-ship form is kept below because it is the clearer statement of *what* the system computes; the gather and
the bin sweep are optimisations of *how*, and all three are behaviourally identical.

### 6.5 `Resolution`, in full

*Illustrative form — the shipped code partitions clusters via `ClusterWork` and sweeps a binned gather (§6.4).
The behaviour is identical; this is the clearer statement of what is computed.*

```csharp
using var clusters = work.Clusters();        // ClusterWork: this chunk's cluster range

foreach (var cluster in clusters)
{
    var bits = cluster.OccupancyBits;
    if (bits == 0) continue;

    var hulls   = cluster.GetReadOnlySpan(Ship.Hull);
    var vitals  = cluster.GetSpan(Ship.Vitals);
    var motions = cluster.GetSpan(Ship.Motion);
    var targets = cluster.GetSpan(Ship.Targeting);
    var ids     = cluster.EntityIds;                        // ReadOnlySpan<long>

    while (bits != 0)
    {
        int i = BitOperations.TrailingZeroCount(bits);
        bits &= bits - 1;

        long  me = ids[i];
        long  myTarget = targets[i].Target.Id.EntityKey;    // own component — free
        float x = hulls[i].Bounds.MinX, y = hulls[i].Bounds.MinY, z = hulls[i].Bounds.MinZ;
        var   sphere = new BSphere3F { CenterX = x, CenterY = y, CenterZ = z, Radius = WeaponRange };

        uint  damage = 0;
        bool  targetSeen = false;
        float tx = 0f, ty = 0f, tz = 0f;

        // AabbClusterEnumerator is a disposable ref struct exposing GetEnumerator() — foreach handles both.
        foreach (var hit in dbe.ClusterSpatialQuery<Ship>().Radius(in sphere))
        {
            if (hit.EntityId == me) continue;

            // (a) incoming fire — one flat array read, no ECS lookup (§6.2)
            long theirTarget = _targetLane[hit.ClusterChunkId * ClusterSize + hit.SlotIndex];
            if (theirTarget == me &&
                Fires(hit.EntityId, ctx.TickNumber) &&
                Hits(hit.EntityId, me, ctx.TickNumber, hit.DistanceSq))
            {
                damage += DamagePerHit;                     // uint — order-independent (§9)
            }

            // (b) pursuit — my own target happens to be in this same scan
            if (hit.EntityId == myTarget)
            {
                targetSeen = true;
                tx = hit.MinX; ty = hit.MinY; tz = hit.MinZ;
            }
        }

        vitals[i].Health = damage >= vitals[i].Health ? 0u : vitals[i].Health - damage;

        if (vitals[i].Health == 0u)      _deaths[ctx.WorkerId].Add(me);
        else if (targetSeen)             SteerToward(ref motions[i], x, y, z, tx, ty, tz);
        else                             targets[i].TargetRawId = TargetingComponent.Unlocked;   // lost it
    }
}
```

Four properties worth naming:

- **One scan, three outcomes.** Incoming damage, pursuit vector and lock validity all come from the same
  `WeaponRange` query. The spatial index sits on the critical path of everything the simulation does — which is the
  whole argument for having one.
- **The cross-entity data flow is `ClusterSpatialQueryResult` plus one array read.** `EntityId`,
  `MinX/MinY/MinZ` and `DistanceSq` come off the hit struct, read by the narrowphase directly from cluster storage
  (`ClusterSpatialQuery.cs:185-233`). **No component-table reads at all**, hence no opportunity for a torn read.
  AntHill measured this switch at ~1.1 µs/hit saved over `WhereNearby` + per-hit `TryOpen`.
- **Neighbour liveness is deliberately not tested.** `Vitals` is being written concurrently by other workers, so
  reading a neighbour's health would race. Everyone is alive for the whole tick; the dead leave at `Reap`. Correct
  *and* faster. A ship that shoots a corpse simply misses — and drops the lock at the end of this same tick, because
  the corpse is not in its scan either.
- **Losing a lock is a self-write.** `targets[i].Target = Null` touches only the iterating entity. The *lane* is not
  updated — it stays as `Acquire` published it, keeping the phase's read set immutable (§9).

`ClusterSpatialQuery` requires an ambient `EpochGuard`; the parallel-query dispatch path already establishes one
(`TyphonRuntime.cs:1041`, `:1094`).

---

## 7. Death and the sequential tail

### 🔴 `EventQueue<T>.Push` is not thread-safe

`EventQueue<T>.Push` is `_buffer[_count++] = item; Produced++` — a plain non-atomic increment
(`EventQueue.cs:113-127`). AntHill pushes to it from inside the parallel `AntUpdateSystem`, which races; it is
survivable there because a dropped `AntDiedEvent` only perturbs a stats readout. **A dropped death event here means a
ship never dies**, so the queue is not used for this.

Instead, **per-worker death buffers indexed by `ctx.WorkerId`**, sized to `WorkerCount`:

```csharp
private readonly List<long>[] _deaths;   // one per worker, allocated at schedule build
```

`ctx.WorkerId` is set on the parallel-query path; `ctx.ChunkIndex` is **not** (it stays 0 — only
`ExecuteChunkedCallback` populates it, `TyphonRuntime.cs:1504`). Worker-indexed is also what the engine does for its
own per-worker pools, and for the same reason: `ChunksPerWorker > 1` lets `chunkIndex` exceed `WorkerCount`
(`TyphonRuntime.cs:1580-1582`).

`Reaper` merges the buffers **in ascending `WorkerId` order**, giving a destruction order independent of thread
scheduling. Cost is O(deaths), not O(N) — no scan for corpses.

---

## 8. Persistence

A real on-disk Typhon database: WAL, commits, checkpoints, recovery. What is dropped is the hand-rolled protocol
*layered on top* of it.

| SpaceBattle | Here |
|---|---|
| 3 × `PauseCheckpoint` components (112 B/ship) | — engine checkpoints |
| Pause/resume + `SpaceBattleRecoveryValidation` | — |
| `ShipRunMembership` transient + multi-index | — one archetype, no membership |
| `TargetLock` archetype + 2 multi-value indexes | — an 8-byte `EntityLink` field on the ship |

Restart is a fresh run. Demonstrating that the engine persists is the engine's job; re-implementing it in demo code
demonstrates nothing and costs half the per-entity footprint.

---

## 9. Determinism

Same seed, same tick count, same outcome — on any core count.

| Hazard | Resolution |
|---|---|
| Float summation order across workers | Damage accumulates as `uint`. Integer addition is associative. |
| Torn cross-entity reads | No cross-entity component reads exist (§6.4). |
| Concurrent read/write of the target lane | Lane is written only in `Acquire`, read only in `Fire`. Dropping a lock writes the component, never the lane. |
| Nearest-target ties | Broken by lower `EntityKey`, never by scan order. |
| Death ordering | Buffers merged in `WorkerId` order (§7). |
| Variable timestep | `Config.DeltaTime` (0.04 s), **never `ctx.DeltaTime`** — the runtime's is wall-clock elapsed time between ticks (`TyphonRuntime.cs:1857`). This requirement was written here first and violated in the code; see §15.2b. |
| RNG | splitmix64, seeded once, bootstrap only. Runtime "randomness" is `Mix()`, a pure function. |
| Slot reuse changing cluster packing | Affects layout, not semantics: every rule is a function of positions and keys, never of slot index. |

A regression test asserts bit-identical `(tick, aliveCount, healthChecksum, targetChecksum)` across
`WorkerCount ∈ {1, 4, 16}`.

> 🔴 **That test currently fails.** Run-to-run determinism holds and alive counts match, but the *target* checksum
> diverges with worker count (~2×10⁻⁵). Verified not to be caused by the chunk oversubscription of §15.2d. Not
> root-caused — **this design must not be described as deterministic until it is** (§15.5c).

---

## 10. Expected performance

> ⚠️ **Superseded by §15.** This section is the pre-implementation prediction, kept because the *gap* between it and
> the measurements is instructive: the dominant costs turned out to be engine machinery the systems did not need
> (view refresh, transaction contention), not the simulation work modelled here. §10.4's `cellSize` model was also
> wrong in shape — see §15.4.

Estimates from the measured constants in `claude/design/Ecs/EntityClusters/11-performance-comparison.md` and the
geometry above. **They are predictions, to be replaced with measurements.**

### 10.1 `Resolution` — the bulk

```
candidates/query = ρ · (cellSize + 2R)² · worldZ                       (§3.2)
                 = 2.5e-4 · (50 + 60)² · 200                     = 605
real hits        = ρ · 4/3 π R³                                  =  28   → 4.6 % hit rate

50 000 queries × 605 narrowphase tests       = 30.3 M tests/tick
   × ~4 ns (cache-resident SoA)              = 121 ms single-threaded
   ÷ 32 workers                              = ~3.8 ms
target-lane reads  50 000 × 28 × ~3 ns ÷ 32  = ~0.13 ms
query setup        50 000 × ~200 ns ÷ 32     = ~0.3 ms
                                               ─────────
                                               ~4.2 ms
```

### 10.2 `Targeting` and the rest

Steady-state lock churn is the fraction of ships whose target died or left weapon range last tick — estimated 5–10 %.
Only those run a query, at `AcquisitionRange = 50` (candidates = 2.5e-4 · 150² · 200 = 1 125):

| System | Estimate |
|---|---|
| `Targeting` — cluster walk + lane publish, all ships | 50 000 × ~6 ns ÷ 32 ≈ **10 µs** |
| `Targeting` — acquisition queries, ~7 % of ships | 3 500 × 1 125 × 4 ns ÷ 32 ≈ **0.5 ms** |
| `Movement` | 50 000 × ~5 ns ÷ 32 ≈ **8 µs** |
| `Reaper` | ~deaths × ~1 µs — tens of µs in steady state |
| `Observer` | once per 25 ticks, negligible |
| Tick fence | **unconditional AABB rescan** — ~50 000 slot reads ≈ **150 µs** serial, less on the parallel `AabbRefresh` path (§3.2b) — plus migration drain. **No fence WAL** (§4.4). |

**Projected tick ≈ 5 ms against a 40 ms budget.** SpaceBattle measures 361.7 ms mean at 50 000 ships — roughly
**70×**, and essentially all of it comes from the structural choices (cluster iteration, parallel dispatch, pull
resolution), not from cutting features.

The **first tick is an outlier**: every ship starts unlocked, so `Targeting` runs 50 000 acquisition queries at once —
roughly 7 ms. Reported separately rather than smoothed away.

### 10.3 The cube this replaced

For reference, the same design in a true 1000³ world with `cellSize 100`, `R = 50`: 2 000 candidates/query → ~12.5 ms
for `Resolution` alone. **Flattening Z to 200 and halving `cellSize` is a 3.3× win on the dominant cost**, which is
exactly the 2D-grid penalty it removes (§3.2).

### 10.4 `cellSize` sweep — the measurement this demo exists to produce

`cellSize` is the dominant knob and the residual 3D gap is read directly off it. Predicted candidates/query at
`R = 30`, `worldZ = 200`, ρ = 2.5 × 10⁻⁴ — to be replaced by measurements in §15:

| `cellSize` | cells | ships/cell | candidates | hit rate | note |
|---|---|---|---|---|---|
| 100 | 100 | 500 | 1 280 | 2.2 % | too coarse |
| **50** | **400** | **125** | **605** | **4.6 %** | shipped default |
| 30 | 1 089 | 46 | 405 | 6.9 % | `KeySpaceDim` 64 → 4 096 slots |
| 25 | 1 600 | 31 | 361 | 7.8 % | diminishing |
| *(ideal 3D grid, c = 30)* | — | — | *182* | *15.4 %* | the 3.3× that a 3D cell grid would recover |

Floor at `cellSize → 0` is 180 candidates: the sphere's bounding box extruded through Z. The gap between that floor
and the ideal-3D figure is Z, and nothing but a 3D grid closes it.

---

## 11. Project layout

```
demo/SimpleSpaceBattle/
├── DESIGN.md
├── SimpleSpaceBattle.Main/
│   ├── SimpleSpaceBattle.Main.csproj
│   ├── Program.cs                    entry point, CLI overrides, terminal reporting
│   ├── SimulationConfig.cs           the §2 table
│   ├── Components.cs                 Hull / Motion / Vitals / Targeting
│   ├── Archetypes.cs                 Ship + schema ids
│   ├── BattlePhases.cs               Acquire → Fire → Move → Reap
│   ├── BattleHost.cs                 DB open, grid config, schedule build, bootstrap, run loop
│   ├── BattleWorld.cs                shared state: lanes, per-worker buffers, transactions, counters
│   ├── ClusterWork.cs                per-chunk cluster range + the per-worker transaction (§15.8)
│   ├── NeighbourGather.cs            per-worker SoA gather + uniform bin grid (§6.4)
│   ├── CombatRules.cs                Fires / Hits — pure functions of (ids, tick) (§6.3)
│   ├── TargetLane.cs                 the §6.2 mirror + its sizing/growth
│   ├── SplitMix64.cs                 bootstrap RNG only
│   ├── ViewRefreshBenchmark.cs       SSB_VIEWBENCH=1 — the #797 repro
│   ├── profiling/                    dotTrace pattern + generated reports (not for commit)
│   ├── Systems/
│   │   ├── TargetingSystem.cs
│   │   ├── ResolutionSystem.cs
│   │   ├── MovementSystem.cs
│   │   ├── ReaperSystem.cs
│   │   └── ObserverSystem.cs
│   └── typhon.telemetry.json         gates; `Trace` key must live HERE, not in Main() (§14)
├── README.md                         project summary, gameplay, Typhon design, trace instructions
└── SimpleSpaceBattle.Tests/
    ├── SimpleSpaceBattle.Tests.csproj
    └── SimulationTests.cs            the seven tests of §13
```

Both projects are added to `Typhon.slnx` under `/demo/`.

---

## 12. Bootstrap

50 000 ships in one transaction before the tick loop: positions uniform in `[0,1000) × [0,1000) × [0,200)` from
splitmix64, velocities uniform on the sphere at `CruiseSpeed`, health at `MaximumHealth`, `Target = Null` (everyone
acquires on tick 1). Sequential — parallel bulk loading is deferred (#236) and this is a one-time cost outside the
measured loop. Timed and reported separately so it never contaminates tick statistics.

---

## 15. Measured results

**Machine:** 32 logical processors, 30 Typhon workers. **Build:** Release. **Fleet:** 50 000 ships, world
1000×1000×200, `cellSize` 50, 25 Hz / 40 ms budget. Steady state sampled at tick 150–200.

### 15.1 The headline

| | tick p50 | vs budget |
|---|---|---|
| First working version | **440 ms** | 11× over |
| After the eight fixes below | **12 ms** | **0.30× — inside budget** |

**~37× faster.** SpaceBattle measures 361.7 ms at the same fleet size, so this is ~16× that as well — but the
comparison that matters is that *none* of the 20× came from cutting gameplay. Every fix removed work that was never
needed.

### 15.2 Where the 440 ms went, and what each fix bought

| # | Finding | Fix | Before → after |
|---|---|---|---|
| 1 | **Pull-mode view refresh is 8.3 µs/entity** — the runtime re-queries every system input view at tick start (#718). It was the whole per-tick residual and made the tick 83 % serial. The systems never read `ctx.Entities`. | Drop `EcsView` entirely: `QuerySystem` → `ChunkedParallel` `CallbackSystem`, partitioning clusters directly (`ClusterWork`). | residual **394 → 0.3 ms** |
| 2 | **One spatial query per ship** — 50 000/tick, each dragging ~1 125 candidates through the enumerator's `double[6]` narrowphase. | One query per **cluster** (~1 240/tick), gathered into flat float SoA, then a tight scalar sweep (`NeighbourGather`). | Resolution **60–73 → 16–24 ms** |
| 3 | **A transaction per chunk cost 8.9 ms** in `Movement` — a system whose work is 50 000 multiply-adds. 30 workers allocating a TSN at once contend on the transaction chain. | `Movement` issues no spatial query, so it needs no epoch scope: use the shared `PointInTimeAccessor`. | Movement **8.88 → 0.15 ms** |
| 4 | **`PointInTimeAccessor.Attach` cost ~5 ms** on the sequential Reap path, every tick. | Re-attach only when a destroy actually changed cluster structure. | Reaper **5.5 → ~0 ms on quiet ticks** |

Fixes 1 and 3 are the same lesson twice: **the expensive thing was the engine machinery the systems did not need**,
not the simulation.

### 15.2b Two correctness bugs the determinism test caught

`Determinism_IsIndependentOfWorkerCount` (§13 test 2) was written to prove the "zero cross-entity writes" rule held.
It found two bugs that had nothing to do with that rule, and both were mine:

| Bug | Symptom | Cause | Fix |
|---|---|---|---|
| **Wall-clock timestep** | The same run produced different outcomes on *consecutive runs at the same worker count* (2029 → 1997 → 1991 survivors). | `ctx.DeltaTime` is **elapsed wall-clock time between ticks** (`TyphonRuntime.cs:1857`), not the fixed timestep. §9 required a fixed step; the code used `ctx.DeltaTime` anyway. | Use `Config.DeltaTime` (`1/TickRate`). |
| **Stale cluster count** | Run-to-run stable, but 1 / 4 / 16 workers each gave a different answer. | `ActiveClusterCount` was refreshed only on ticks with destroys. But **cell migration also allocates clusters** — a ship crossing a cell boundary can create one. `ClusterWork` partitions `[0, count)`, so every cluster past the stale bound was **silently skipped: those ships stopped being simulated**. Migration volume varies with worker scheduling, which is why it surfaced as worker-count divergence rather than an obvious fault. | Refresh the count every tick; re-attach the accessor (the expensive part) only on destroys. |

The second is the more serious one and the reason this test is worth its 23 s: the simulation looked healthy — ships
moved, fired and died — while a slice of the fleet was frozen. Nothing else in the suite would have noticed.

**Diagnostic sequence**, since the path to it was not obvious: run twice at one worker count → not self-consistent →
timestep. Then still divergent across worker counts → re-run with `MaximumHealth = uint.MaxValue` so nothing dies →
still divergent, so **not** destroy ordering → damage is integer and nothing accumulates floats across candidates,
so the neighbour *sets* must differ → either duplicates or omissions → `ClusterSpatialQuery_DoesNotYieldDuplicateEntities`
ruled out duplicates → omissions → stale bound.

### 15.2c Fix #5 — binning the gather

The gather (fix #2) traded a 6.75× larger candidate set for a ~12× cheaper per-candidate cost. That trade left the
sweep examining **1 687 candidates per ship when only ~131 lie inside its 50-unit sphere — a 7.8 % hit rate.**

The cluster AABB spans essentially the whole cell (ships are assigned to clusters by slot availability, not position
— §3.2), so the gather box is the union of 41 ships' neighbourhoods rather than one ship's. Fix: counting-sort the
gathered candidates into a uniform bin grid (`b = 25`) over the gather box and have each ship sweep only the bins its
own sphere touches. Bins are x-major, so each `(z,y)` pair is one contiguous run.

| | candidates swept per ship | Resolution |
|---|---|---|
| flat sweep of the gather | 1 687 | 16–24 ms |
| **binned, `b = 25`** | **~488 predicted** | **11.7 ms** |
| *floor (box circumscribing the sphere)* | *250* | — |

**Measured 1.7×, against a predicted 3.5×.** The prediction assumed the sweep was all of Resolution; it is not. The
per-cluster spatial query (~2 ms), the binning passes themselves, and per-ship bin-window bookkeeping do not shrink,
so Amdahl applies inside the system just as it does across the tick. The remaining 1.9× to the floor is the
box-vs-sphere ratio and is not removable with axis-aligned tests.

Tick p50: **20 → 16–19 ms**. Resolution is still the largest single system, but `FenceMigrate` (0.2–5.6 ms,
engine-side) is now the same order.

### 15.2d Fix #6 — chunk oversubscription for Resolution

`ChunkedParallel(WorkerCount)` gives each worker exactly one chunk, so the phase lasts as long as the slowest one
with no spare chunk to steal. Chunk cost is not uniform — a chunk's work is the sum of its clusters' candidate
counts, and dense-region clusters gather far more than sparse ones.

Three 160-tick runs per setting, p50 at tick 150:

| chunks/worker | chunks | p50 samples (ms) | median |
|---|---|---|---|
| 1 | 30 | 19.51 / 18.25 / 19.08 | 19.08 |
| **2** | **60** | 17.98 / 16.85 / 16.33 | **16.85** |
| 4 | 120 | 18.13 / 16.79 / 16.61 | 16.79 |

**~10 %, and 2× captures all of it.** Shipped as `SimulationConfig.ResolutionChunksPerWorker = 2`
(`SSB_RESCHUNKS` to sweep). Only Resolution oversubscribes; Targeting and Movement are 0.1–0.6 ms and have no
imbalance worth smoothing.

This was flagged earlier as a suspect for the p99 tail. It is a real effect but a modest one — the tail is
`FencePrep`/`FenceMigrate`, not chunk imbalance. Worth recording as a hypothesis that survived testing only
partially: a first single-sample measurement showed 22.85 ms for 1× and would have overstated the gain at ~27 %;
three samples put it at 10 %.

### 15.3 Final per-system breakdown

Steady state, tick ≈ 22 ms:

| System | ms | workers | note |
|---|---|---|---|
| `Resolution` | **16–24** | 30 | the simulation; one gather + sweep per cluster |
| `FencePrep` | 0.8–1.2 | 1 | sequential; spikes to ~28 ms occasionally — the p99 tail |
| `FenceMigrate` | 0.2–5.6 | 24–45 | engine-side; now the same order as Resolution |
| `Targeting` | 0.2–0.5 | 30 | only ~7 % of ships re-acquire |
| `Movement` | **0.08–0.15** | 30 | |
| `Reaper` | ~0 / 5 | 1 | 5 ms only on ticks with destroys |
| `FenceAabbRefresh` | 0.07 | 25 | the unconditional rescan (§3.2b) — far cheaper than feared |
| **Residual** | **0.3–0.4** | — | was 394 ms |

Latency profile: **p50 22 ms · p95 30 ms · p99 31–44 ms**, 0–2 budget overruns per 25 ticks. The tail is
`FencePrep`, which is sequential and engine-side.

### 15.4 `cellSize` sweep — §10.4 answered

50 000 ships, tick 150, p50/p95/p99 ms:

| `cellSize` | cells | clusters | slot occupancy | predicted candidates | **measured p50** |
|---|---|---|---|---|---|
| 100 | 100 | 1 134 | 96 % | 1 280 | 25.2 |
| **50** | **400** | **1 243** | **88 %** | **605** | **23.5** |
| **30** | **1 089** | **1 599** | **68 %** | **405** | **20.7** ← best |
| 25 | 1 600 | 1 605 | 68 % | 361 | 22.5 |

**The prediction was wrong in shape.** §10.4 expected candidate count to dominate, implying a ~3.5× spread across
this range; the measured spread is **20 %**, and the curve is nearly flat with a shallow optimum at `cellSize 30`.

Why: after fix #2 the query is amortised over a cluster's 46 ships, so per-candidate enumerator cost — the thing
`cellSize` controls — stopped being the bottleneck. What remains is the inner sweep, whose size is set by the
*cluster AABB* plus scan range, not the cell. Smaller cells shrink cluster AABBs but also cut occupancy (68 % at
`cellSize 30`), which wastes both page residency and sweep work. The two effects nearly cancel.

**Consequence for §3.2:** the 3.3× "cost of the missing 3D cell grid" is a *broadphase* number, and the broadphase
is no longer the bottleneck. A 3D cell grid would still help — but this workload says it is worth ~20 %, not 3.3×.
That is a materially different argument than the one §3.2 makes, and it is the honest one.

### 15.5 Milestones

| Milestone | Status |
|---|---|
| M1 — builds, 5 systems registered, tick loop runs | ✅ 2026-08-13 |
| M2 — combat resolves, ships die, battle converges | ✅ 2026-08-13 |
| M3 — profiled at 50 000 ships, `cellSize` sweep | ✅ 2026-08-13 (§15.2–15.4) |
| M4 — optimisation pass | ✅ 2026-08-13 — 440 → 22 ms |
| M5 — tests green (6/6), two correctness bugs found and fixed | ✅ 2026-08-13 (§15.2b) |
| M6 — `/code-review ultra` | pending — user-triggered, cannot be launched from inside a session |

### 15.5b Open: reproducibility at 50 000 ships

`Determinism_IsIndependentOfWorkerCount` passes, but it runs 3 000 ships in a 250×250×60 world. At the shipped
50 000-ship scale, two runs of the same build agree **exactly** on alive count, deaths and acquisitions
(49 952 / 47 / 992 at tick 100 in both), while the cumulative *shot* counter differs by ~0.02 % (131 519 vs
131 491 shots/s).

So simulation state reproduces at scale, but something does not. Not root-caused. The likely candidates are the
per-worker counter fold (a stat, harmless) or a genuine marginal difference in candidate sets driven by
migration-order-dependent cluster membership (not harmless). **The determinism test should be extended to the
shipped configuration before this is claimed to be deterministic at scale.**

### 15.5c 🔴 Determinism regression — open

`Determinism_IsIndependentOfWorkerCount` **now fails consistently**, including its no-deaths diagnostic. The
1-worker baseline is stable run-to-run; it is the *worker-count* dependence that breaks. Health checksums match
exactly; only the **target** checksum diverges (~2x10^-5 relative).

Verified **not** caused by the chunk oversubscription of 15.2d — it fails identically at
`ResolutionChunksPerWorker = 1`. It most likely predates today's work: the 0.02 % shot-counter divergence recorded
in 15.5b was observed before binning, which means the 3 000-ship test was passing marginally rather than robustly.

**Not root-caused. The demo must not be described as deterministic until it is.** The divergence being confined to
targets points at `FindNearest` or the lock-drop path in `Resolution`, both of which depend on the neighbour set;
15.5d is the most likely mechanism.

### 15.5d Spatial queries and the migration hysteresis band

Writing the section-13 index-tracking guard surfaced this: a query whose radius is **smaller than
`MigrationHysteresisRatio x cellSize`** can miss entities near a cell boundary. Probing 2 000 ships at their own
position with a 1-unit radius found 29 (1.45 %) unreachable; at 4 units, zero.

This is engine policy, not a fault — hysteresis deliberately leaves an entity registered to the cell it has just
left until it is more than the band past the boundary, so a query tighter than the band never visits the cell
holding it. At `cellSize 25` the band is 1.25 units.

Harmless for this demo (`scanRange` 50 >> 1.25) but worth knowing for anyone doing tight proximity checks. It is also
a candidate mechanism for 15.5c: which entities sit inside the band depends on migration timing.

### 15.7 dotTrace sampling — where Resolution actually spends its time

Sampling profile, 200 ticks @ 50 000 ships, Typhon tracing off, 30 workers. Total sampled 237 139 ms across all
threads; `ResolutionSystem.Execute` inclusive is **61 499 ms (25.9 %)**.

Breakdown of that 61.5 s:

| Component | time | share of Resolution |
|---|---|---|
| **`CreateUnitOfWork` -> `AccessControlSmall.EnterExclusiveAccess`** | **25 841 ms** | **42 %** |
| `ResolutionSystem.Execute` self (the binned sweep) | 10 117 ms | 16 % |
| `AabbClusterEnumerator.MoveNext` (query narrowphase) | 5 972 ms | 10 % |
| `NeighbourGather.Fill` self (gather + counting sort) | 3 862 ms | 6 % |
| remainder (page epoch, latches, dispatch) | ~15 700 ms | 26 % |

**The dominant cost is not the simulation. It is lock contention on transaction creation.** Callers of
`EnterExclusiveAccess`, by time: `DatabaseEngine.CreateUnitOfWork` 25 841 ms, `PagedMMF.TryLatchPageExclusive`
6 495 ms, `ApplyDirtyBitDeltas` 3 009 ms.

Resolution opens **one transaction per chunk** — 60 per tick after 15.2d, plus 30 for Targeting — solely because
`ClusterSpatialQuery` requires an ambient `EpochGuard` and `EpochGuard` is `internal`, so a transaction is the only
way game code can obtain one. That is exactly issue #798, and the profile shows it is far larger than the 8.9 ms
that issue was filed on.

Three consequences:

1. **It explains 15.2d.** Going 1x -> 2x chunks improved balance but doubled transaction count; 2x -> 4x doubled it
   again and the contention cancelled the balance gain, which is why 4x measured the same as 2x.
2. **It explains the per-test cost gap.** 15.3 noted ~6-7 ns per distance test against ~2-3 ns of arithmetic. The
   sweep itself is only 16 % of the system; the rest was being attributed to it by wall-clock division.
3. **The binning ceiling is lower than 15.2c implies.** Optimising the sweep further attacks 16 % of Resolution.
   Removing the per-chunk transaction attacks 42 %.

**Sampling was sufficient; instrumented tracing was not used.** Sampling already gives exact caller attribution for
the contended lock, and the hot inner loop is a tight arithmetic sweep where per-call instrumentation would cost far
more than the work being measured and would distort the very ratio in question.

Minor caveat: setting `Profiler.Enabled = false` did not stop the CPU sampler or the ETW scheduling pump
(`EtwSchedulingPump.OnContextSwitch` appears at 253 ms, 0.1 %). Negligible here, but the parent gate does not
disable those children as the resolver semantics suggest it should.

### 15.8 Fixes #7 and #8 — per-worker transactions, and a per-cell gather that did not pay

Acting on 15.7, which put 42 % of Resolution in `CreateUnitOfWork` lock contention.

**#7 — one transaction per worker per tick, not per chunk.** A worker runs its chunks sequentially, so a single
transaction serves all of them — across both the Acquire and Fire phases. 90 transactions/tick becomes 30. Create and
dispose both happen on the worker's own thread (tick T's transaction is disposed by the first chunk of tick T+1 on
that worker), so a transaction is never touched from a foreign thread. Accepted cost: the transaction spans the tick
fence, deferring page reclamation by one tick — immaterial at ~3 MB live against an 88 MB page cache.

**#8 — gather keyed on the cell rather than the cluster.** A cell holds ~3 clusters with near-identical AABBs, so a
per-cluster gather ran the same query ~3× over. Implemented as a one-entry cache: re-gather only when the cluster's
cell differs from the previous cluster's.

**Result, three 160-tick runs, p50 at tick 150:**

| | p50 (ms) |
|---|---|
| before (per-chunk tx, per-cluster gather) | 16.85 |
| **after** | **12.00 / 11.88 / 13.00 → median 12.00** |

**1.4× on the whole tick.** Resolution 11.7 -> 8.1-10.6 ms, p95 24 -> 14, and **zero budget overruns** in steady
state.

**#8 contributed essentially nothing, and the instrumentation says so.** A `gather/tick` counter was added precisely
to check: the floor is one gather per occupied cell (~400), the ceiling one per cluster (~1 250). Measured:
**1 505-1 557 per tick** — at or above the cluster count, i.e. **a ~100 % cache miss rate**. `ActiveClusterIds` is not
cell-ordered, so consecutive clusters in a chunk almost never share a cell and the one-entry cache never hits.

The whole 1.4× therefore belongs to #7. Making #8 pay needs the chunk's clusters visited in cell order, and there is
no public way to iterate a cell's clusters or to reorder the active list — so it is blocked on the same engine
surface as everything else in 15.6. The code is kept because it is correct, costs one `WorldToCell` per cluster, and
starts paying the moment cluster ordering or a cell-iteration API exists. Its predicted value is ~12 % of Resolution
(the query narrowphase plus gather fill, 16 %, cut ~3.75×).

Worth noting how this was caught: the counter was written before the measurement, so the null result surfaced
immediately instead of being absorbed into #7's win and reported as a combined success.

### 15.9 Re-profile after #7 — and the fence lock (issue #802)

dotTrace sampling on the post-#7 build, same workload (200 ticks, 50 000 ships, 30 workers, 237 985 ms total).

**#7 did what 15.7 predicted, and slightly more.** `ResolutionSystem.Execute` inclusive **61 499 -> 21 945 ms (2.8x)**,
and the transaction contention is simply gone:

| Caller of `EnterExclusiveAccess` | before #7 | after #7 |
|---|---|---|
| `DatabaseEngine.CreateUnitOfWork` | 25 841 ms | **62 ms** (417x less) |
| `PagedMMF.TryLatchPageExclusive` | 6 495 ms | 9.5 ms |
| **`ArchetypeClusterState.ApplyDirtyBitDeltas`** | 3 009 ms | **3 851 ms — now the largest** |

Resolution is now ~81 % real work: sweep 8 184 ms (37 %), `NeighbourGather.FillBox` 5 040 ms (23 %),
`AabbClusterEnumerator.MoveNext` 4 586 ms (21 %).

**The gather is now 23 % of Resolution**, which re-prices fix #8's null result: with the per-cell cache working
(~400 gathers instead of ~1 500) the gather and query together — 44 % of the system — would fall roughly 3.75x.
That makes cell-ordered cluster iteration the single most valuable thing still available in the demo's own code.

**The fence's serialised epilogue is now the process's dominant lock** — filed as **#802**. Its cause is documented
in the engine and deliberate: `ApplyDirtyBitDeltas` holds `_finalizeLock` across its whole batch so the bit writes
can be plain rather than `Interlocked`, *"eliminating cross-worker cache-line false-sharing on adjacent chunkIds"*.
That amortises well over few fat batches. This workload is the opposite shape — **one** archetype, 42-45 Migrate
chunks, ~2 000 migrations spread thinly — so it is ~45 acquisitions/tick of small batches, and chunks queue at the
exit:

| | inclusive |
|---|---|
| `FenceMigrateExecSystem.OnAfterChunk` | 4 666 ms |
| of which lock wait | 3 851 ms (99 % of the flush) |
| `DispatchItem` + `ExecuteMigrationsSlice` (the actual migration) | 724 ms |

**The epilogue costs 5.3x the migration it follows.** That is the 11 ms single-chunk outlier visible in the
Workbench: the chunk is blocked, not working.

### 15.6 Engine issues found

Filed from this work, each with a reproduction:

| Issue | Finding |
|---|---|
| #797 | Pull-mode `EcsView` refresh is 8.3 µs/entity — 413 ms/tick at 50 k. Repro: `SSB_VIEWBENCH=1`. `Execute()` is only 1.4 ms of it; the add/remove diff is 99.6 %. |
| #798 | `CreateQuickTransaction` from N workers concurrently serialises — 25.8 s of lock time at 90 tx/tick. Worked around in 15.8 (one tx per worker per tick) which took it to 62 ms; the root ask is a public `EpochGuard`. |
| #802 | Parallel fence Migrate epilogue serialises on `_finalizeLock` (`ApplyDirtyBitDeltas`) — 5.3x the cost of the migration it protects, and now the largest lock consumer in the process. |
| — | `WriteSpatial` rejects `AABB3F` (§3.2b) — no 3D archetype can use the O(1) spatial write barrier. |
| — | No public `GetCluster(chunkId)` — forces the target-lane denormalisation (§6.2). |
| — | `EntityId.FromRaw` is `internal` — a spatial hit cannot be round-tripped into an `EntityId`, so the target is stored as a raw `long`. |
| — | `EventQueue<T>.Push` is not thread-safe under parallel dispatch (§7). |
| — | `EpochGuard` is `internal`, so game code cannot open an epoch scope; a `Transaction` is the only way, and it is far more expensive than the scope it provides. |

---

## 13. Validation

| # | Test | Asserts |
|---|---|---|
| 1 | `ClusterGeometry` | `N == 46`, 3 clusters/page, 138 entities/page — pins §4.3 against component drift |
| 2 | `Determinism_AcrossWorkerCounts` | identical `(tick, alive, healthChecksum, targetChecksum)` for `WorkerCount ∈ {1, 4, 32}` |
| 3 | `NoCrossEntityWrites` | each system's declared `Writes` set matches what it touches; §1's rule made mechanical |
| 4 | `TargetLaneMatchesComponent` | after `Acquire`, every lane entry equals its `Targeting.Target` — the denormalisation of §6.2 cannot silently drift |
| 5 | `PullEqualsPush` | over 100 ticks at 500 ships, pulled damage equals a reference push implementation shot-for-shot |
| 6 | `LockLifecycle` | a ship drops its lock the tick its target dies or leaves weapon range, and re-acquires the next tick |
| 7 | `Termination` | run reaches a terminal state within `MaximumCompletedTicks` at reduced ship counts |
| 8 | `TickBudget` | Release, p99 tick < 40 ms at 50 000 ships |
| 9 | `DurabilityWindow` | reopen after crash: ships exist; values are as of the last checkpoint, not lost |

**Implemented (7 — six green, one failing):**

| Test | Pins | State |
|---|---|---|
| `ClusterGeometry_MatchesDesign` | §4.3 — `N == 46` | ✅ |
| `Determinism_IsIndependentOfWorkerCount` | §9 | 🔴 **fails** — §15.5c |
| `Simulation_ConvergesTowardTermination` | §2 — the sim does something | ✅ |
| `FiringCadence_IsPureAndEvenlyDistributed` | §6.3 — purity + even phase spread | ✅ |
| `Accuracy_IsSymmetricAndFallsWithRange` | §6.3 — pair symmetry, the basis of pull/push equivalence | ✅ |
| `ClusterSpatialQuery_DoesNotYieldDuplicateEntities` | no double-counted attackers | ✅ |
| `SpatialIndexTracksMovement_GuardsAgainstSpatialBarrierOnly` | §3.2b — the load-bearing negative | ✅ |

The last four were written as diagnostics while hunting §15.2b and §15.5c, and kept because each pins a property
nothing else would notice breaking. The index-tracking guard in particular is the only thing standing between
`SetSpatialBarrierOnly<Ship>()` and a silently frozen spatial index.

**Not yet written:** 3 (`NoCrossEntityWrites`), 4 (`TargetLaneMatchesComponent`), 5 (`PullEqualsPush`),
8 (`TickBudget`), 9 (`DurabilityWindow`). Test 5 remains the most valuable of these — it is the one that would prove
the pull formulation is a *reformulation* of aimed fire and not a different game.

Test 2 earned the whole suite: it found both bugs in §15.2b, including one where part of the fleet had silently
stopped being simulated.

---

## 14. Known characteristics and extension points

| # | Item |
|---|---|
| 1 | **Z is unpartitioned** (§3.2). Deliberate and measured, not worked around. |
| 1b | **`WriteSpatial` rejects `AABB3F`** (§3.2b) — the O(1) spatial write barrier is unavailable to every 3D archetype; the fence full-rescan carries it instead. With #1, the two gaps a 3D game hits on day one. |
| 2 | **No public `GetCluster(chunkId)`** — cluster access is enumerator-only (`EntityAccessor.ECS.cs:38-77`), which is the sole reason the target lane exists (§6.2). An O(1) chunk accessor would let `Resolution` read `Targeting` straight from the neighbour's cluster and delete the mirror. |
| 3 | **`EventQueue<T>.Push` races under parallel dispatch** (§7). Avoided here; AntHill is exposed to it. |
| 4 | **`ctx.ChunkIndex` is 0 on the parallel-query path** — only `ChunkedParallel` populates it. Use `WorkerId`. |
| 5 | **Telemetry `Trace` must be set in `typhon.telemetry.json`**, not via `TYPHON__PROFILER__TRACE` inside `Main`: the engine's `[ModuleInitializer]` snapshots `TelemetryConfig` at assembly load, before `Main` runs (issue #792). |
| 6 | **`.Tier()` / `.CellAmortize()` / `.Checkerboard()` are unused** (§3.4) — no observer exists. `Checkerboard` in particular is unnecessary *by construction*: it exists to make cross-neighbour writes safe, and there are none. |
| 7 | **`FenceWal`'s ≤1-tick window is nominal** (#569) — noted in §4.4 so nobody reads `Checkpoint` here as a durability regression. |
| 8 | **`EpochGuard` is `internal`** — game code cannot open an epoch scope, so a `Transaction` is the only way to satisfy `ClusterSpatialQuery`. That single visibility decision is what forces the per-worker-transaction machinery of §15.8 and issue #798. |
| 9 | **The active cluster list is not cell-ordered**, so the per-cell gather cache misses ~100 % of the time (§15.8). Cell-ordered iteration, or any public way to enumerate a cell's clusters, would be worth ~12 % of `Resolution`. |
| 10 | **The parallel fence's Migrate epilogue serialises** on one exclusive lock (`ApplyDirtyBitDeltas`), costing 5.3× the migration it protects — issue #802. Visible in a trace as single `FenceMigrate` chunks at ~11 ms that are blocked, not working. |
| 11 | **`Profiler.Enabled = false` does not disable the CPU sampler or the ETW scheduling pump** — both still run (~0.1 % here), contrary to the resolver's parent-gate semantics. |
| 12 | **Obvious next steps if `Resolution` dominates**: precompute `1/WeaponRangeSq`; AVX2 the sweep (the gather is already SoA); or bin at a finer `b` — the floor is 250 candidates against ~488 today. |

---

## References

- `claude/design/Spatial/SpatialTiers/` — clusters, per-cell R-Trees, tier dispatch, tick integration
- `claude/design/Spatial/spatial-grid-api.md` — public spatial surface
- `claude/design/Ecs/EntityClusters/03-iteration.md`, `11-performance-comparison.md` — iteration modes and measured costs
- `claude/design/Durability/cluster-page-durability.md` — `ClusterDurability`, issue #568
- `claude/adr/044-spatial-rtree-architecture.md`, `046-spatial-tiers-architecture.md`
- `rules/ecs.md` — CLUSTERWALK-01 (§5.4), `rules/spatial.md` — SC-01, TI-01/02
- `demo/AntHill/AntHill.Core` — the reference cluster-walk system (`AntUpdateSystem`, `TyphonBridge.AntUpdateTick`)
