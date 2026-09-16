# SWG Tatooine — a game-shaped workload for Typhon's spatial layer

A server-side simulation of *Star Wars Galaxies*' planet Tatooine, built as a load for Typhon's spatial partitioning. No
client, no rendering, no network: the planet is reconstructed into a real on-disk Typhon database and then ticked through
a real system DAG, so the engine sees the traffic a game server actually produces.

At its baseline it simulates **320 players, 10 524 creatures and 1 155 city NPCs** among 5 725 buildings, props and
lairs, at 10 Hz. Scaled up (`--pop 128`) it simulates **40 960 players and 1.35 million creatures** — 2.14 million
entities — and still ticks inside the 100 ms budget on one desktop CPU.

It exists because every other spatial workload the engine was tuned against is synthetic — uniform fill or an abstract
swarm, one archetype, one update rate, driven straight through the fence. A game server looks nothing like that. Its
buildings never move and never tick; its creatures move on an AI cadence slower than the tick; its players move every
tick and are the only things anything queries around; and exactly one of its archetypes is worth a WAL record. The point
of this demo is that mixture.

**It is not an API sample.** [`samples/Typhon.Samples.Swg`](../../samples/Typhon.Samples.Swg) is the sample, shaped to
teach the API. This project deliberately shares no code with it: it is shaped to load the spatial layer, and neither
should constrain the other.

---

## What it does today

| | |
|---|---|
| **World** | 16 384 m square, coordinates −8 192…+8 192 on X and Z, from Core3's own `coordinateMin`/`coordinateMax` |
| **Content** | 7 cities, 7 points of interest, 8 creature spawn regions, player cities in 5 size tiers, player structures |
| **Archetypes** | 5 — static world objects, creature lairs, creatures, city NPCs, players |
| **Systems** | 12 (10 with `--no-shuttles`) over 7 phases, dispatched by `TyphonRuntime` |
| **Storage** | a real database with a real WAL; per-archetype `ClusterDurability.Checkpoint`, plus one `Versioned` component |
| **Rate** | 10 Hz by default — SWG's server ran its AI and movement broadcast at roughly that |

The systems, in phase order:

| System | Phase | What it does |
|---|---|---|
| `Mission` | Spawn | Destroy-mission lifecycle: issue, camp spawn, completion |
| `Shuttle` | Spawn | Shuttleport landings, boarding windows, transport |
| `CreatureThink` | Think | Creature AI: mode transitions, wander destinations, the 24 m aggro query |
| `PlayerThink` | Think | Player activity mix and destination choice |
| `CreatureMove` | Move | Creature position integration |
| `PlayerMove` | Move | Player position integration |
| `NpcMove` | Move | City NPC drift — overwhelmingly stationary |
| `Awareness` | Awareness | The interest query: for every player, everything within 192 m, per queried archetype |
| `CreatureCombat` | Resolve | A 75 m query per creature; a creature in a player's line of fire takes damage, dies, and is revived by its lair |
| `Economy` | Economy | Harvesters and factories ticking their own counters |
| `ShuttleProbe` | Report | Times queries at shuttleports after an arrival |
| `SpatialTelemetry` | Report | Folds the previous fence's per-archetype maintenance counters |

`Awareness` is the dominant cost at every population, which is the intended shape: interest management is what a game
server spends its spatial budget on.

## How big is a "population factor"?

`--pop <x>` multiplies every **agent** population. The planet's own geography does not scale — there is one Mos Eisley
however many players walk around it — so the static prop count stays at 976 while everything player-driven grows with
the factor. These are the worlds the numbers further down were measured on:

| `--pop` | Players | Creatures | City NPCs | Lairs | Player structures | Static props | **Total entities** | World build |
|---|---|---|---|---|---|---|---|---|
| 1 | 320 | 10 524 | 1 155 | 2 149 | 2 600 | 976 | **17 724** | 0.7 s |
| 4 | 1 280 | 42 096 | 4 620 | 8 596 | 10 400 | 976 | **67 968** | 0.8 s |
| 16 | 5 120 | 168 384 | 18 480 | 34 384 | 41 600 | 976 | **268 944** | 1.2 s |
| 64 | 20 480 | 673 536 | 73 920 | 137 536 | 166 400 | 976 | **1 072 848** | 2.2 s |
| 128 | 40 960 | 1 347 072 | 147 840 | 275 072 | 332 800 | 976 | **2 144 720** | 4.0 s |

`--pop 1` is the faithful baseline the world data aims at — one planet's worth of a live galaxy. Everything above it is
volumetry rather than fidelity, and the numbers below say which is which.

## What one tick has to do

Because the planet does not grow with the population, everything gets denser, and a fixed 192 m interest radius finds
proportionally more. That is the whole difficulty of this workload:

| `--pop` | Interest queries per tick | Hits per query | Aggro queries per tick | Allocated per tick |
|---|---|---|---|---|
| 1 | 1 536 | 15.6 | 1 078 | 77 KB |
| 4 | 6 144 | 40.6 | 4 166 | 199 KB |
| 16 | 24 576 | 152.9 | 15 253 | 508 KB |
| 64 | 98 304 | 599.5 | 51 957 | 1.83 MB |
| 128 | 196 608 | 1 177.4 | 95 419 | 3.39 MB |

At `--pop 64` that is **59 million entity hits a tick**, 590 million a second. GC stays out of the way throughout: 6
gen0, 3 gen1 and 1 gen2 collection over a 24 s run, 11.3 ms of pause in total — 0.05 % of the run.

## Where the world comes from

Every constant in [`World/TatooineData.cs`](World/TatooineData.cs) carries its provenance:

- **`[CORE3]`** — read out of the open-source SWGEmu Core3 server: the planet's extent, Mos Eisley at (3460, −4768) with
  a 456 m radius, Bestine 336 m, Anchorhead 125 m, aggro 24 m, chase 75 m, leash 192 m, the 400–1000 ms AI interval, the
  player-city radii 150/200/300/400/450 m and their 1 000 m minimum separation.
- **`[WIKI]`** — coordinates and figures from the community wikis, where Core3 has none.
- **`[EST]`** — estimated here, and marked as such: how many buildings a city holds, how many creatures are alive at
  once, the activity mix. Nobody datamined those.

The world is built where the game would put things, and that is the whole point: cities are dense discs of buildings and
NPCs, points of interest are tight knots, and the wilderness is lairs at a few per square kilometre with their creatures
scattered inside a spawn radius. A uniform sprinkle would be a page of code, and its cluster bounds would be a
restatement of its density — which measures nothing.

## Design

**Five archetypes, each with its own component types.** There is no shared `Placement` type: `StructurePlacement`,
`LairPlacement`, `CreaturePlacement`, `NpcPlacement` and `PlayerPlacement` are distinct, because a system's input view
binds to an archetype and sharing the type would silently merge populations the fence should see separately.

**Durability is declared per archetype.** All five are `ClusterDurability.Checkpoint`: a creature's position is
regenerated by the simulation, and writing it to the WAL ten times a second buys a guarantee nobody wants. The exception
is `Player.Inventory`, which is `StorageMode.Versioned` — so the player archetype carries a checkpoint-durable cluster
half *and* a WAL-logged versioned half, which is the interesting case. The WAL is on, deliberately: turning it off makes
the unit of work sync pages itself, which measured 4× slower in an earlier demo.

**Every archetype is barrier-only** (`SetSpatialBarrierOnly`). Movers publish through `cluster.WriteSpatial`, which flags
migration and AABB growth inline, so the fence never scans a dirty bitmap to discover what moved. For the scenery that
means the largest population in the world costs the fence nothing per tick.

**Phases do not imply a barrier.** Ordering comes from declared component access; a system that queries an archetype it
does not declare runs concurrently with that archetype's writers. Every system here declares the placements its queries
read, which is why `Awareness` names creature, NPC and structure placements it never writes.

## Partitioning choices

The grid is flat and configured once, in [`Sim/TatooineSim.cs`](Sim/TatooineSim.cs):

| Setting | Value | Why |
|---|---|---|
| Cell size | `256 m × content scale`, or `--cell` | A 64 × 64 grid over the real planet. Deliberately crude — finding the optimum is what the sweep is for, and a clever default would bias it |
| `clusterTargetExtentRatio` | 0.25 | Drift gate floor, as a fraction of the cell edge |
| `clusterRepairExtentRatio` | 0.75 | Repair-nomination gate floor |
| `clusterRepairCriticalExtentRatio` | 1.0 | The repair safety valve |
| `reclusterBudgetMs` | 1.0 ms | Per-tick maintenance budget. A cliff, not a dial: 0 means unbounded relocation |
| `repairWorstClustersPerUnit` | 8 | Clusters per repair unit |
| `repairCooldownTicks` | 50 | A repaired cell sits out this many ticks before it can be repaired again — the anti-churn half |
| `queryEfficiencyTolerance` | 0.1 | How far the queries' candidates-per-hit may drift above their best before maintenance gets the whole budget — the adaptive half |
| `clusterTargetPackingSlack` | 1.5 | Multiplier on the per-cell packing bound |
| `batchSpawnSortThreshold` | 128 | Spawn count at which a transaction places its entities in per-cell Morton order |
| Cell trees | off | `CellTreePromoteThreshold` is `int.MaxValue`; `--promote` turns promotion on |

The last two rows of the maintenance block are the interesting pair. Left alone, intra-cell maintenance costs far more
than the query it buys, because a re-packed cell decays within a few ticks and is nominated again — the cost is churn,
not the packing. The cooldown stops a cell being re-packed immediately; the efficiency tolerance stops maintenance being
paid for at all while the queries are still efficient, and releases the budget when they degrade. At `--pop 64` what
survives that pacing is about 200 player and 150 creature migrations a tick, against 673 536 moving creatures.

## Measured

All numbers below: **16 km world, 10 Hz (a 100 ms budget), Release, on a Ryzen 9 7950X (16 cores / 32 threads) under
Windows, .NET 10, 32 workers, a 1 GiB page cache**, with the fixed seed the demo ships.

### Tick cost by population and cell size

Median tick, 40 warm-up + 300 measured ticks, one run per point:

| `--pop` | Entities | 128 m cells | 256 m cells | 1 024 m cells | 2 048 m cells |
|---|---|---|---|---|---|
| 1 | 17 724 | 1.01 ms | 0.80 ms | 0.68 ms | **0.65 ms** |
| 4 | 67 968 | 2.02 ms | 1.60 ms | **1.42 ms** | — |
| 16 | 268 944 | 5.49 ms | 4.45 ms | **4.01 ms** | 4.21 ms |
| 64 | 1 072 848 | 32.75 ms | 25.40 ms | **22.83 ms** | 25.45 ms |
| 128 | 2 144 720 | — | — | **76.35 ms** | 86.41 ms |

p99 over the same runs:

| `--pop` | 128 m | 256 m | 1 024 m | 2 048 m |
|---|---|---|---|---|
| 1 | 2.56 ms | 1.72 ms | 1.21 ms | 1.01 ms |
| 4 | 2.77 ms | 2.26 ms | 2.11 ms | — |
| 16 | 6.74 ms | 5.43 ms | 4.72 ms | 5.03 ms |
| 64 | 37.57 ms | 28.97 ms | 26.69 ms | 29.24 ms |
| 128 | — | — | 90.56 ms | 101.52 ms |

Reading it:

- **A million entities — 20 480 players and 673 536 creatures — tick in 23 ms**, under a quarter of the 10 Hz budget,
  with a p99 within 17 % of the median. Two million tick in 76 ms, and that is where the budget starts to bind: the p99
  is 91 ms of the 100 available.
- **1 024 m cells are the optimum from `--pop 16` up** — about 30 % better than 128 m, 10 % better than either 256 m or
  2 048 m. At `--pop 1` the curve is nearly flat and the largest cells win by a hair, because the tick is dominated by
  fixed cost there rather than by the queries. Earlier sweeps of this workload preferred 128 m at the smaller
  populations; the query path has since been rebuilt, and fewer, fatter cells now win outright.
- **Cost tracks population from `--pop 4` to `--pop 64`**: 15.8× the entities for 16.1× the tick, even though the planet
  does not grow and every cell gets denser. It turns superlinear past that — `--pop 128` carries exactly 2× the entities
  of `--pop 64` for 3.3× the tick. Below `--pop 4` it is the other way: `--pop 1` has 15× fewer entities than `--pop 16`
  and only 5.9× less tick.

### Where the time goes at `--pop 64`

One run at 1 024 m cells, 200 measured ticks — 1 072 848 entities, median tick **21.93 ms**. "Span" is wall-clock from
the system's first chunk to its last; "worker time" is the CPU its chunks consumed across the pool:

| System | Span | Share of tick | Entities walked | Worker time |
|---|---|---|---|---|
| `Awareness` | 13.59 ms | 62.0 % | 20 480 | 353.8 ms |
| `CreatureCombat` | 4.01 ms | 18.3 % | 673 536 | 86.6 ms |
| `CreatureMove` | 1.60 ms | 7.3 % | 673 536 | 42.6 ms |
| `FencePrep` | 1.49 ms | 6.8 % | — | 1.8 ms |
| `CreatureThink` | 1.35 ms | 6.1 % | 673 536 | 33.8 ms |
| `FenceAabbRefresh` | 0.79 ms | 3.6 % | — | 21.3 ms |
| `FenceFinalize` | 0.50 ms | 2.3 % | — | 0.5 ms |
| `Missions` | 0.27 ms | 1.2 % | 137 536 | 4.5 ms |
| `FenceMigrate` | 0.26 ms | 1.2 % | — | 0.4 ms |
| `Economy` | 0.16 ms | 0.7 % | 167 376 | 2.9 ms |
| `NpcMove` | 0.14 ms | 0.7 % | 73 920 | 2.5 ms |
| `PlayerMove` | 0.14 ms | 0.6 % | 20 480 | 1.2 ms |
| `PlayerThink` | 0.13 ms | 0.6 % | 20 480 | 1.2 ms |
| `Shuttle` | 0.08 ms | 0.3 % | 20 480 | 0.4 ms |

The shares sum past 100 % because systems that share no write run concurrently — the spans overlap. Adding the worker
time up gives **≈ 553 ms of CPU compressed into a 21.9 ms tick**, a 25× speed-up on 32 threads, or 79 % of perfect.

Two systems are the workload: interest management (`Awareness` walks only the 20 480 players, and spends 354 ms of CPU
doing it) and combat's per-creature radius query. The five fence phases together cost 3.1 ms of the tick, of which `FencePrep` — which cannot
parallelise for barrier-only archetypes — is half.

### What the tick fence costs

The fence is where the engine reconciles what the systems moved — the phases the per-system table above reports
alongside the application systems. Medians from the same five runs, at 1 024 m cells:

| Phase | `--pop 1` | `--pop 4` | `--pop 16` | `--pop 64` | `--pop 128` |
|---|---|---|---|---|---|
| `FencePrep` | 0.06 ms | 0.16 ms | 0.35 ms | 1.49 ms | 2.86 ms |
| `FenceAabbRefresh` | 0.04 ms | 0.06 ms | 0.19 ms | 0.79 ms | 1.56 ms |
| `FenceMigrate` | 0.03 ms | 0.03 ms | 0.09 ms | 0.26 ms | 0.35 ms |
| `FenceFinalize` | 0.03 ms | 0.06 ms | 0.14 ms | 0.50 ms | 0.93 ms |
| **Fence total** | **0.16 ms** | **0.31 ms** | **0.78 ms** | **3.04 ms** | **5.70 ms** |
| Tick | 0.71 ms | 1.40 ms | 4.01 ms | 21.93 ms | 72.74 ms |
| **Fence share of the tick** | **22.4 %** | **22.2 %** | **19.5 %** | **13.9 %** | **7.8 %** |

- **`FencePrep` is about half the fence at every population** — 49 % at `--pop 64`, 50 % at `--pop 128`. It is also the
  phase that does not slice for a barrier-only archetype: one worker walks that archetype's clusters while the rest of
  the pool waits on it.
- **The fence's share of the tick falls as the population rises**, from 22 % to 8 %, because it grows with what actually
  moved while the queries grow with density. At scale the fence is not what costs; interest management is.

**Caveats.** One run per point. The 1 024 m column was measured twice, in two separate sweeps — 0.68 / 0.72 ms at
`--pop 1`, 4.01 / 4.10 at `--pop 16`, 22.83 / 22.98 at `--pop 64` — so read anything under about 5 % as noise. This
demo's CPU time is bimodal run to run; comparing two configurations needs interleaved pairs, not single runs.

## Running it

```bash
dotnet run -c Release --project demo/SwgTatooine -- --pop 16 --cell 1024
```

Each run creates `SwgTatooine_<pid>.typhon` beside the binary — about 180 MB of it at `--pop 64` — and deletes only a
database of its own name at startup. Because the name carries the process id, finished runs leave theirs behind; delete
them when you are done sweeping. Resident memory is dominated by the page cache, 1 GiB by default (`--cache-mib`).

Useful flags:

| Flag | Default | What it does |
|---|---|---|
| `--pop <x>` | 1 | Multiplier on every agent population. Static geography is not scaled |
| `--world <km>` | 16.384 | Planet edge. Content scales with it, so the world gets sparser rather than emptier |
| `--cell <m>` | 256 × scale | Grid cell edge |
| `--hz <n>` | 10 | Tick rate, and the budget a tick is measured against |
| `--ticks <n>` / `--warm <n>` | 200 / 40 | Measured and warm-up ticks |
| `--workers <n>` | all cores | Worker threads for the DAG and the parallel fence |
| `--unpaced` | off | Run ticks back to back for count-only comparisons — not for timing |
| `--sweep` | — | Grid over `--sweep-worlds`, `--sweep-pops`, `--sweep-cells` |
| `--chunk-stats` | off | Per-chunk timing for the awareness system: how evenly the chunks shared the pool |
| `--work-probe` | off | Counts a sample of interest queries: cells walked, clusters opened, entities tested, hits, pages |
| `--awareness-api <mode>` | count | `count`, `movenext`, `fill` or `batch` — how each interest query is drained |
| `--combat-api <mode>` | movenext | `movenext` or `batch` — one query per creature, or one per creature cluster |
| `--combat-model <m>` | pull | `pull` or `push` — creatures query for players, or players query for creatures and push hit events the creature side applies. Same hits either way |
| `--combat-verify` | off | With `push`: every ready creature also runs pull's query, and the run reports how many shooter counts differ. A check, not a timing |
| `--eff-tol <r>` / `--repair-cooldown <n>` | 0.1 / 50 | The two maintenance knobs above; `0` disables either |
| `--promote <n>` / `--tightness <r>` | off / 1 | Turn per-cell R-tree promotion on |
| `--no-shuttles` | shuttles on | Drop the shuttle systems and the mass-arrival traffic they produce |
| `--tick-log <path>` | — | Every measured tick's duration, one per line |
| `--seed <n>` | fixed | Every random decision, so two runs build the same world |

A run prints the census, the grid, the tick distribution, the workload counters, the per-system table above, and a
per-archetype maintenance table — drifters nominated, relocations admitted, cells repaired, migrations — which is where
the spatial layer's own behaviour shows up.

## Known limitations

These are deliberate, and each one is a thing the workload does *not* currently exercise:

- **Death is pooled, not structural.** A killed creature goes to a dead state and is revived at its lair rather than
  being destroyed and re-spawned, which is what SWG lairs did anyway. It does mean per-tick cluster-occupancy churn is
  exercised only through the world build and through revives, which teleport.
- **Combat is creature-side — the one limitation here that is not deliberate.** A creature in a player's line of fire
  damages itself; players never take damage. The natural shape, a player-side system opening the creature through
  `ctx.Transaction` and writing its vitals, stalls the tick loop at tick 1: engine bug
  [#907](https://github.com/Log2n-io/Typhon/issues/907). It is a workaround, not a design rule — writing an entity of
  another archetype from a system is legal — and it stays until #907 is fixed. Nor is it neutral: every creature whose
  cooldown has expired issues its own 75 m query for players, where a player-side model would issue one per firing
  player, and any player in range damages every creature in range, up to four shooters each.
- **`Awareness` counts hits by default.** A real interest system builds each player's list and diffs it against last
  tick's to send enter/leave. Writing hits out costs about 2.8 ns each, which at `--pop 64`'s 59 million hits a tick
  would dominate everything measured here — so the numbers above are for a caller that counts. `--awareness-api fill`
  writes into a scratch buffer and throws it away; the engine's own `SpatialInterestSystem` produces real deltas and this
  demo does not use it yet.
- **The inventory is written at spawn and never again**, so the WAL carries no per-tick traffic from it.
