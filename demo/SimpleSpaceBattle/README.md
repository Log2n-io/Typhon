# SimpleSpaceBattle

A headless fleet-combat simulation built to be **as fast as Typhon allows**, and to show what that costs in code.

50,000 ships fight a free-for-all at 25 Hz. The whole tick runs in **~12 ms against a 40 ms budget** — down from
440 ms when it first worked, with no gameplay removed along the way.

> Full design, measurements and the reasoning behind every decision: **[DESIGN.md](DESIGN.md)**.
> Work item: [#796](https://github.com/Log2n-io/Typhon/issues/796).

---

## What it demonstrates

Three things the engine does well and that game code usually gets wrong:

1. **Cluster-native iteration** — walk SoA cluster spans, never per-entity `Open`/`Read`.
2. **Wide systems, not wide DAGs** — Amdahl counts serial fraction, not node count. The DAG here is a straight line
   of five nodes; the parallelism is *inside* each node.
3. **Spatial partitioning that earns its keep** — the index is queried every tick by every ship, not maintained and
   then ignored.

### The rule everything follows

> **Zero cross-entity writes.** Every system writes only the entity it is currently iterating. All cross-entity data
> flow is read-only, of a lane no system writes in the same phase.

That single constraint is what makes the tick parallel with no locks, no atomics and no single-threaded drain —
**including real target acquisition and aimed fire**, which is normally the part that forces serialization.

---

## Gameplay

50,000 ships in a flattened, disc-shaped volume (`1000 × 1000 × 200`), no factions. Each ship runs a four-beat loop:

| Beat | Behaviour |
|---|---|
| **Acquire** | With no current target, lock the **nearest ship within 50 units** (ties broken by lower entity id). |
| **Pursue** | Steer toward the locked target while it stays in sight, capped at 2 rad/s. |
| **Fire** | Every 8 ticks (phase-offset per ship), if the target is within **30 units**, take an aimed shot. Accuracy is certain at point blank and ~50 % at maximum range. A hit removes 25 HP of 1,000. |
| **Re-acquire** | If the target dies or leaves scan range it is dropped, and a new one is locked next tick. |

Acquisition range deliberately exceeds weapon range, so ships **close on their target before they can shoot**.

Ships never spawn after bootstrap, so the population only shrinks and the run terminates: one survivor, mutual
annihilation, or the tick cap. A typical run kills ~7,000 ships in the first 200 ticks and accelerates from there.

Output is a console line per simulated second:

```
t=    200  alive= 43,152  deaths/s= 2,980  shots/s= 121,476  hit%= 86.3  acq/s=  7,225  gather/tick=  1515  tick p50/p95/p99= 11.34/ 12.71/ 12.78 ms  over=0
```

---

### Components — the game state

The entire ship is **four components, 48 bytes**. Everything the loop above needs is here; anything that could be
derived, or that only one system cared about, was removed rather than stored.

#### `Hull` — 24 B — where the ship is

```csharp
[Component("SimpleSpaceBattle.Hull", 1, StorageMode = StorageMode.SingleVersion)]
public struct HullComponent
{
    [Field]
    [SpatialIndex(8f)]
    public AABB3F Bounds;
}
```

**Position *is* the AABB.** A ship is a point — `MinX == MaxX` on every axis — so `Bounds` doubles as the position
and as the spatially-indexed field. There is deliberately no `Position` component: a second copy would have to be
kept in sync with this one, every tick, for no gain.

The `8f` is the fat-AABB margin. Ships cruise 2 units/tick, so 8 units is ~4 ticks of movement before the index is
forced to re-insert the entry.

This is the only component read **across entities** — every neighbour lookup ultimately reads someone else's
`Bounds`.

#### `Motion` — 12 B — where it is going

```csharp
public struct MotionComponent { [Field] public float X, Y, Z; }
```

Velocity in units/second, magnitude folded in — there is no separate speed scalar to multiply by. Two systems write
it: `Resolution` bends it toward the target (pursuit), `Movement` integrates it and flips a component on a wall
bounce.

#### `Vitals` — 4 B — whether it is still alive

```csharp
public struct VitalsComponent { [Field] public uint Health; }
```

**Unsigned integer, not float, and that is load-bearing.** A ship takes damage from several attackers in the same
tick, and those attackers are found in an order that depends on how work was partitioned across workers. Integer
addition is associative; float addition is not. Storing health as a float would make the result depend on worker
count — the simulation would stop being reproducible.

1,000 HP against 25 damage per hit is 40 hits to kill.

#### `Targeting` — 8 B — who it is shooting at

```csharp
public struct TargetingComponent
{
    [Field] public long TargetRawId;      // 0 = unlocked
    public const long Unlocked = 0L;
}
```

The **raw packed `EntityId`** of the current lock, not an `EntityLink<Ship>`. That is not a stylistic choice:
`EntityId.FromRaw` is `internal`, so a spatial-query hit — which hands back a raw `long` — **cannot be turned back
into an `EntityId` through the public API**. A typed link could not be constructed from an acquisition scan at all.
Storing the same raw value the engine keeps in the cluster's id array means a hit can be compared to it directly.

There is **no lock timer**. A `ushort` beside the 8-byte id would pad the struct to 16 B and cost 14 entities per
page. It is unnecessary: `Resolution` already scans the neighbourhood, so *"my target was not among my neighbours"*
is free, and the lock is dropped by observation instead of by a countdown.

#### What is deliberately absent

No `Behavior`, `Weapon`, `Afterburner`, `Tracking`, run-membership or checkpoint components; no `TargetLock`
archetype with its own indexes. The comparable `demo/SpaceBattle` ship carries ten components at 230 B, of which one
pause-checkpoint component alone is 112 B.

Cooldown and accuracy here are **derived** rather than stored — both are pure functions of `(entity id, tick)`:

```csharp
Fires(id, tick)          = ((tick + Mix(id)) & (FireInterval - 1)) == 0
Hits(shooter, target, …) = Mix(shooter ^ target ^ tick) < accuracyThreshold(distSq)
```

`Mix` is a splitmix64 finalizer. Any ship can evaluate any other ship's behaviour with **no memory access at all**,
which is what makes the pull formulation possible (see *Typhon design → Damage is pulled, not pushed*).

---

### Systems — the game behaviour

Five systems, in a fixed phase order:

```
Acquire  →  Fire  →  Move  →  Reap
```

#### `TargetingSystem` — phase `Acquire`

| | |
|---|---|
| **In** | `Hull` — own, and neighbours' via one spatial query per unlocked ship |
| **Out** | `Targeting` (own entity only) · the target lane (own slot only) |
| **Cost** | 0.2–0.6 ms |

Walks every cluster, but **only ships whose lock is `Unlocked` run a spatial query**. In steady state that is 40–290
ships out of 50,000 per tick — 0.1–0.6 % — because pursuit keeps targets in range and a lock only breaks when the
target dies or escapes. The expensive part is therefore gated to the churn, not the population.

For those ships: search `AcquisitionRange = 50`, keep the minimum distance, break ties on the **lower raw entity
id**. The tie-break matters — enumeration order depends on how work was partitioned across workers, so a
scan-order tie-break would make the run worker-count dependent.

Then **every** ship — locked or not — republishes its target to the lane. One 8-byte store, and it removes every
staleness question from the next phase.

#### `ResolutionSystem` — phase `Fire` — the simulation

| | |
|---|---|
| **In** | `Hull` — own and neighbours' · the target lane — **neighbours'** published targets · own `Targeting` |
| **Out** | `Vitals` (own) · `Motion` (own, pursuit steering) · `Targeting` (own, lock drop) · deaths |
| **Cost** | **8–11 ms** — everything else combined is under 1 ms |

One neighbourhood scan yields **three** results, which is why this is one system and not three:

1. **Incoming damage.** For each neighbour: if it is inside weapon range, *and* its published target is me, *and* it
   fires this tick, *and* the shot connects — take 25 damage.
2. **Pursuit.** The ship's own target is usually inside that same scan, so its position comes for free; the velocity
   is bent toward it, clamped to the turn rate.
3. **Lock validity.** If the target was not in the scan, it is dead or gone — drop the lock, and `Targeting` will
   find a new one next tick.

A ship whose accumulated damage meets or exceeds its health is recorded as dead and stops there; it is destroyed at
the end of the tick, not in place.

Note what is *not* checked: whether a neighbour is still alive. `Vitals` is being written concurrently by other
workers, so reading a neighbour's health would be a race. Everyone is alive for the whole tick and the dead leave at
`Reap` — correct *and* faster. A ship that shoots a corpse simply misses, and drops the lock the same tick because
the corpse is not in its scan either.

#### `MovementSystem` — phase `Move`

| | |
|---|---|
| **In** | `Vitals` (fresh, from the previous phase) |
| **Out** | `Hull` · `Motion` |
| **Cost** | ~0.1 ms |

`p += v · dt`, then reflect off the six world walls by mirroring the coordinate back inside and negating that
velocity component. Mirroring rather than clamping keeps ships from piling up against a wall, which would wreck the
uniform density every range in the design is derived from.

`dt` is the **fixed** timestep, never the runtime's wall-clock `DeltaTime` — see *Typhon design → Determinism*.

#### `ReaperSystem` — phase `Reap`

| | |
|---|---|
| **In** | The deaths recorded during `Fire` |
| **Out** | Entity destruction · run counters · terminal state |
| **Cost** | ~0 ms on quiet ticks; O(deaths), never O(N) |

Destroys everything that died this tick in one transaction, folds the per-worker counters into the run totals, and
decides whether the battle has ended: one survivor, mutual annihilation, or the tick cap.

Destruction is deferred here rather than done in place because a cluster walk must not run concurrently with
`Destroy` + `Commit` on the same archetype (rule CLUSTERWALK-01). Deferring it is what lets the other three systems
stay lock-free.

#### `ObserverSystem` — phase `Reap`, after `Reaper`

| | |
|---|---|
| **In** | Run counters · the runtime's tick-telemetry ring |
| **Out** | Console only |
| **Cost** | Negligible — runs once per second and never touches an entity |

One line per simulated second: alive count, deaths/s, shots/s, hit rate, acquisitions/s, gathers/tick, and tick
p50/p95/p99 with an overrun count. Percentiles come from the runtime's own ring, so they report what the scheduler
measured rather than what the host could time from outside. `SSB_BREAKDOWN=1` adds the per-system split.

---

## Typhon design

How the above is actually built on the engine — the choices that produced the 440 ms → 12 ms difference. Each one is
measured; see [DESIGN.md §15](DESIGN.md) for the numbers behind them.

### Phases, and why the order is forced

The phase order is a consequence of the access matrix, not a preference:

| Lane | Acquire | Fire | Move | Reap |
|---|---|---|---|---|
| `Hull` | reads (self + neighbours) | reads (self + neighbours) | **writes** | — |
| `Targeting` | **writes** | **writes** | — | — |
| `Vitals` | — | **writes** | reads | reads → `Destroy` |
| `Motion` | — | **writes** | **writes** | — |
| target lane | **writes** (own slot) | reads (neighbours') | — | — |

1. **The lane forces Acquire before Fire** — a ship can only ask *"does E shoot me?"* once every E has published its
   choice.
2. **`Hull` forces Move last** — two phases read neighbours' `Hull`; one writes it. Merging them means a worker
   writing a 24-byte AABB while another reads it, and a torn position yields garbage combat.
3. **Reap is last** — CLUSTERWALK-01.

Within each phase every lane has exactly one writer, so there are **no intra-phase edges at all**: each parallel
system is alone in its phase and occupies every worker for its whole duration. That is the "wide systems, not wide
DAGs" point — five nodes in a line, with the width inside them.

### No entity views — `ChunkedParallel` + manual cluster partitioning

The three parallel systems are **`ChunkedParallel` `CallbackSystem`s with no `Input` view**.

A `QuerySystem` requires an `EcsView`, and the runtime refreshes every pull-mode input view at tick start at a
measured **8.3 µs per entity** — 394 ms of the original 440 ms tick, single-threaded, making the tick 83 % serial
([#797](https://github.com/Log2n-io/Typhon/issues/797)). These systems iterate clusters and never read
`ctx.Entities`, so the view was pure cost.

In its place, `ClusterWork` computes each chunk's slice of the active cluster list from `ctx.ChunkIndex` /
`ctx.ChunkCount`, with 64-bit intermediates so the ranges tile exactly.

**The cluster count is re-sampled every tick**, not cached: cell migration can allocate *new* clusters, and a stale
bound makes the parallel phases silently skip every cluster past it — those ships stop being simulated, while the
run still looks healthy.

### Cluster and cell enumeration

Iteration is cluster-native throughout:

```csharp
using ClusterEnumerator<Ship> clusters = work.Clusters();
foreach (ClusterRef<Ship> cluster in clusters)
{
    ulong bits = cluster.OccupancyBits;
    ReadOnlySpan<HullComponent> hulls = cluster.GetReadOnlySpan(Ship.Hull);
    Span<VitalsComponent>      vitals = cluster.GetSpan(Ship.Vitals);
    ReadOnlySpan<long>            ids = cluster.EntityIds;

    while (bits != 0)
    {
        int i = BitOperations.TrailingZeroCount(bits);
        bits &= bits - 1;
        …
    }
}
```

Occupancy is a 64-bit mask walked with TZCNT; component access is a `Span<T>` over the cluster's SoA array. No
`Open`, no `Read`, no per-entity accessor — that path costs ~186 ns/entity against ~2.7 ns for a cluster walk.

Component layout resolves to **N = 46, 3 clusters per 8 KB page, 138 entities/page**. The comparable SpaceBattle
ship is 230 B → N = 34, 1 cluster/page, 34 entities/page.

### One transaction per worker per tick

`ClusterSpatialQuery` requires an ambient `EpochGuard`, and `EpochGuard` is `internal` — so **a transaction is the
only way game code can obtain an epoch scope**.

Opening one per *chunk* meant ~90 transactions/tick, and dotTrace put
`CreateUnitOfWork → AccessControlSmall.EnterExclusiveAccess` at **42 % of `ResolutionSystem`** — pure contention,
30 workers claiming one exclusive lock ([#798](https://github.com/Log2n-io/Typhon/issues/798)). One transaction per
*worker*, reused by every chunk it runs across both phases, took that to 30/tick and the lock cost from 25,841 ms to
**62 ms**.

Two consequences that are accepted rather than solved:

- **Thread affinity.** Create and dispose both happen on the worker's own thread — tick T's transaction is disposed
  by the first chunk of tick T+1 on that same worker.
- **It spans the tick fence**, deferring page reclamation by one tick. Immaterial at ~3 MB live against an 88 MB
  page cache; it would matter near the memory envelope.

`MovementSystem` issues no spatial query, so it needs no epoch and takes **no transaction at all** — it uses a
shared `PointInTimeAccessor`. That distinction alone was worth 8.7 ms.

### Spatial queries: one per cell, gathered and binned

The naive form is one query per ship — 50,000/tick. Measured at ~60 ms, because the engine's narrowphase is not free
per candidate: each hit goes through a `double[6]` scratch inside an enumerator state machine.

Instead, per cell: **one** `ClusterSpatialQuery.AABB` over the cell box expanded by the scan range (~1,500
queries/tick). Hits are gathered into flat float SoA arrays and **counting-sorted into a uniform bin grid**
(`b = 25`). Each ship then sweeps only the bins its own 50-unit sphere touches; bins are x-major, so each `(z,y)`
pair is one contiguous run.

Total distance tests go *up*; wall-clock drops by an order of magnitude, because per-test cost collapses from ~30 ns
of enumerator work to ~2 ns of arithmetic on cache-resident memory.

> The engine grid is `1000 × 1000` XY at `cellSize 50` → **20 × 20 = 400 cells**. It is **2D** — cells are columns
> spanning the full Z depth, and Z is filtered at the query narrowphase. See [DESIGN.md §3.2](DESIGN.md).

### The target lane — a deliberate denormalisation

`Resolution` must read the *target of every neighbour it scans*. `ClusterSpatialQueryResult` hands back
`ClusterChunkId` and `SlotIndex` precisely so a caller can locate a hit in O(1) — but there is **no public
`GetCluster(chunkId)`**; cluster access is enumerator-only. Reading a neighbour's component through the ECS would
cost a per-candidate enumerator setup, ~3 ms/tick of pure overhead.

So `Targeting.TargetRawId` is mirrored into a flat `long[]` indexed by `ClusterChunkId × ClusterSize + SlotIndex` —
the dense, tick-stable coordinate the hit struct already carries. One L2/L3 load per candidate.

It is safe because it is **written only in `Acquire` and read only in `Fire`** — the same strict phase separation
`Hull` gets, so no concurrent reader/writer ever exists. Notably, `Resolution` dropping a lost lock writes the
*component* and deliberately **not** the lane, which is what keeps the Fire phase's read set immutable.

The component remains the source of truth; the lane is derived and rebuilt every tick.

### Writes: `GetSpan`, not `WriteSpatial`

`ClusterRef.WriteSpatial` is the canonical O(1) spatial write barrier, but it accepts `AABB2F` **only** and throws
`NotSupportedException` for `AABB3F` — so it is unavailable to every 3D archetype.

`Movement` therefore writes `Hull` through `GetSpan`, which is correct *because* `SpatialBarrierOnly` stays `false`:
that makes the tick fence rescan every active cluster and recompute AABBs from stored values.

**Enabling `SetSpatialBarrierOnly<Ship>()` would silently freeze the spatial index** — no exception, no warning, and
a simulation that still produces plausible-looking deaths against stale neighbours. A comment cannot catch that, so
there is a test (`SpatialIndexTracksMovement_GuardsAgainstSpatialBarrierOnly`) that probes every ship at its own
position after 40 ticks of movement.

### Damage is pulled, not pushed

Pushing damage to a target is a **cross-entity write**, and every way of making it safe costs something:

| Approach | Cost |
|---|---|
| Event queue drained by one system | Single-threaded drain — the classic bottleneck |
| `Interlocked.Add` into the target | Parallel, but an undeclared cross-cluster write the scheduler cannot verify |
| **Pull: the defender computes what it receives** | **Zero cross-entity writes.** No queue, no atomics, no checkerboard. |

Pull needs *"does E shoot me?"* answerable by the defender alone. Aimed fire looks like it defeats that — the
defender would need E's target choice. **It does not, because E published that choice one phase earlier.** The lane
makes `E.Target` exactly as safe to read as `E.Hull`.

The cost is that each pair is examined from both ends. It buys ~30× parallelism.

### Death collection

Deaths go to **per-worker buffers indexed by `ctx.WorkerId`**, merged by `Reaper` in ascending worker order — that
ordering is what makes destruction independent of thread scheduling.

Deliberately *not* an `EventQueue<T>`: `EventQueue.Push` is `_buffer[_count++]`, a plain non-atomic increment.
AntHill pushes to one from a parallel system and survives because a dropped stats event is invisible; a dropped
death event here would mean a ship never dies.

### Durability

```csharp
[Archetype(SimpleSpaceBattleSchemaIds.Ship, ClusterDurability = ClusterDurability.Checkpoint)]
public sealed partial class Ship : Archetype<Ship> { … }
```

`ClusterDurability.Checkpoint` stops the per-tick fence WAL emission. Every field here is regenerable simulation
state the sim rewrites 25×/second, so a crash losing up to one checkpoint interval of *freshness* is the right
trade. Ship **existence** is unaffected — spawn and destroy are lifecycle records and stay fully durable.

Read the setting as *"thirty seconds"*, not as *"fast"*.

### Determinism

Same seed, same tick count, same outcome — on any core count. What that requires:

| Hazard | Resolution |
|---|---|
| Float summation order across workers | Damage accumulates as `uint` |
| Torn cross-entity reads | No cross-entity component reads exist |
| Concurrent read/write of the lane | Lane written only in `Acquire`, read only in `Fire` |
| Nearest-target ties | Broken by lower entity id, never scan order |
| Death ordering | Buffers merged in `WorkerId` order |
| **Variable timestep** | **`Config.DeltaTime`, never `ctx.DeltaTime`** — the runtime's is *wall-clock elapsed time between ticks*, which made the same run diverge between consecutive executions |
| Runtime randomness | None. `Mix(id, tick)` is a pure function; splitmix64 is used at bootstrap only |

> ⚠️ `Determinism_IsIndependentOfWorkerCount` currently **fails** — a real, unresolved worker-count divergence
> confined to target selection. See [DESIGN.md §15.5c](DESIGN.md). The demo must not be described as deterministic
> until it is fixed.

---

## Running it

```bash
dotnet build demo/SimpleSpaceBattle/SimpleSpaceBattle.Main/SimpleSpaceBattle.Main.csproj -c Release
cd demo/SimpleSpaceBattle/SimpleSpaceBattle.Main
dotnet run -c Release --no-build
```

Always **Release** — Debug is ~20× slower and the numbers are meaningless.

### Knobs

All are environment variables; the two most-swept are also positional (`dotnet run -c Release 50000 30`).

| Variable | Default | Meaning |
|---|---|---|
| `SSB_SHIPS` | 50000 | Fleet size |
| `SSB_TICKS` | 45000 | Tick cap (the run ends here if nobody has won) |
| `SSB_CELL` | 50 | Spatial cell size — the dominant broadphase knob |
| `SSB_WORLD_Z` | 200 | World depth |
| `SSB_ACQ` / `SSB_WEAPON` | 50 / 30 | Acquisition and weapon range |
| `SSB_WORKERS` | CPUs − 2 | Typhon worker count |
| `SSB_RESCHUNKS` | 2 | Chunk oversubscription for `Resolution` |
| `SSB_BREAKDOWN` | — | `1` prints a per-system + fence breakdown each report |
| `SSB_VIEWBENCH` | — | `1` runs the `EcsView` refresh benchmark instead of the sim (repro for #797) |

`SSB_BREAKDOWN=1` is the one to reach for first — it prints where the tick actually went, including a `RESIDUAL`
line for time belonging to no system:

```
    breakdown of tick 174 (19.46 ms):
      Targeting          0.63 ms  workers= 30
      Resolution        11.67 ms  workers= 60
      Movement           0.06 ms  workers= 30
      Reaper             0.40 ms  workers=  1
      FenceMigrate       5.57 ms  workers= 42
      RESIDUAL           0.22 ms  (view refresh + tick fence + migration)
```

### Tests

```bash
dotnet test demo/SimpleSpaceBattle/SimpleSpaceBattle.Tests/SimpleSpaceBattle.Tests.csproj -c Release
```

Seven tests pinning the load-bearing claims: cluster geometry, worker-count determinism, the firing/accuracy purity
properties, that the spatial index keeps tracking movement, and that queries never yield duplicates. One currently
fails — see *Determinism* above.

---

## Capturing a trace and viewing it in the Workbench

### 1. Turn tracing on

Tracing is gated by `typhon.telemetry.json`, which is copied next to the binary at build time. Set the profiler
gate to `true`:

```jsonc
{
  "Typhon": {
    "Profiler": {
      "Enabled": true,                                  // ← flip this
      "Trace": "simplespacebattle.typhon-trace",
      …
    }
  }
}
```

> 🔴 **The `Trace` key must live in this file — do not set `TYPHON__PROFILER__TRACE` from `Main()`.** The engine's
> `[ModuleInitializer]` snapshots `TelemetryConfig` at assembly load, *before* `Main` runs, so an environment
> variable set in code arrives too late and you get a run with no trace and no error
> ([#792](https://github.com/Log2n-io/Typhon/issues/792)).

The shipped file already enables everything worth having: Scheduler, Runtime (including `ThreadScheduling` and the
whole `WriteTickFence` subtree), ECS, Data, Memory, Storage, Durability, Query, Spatial, GC, allocations, per-tick
gauges and CPU sampling. Per-acquire/release `Concurrency` events are deliberately **off** — at 30 workers they are
a firehose that would dominate the file. Turn them on for a short run if you need lock-level detail.

**Thread context switches** (`Runtime.ThreadScheduling`) need **Administrator or membership of *Performance Log
Users*** — the pump opens the NT Kernel Logger. Without it you get one stderr line and the rest of the trace still
works.

### 2. Capture

```bash
dotnet build demo/SimpleSpaceBattle/SimpleSpaceBattle.Main/SimpleSpaceBattle.Main.csproj -c Release
cd demo/SimpleSpaceBattle/SimpleSpaceBattle.Main
SSB_SHIPS=50000 SSB_TICKS=200 dotnet run -c Release --no-build
```

**Let the run reach its terminal state.** The exporter only drains and flushes on a clean `Stop()` — killing the
process gives you a truncated file. `SSB_TICKS=200` is a good size: it spans the acquisition storm of the first
~75 ticks *and* steady state.

You get `simplespacebattle.typhon-trace` in the working directory — magic `TYTR`, format v11, ~330 KB/tick
(≈65 MB for 200 ticks). CPU samples are transcoded into the file's trailer, so it is self-contained.

### 3. Open it in the Workbench

Start the dev servers (Kestrel `:5200` + Vite `:5173`):

```bash
pwsh -NoProfile -Command './wb-dev.ps1 start'
```

Then either open <http://localhost:5173> and use **Connect → Open Trace**, pasting the absolute path to the
`.typhon-trace`, or launch straight into it:

```bash
typhon ui --trace <absolute-path-to>/simplespacebattle.typhon-trace
typhon ui --open-latest          # opens the most recent capture
```

Stop the servers with `./wb-dev.ps1 stop` (or `reset` if a port is stuck).

### 4. What to look at

| Question | Where |
|---|---|
| Where did the tick go? | Per-system spans. `Resolution` should dominate; everything else is sub-millisecond. |
| Is anything serial that shouldn't be? | Look for `workers=1` spans. `FencePrep` is the one to watch — it spikes to ~28 ms occasionally and is the p99 tail. |
| Why is one chunk slow? | `FenceMigrate` chunks can hit ~11 ms. They are *blocked*, not working — the per-chunk epilogue serialises on one exclusive lock ([#802](https://github.com/Log2n-io/Typhon/issues/802)). |
| Memory behaviour | Per-tick gauges: GC heap by generation, LOH/POH, unmanaged total/peak, fragmentation, page-cache pages, WAL buffers. |
| Off-CPU time | `ThreadContextSwitch` records carry duration **and wait reason**, so off-CPU gaps render with their cause. |

Turn tracing back **off** before benchmarking — every performance number in DESIGN.md was measured with the gate
disabled.

### Profiling with dotTrace instead

For CPU attribution rather than tick structure:

```bash
cd demo/SimpleSpaceBattle/SimpleSpaceBattle.Main
SSB_SHIPS=50000 SSB_TICKS=200 DOTNET_ROLL_FORWARD=LatestMajor dottrace start \
  --profiling-type=Sampling --save-to=profiling/run.dtp --overwrite \
  -- bin/Release/net10.0/SimpleSpaceBattle.Main.exe
```

Profile the **built exe**, not `dotnet run` (which adds MSBuild noise), and with Typhon tracing **off** — otherwise
you are measuring the tracer. Report and analyse with `Reporter.exe` and
`test/Typhon.Benchmark/profiling/analyze_profile.py`; see the `/profile` skill.
