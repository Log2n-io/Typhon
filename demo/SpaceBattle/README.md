# SpaceBattle — a spatial-partitioning observatory

A never-ending 2D space battle on Typhon, built to make the two-level spatial partitioning (cells + clusters)
**visible and measurable**. The game is a vehicle; the instrumentation is the point.

Companion to [`claude/research/Spatial/spatial-partitioning-intent-vs-reality.md`](../../claude/research/Spatial/spatial-partitioning-intent-vs-reality.md)
and [`spatial-implementation-assessment.md`](../../claude/research/Spatial/spatial-implementation-assessment.md).

```bash
cd demo/SpaceBattle
dotnet run -c Release                       # interactive
dotnet run -c Release -- --help             # every knob, with defaults
```

Boots in **~430 ms** to a live 1 250-v-1 250 battle in a 100 km world, settling around 3 000 ships.
Simulation **2.5–4.2 ms/tick** single-threaded; render **5–6 ms/frame**.

---

## Scale: one unit is one metre

The world is **100 km × 100 km**; a ship is **10 m**. That 1:10 000 ratio is not decoration — it is what forces
every rendering decision below, because it means a ship is **0.09 pixels** with the whole world in view.

| | | |
|---|---|---|
| World | 100 000 m | 100 km square |
| Cell | 2 000 m | 51×51 grid, Morton-padded to 4 096 |
| Ship | 5 m radius | 10 m hull |
| Station | 150 m radius | 300 m structure |
| Asteroid | 400 m radius | a landmark, visible one LOD tier after ships vanish |
| Weapon range | 800 m | 80 hull-lengths |
| Sensor (acquire) | 1 600 m | the radius of the acquisition query |
| Ship speed | 450 m/s | see below — crossing the map is not the relevant distance |
| Projectile | 3 000 m/s, 35 m hit radius | see gunnery, and the tunnelling note below |

Two constants stop being free parameters at this scale:

- **`SpatialMargin`.** The fat-AABB pad was 1.0 — 1.5 % of the old 66 u ship. On a 5 m ship the same value is
  **20 %**, silently inflating every entity bound and every cluster AABB. It is now 0.5.
- **`ShotSpeed` vs `ShotHitRadius`.** Hit detection is one point-vs-radius test per tick, so a projectile that
  advances further than `2 × ShotHitRadius` per tick can step straight over a target. 25 m/tick against a 40 m hit
  diameter is inside the limit; raising the speed or shrinking the radius without re-checking that inequality drops
  hits silently, and the symptom — ships that occasionally refuse to die — looks nothing like the cause.

`f32` is comfortable here: the ULP at 100 000 is 7.8 mm, three orders below a ship. It stays usable to ~1 000 km.

**Transit time is set by the layout, not by the map.** Ship speed was raised to 800 m/s when the world went to
100 km, on the reasoning that crossing it would otherwise take two minutes. That reasoning expired when the
stations were interleaved: nothing crosses the map any more, and the lattice restored almost exactly the distances
the 32 km world had.

| Distance that actually matters | Old (32 km, opposing columns) | Now (100 km, lattice) |
|---|---|---|
| Station → nearest enemy station | 28.2 km | 29–31 km |
| Station → its contested ore field | 14.1 km | ~15 km |

At 667 m/s a miner reaches ore in 22.5 s against 21.1 s before — the map got three times wider and the journeys did
not. `InitialSpread` is 0.5 so the opening formation is already spread out; without it the first minutes are an
empty screen while everyone commutes.

---

## Level of detail

Three tiers, chosen from **pixels per entity**, never from camera distance — a distance threshold is silently wrong
the moment the window is resized.

| Tier | When | Draws | Cost |
|---|---|---|---|
| **LOD 0** sprites | ≥ 3 px per entity (≲ 3 km view) | hulls, heading, shields, hit flash, selection | O(visible) |
| **LOD 1** points | down to 0.35 px per entity (≲ 26 km view) | one clamped marker per entity | O(visible) |
| **LOD 2** density | below that, or once markers saturate | a binned density field, **plus landmarks** | **O(cells)** |

**Landmarks are exempt.** Stations and asteroids are drawn at every tier, clamped to `LandmarkPixels` (12 px) so
they never disappear. Neither argument for collapsing entities reaches them:

- *Cost.* There are six stations and eight asteroids. Their count is set by the scenario, not by the population, so
  drawing them costs the same at 1 000 ships as at 100 000.
- *Honesty.* A 2 px marker for a 10 m ship overstates it 22× and there are hundreds, so the picture lies about
  density. A clamped marker on the one station in a region says *"a station is here"*, which is true.

Without them the far view is an abstract heat map with nothing to navigate by. Zoomed in they revert to their real
dimensions — the clamp only engages once true size would be a couple of pixels — and take on an extra outline so
marker mode is visibly distinct: stations get a second box, ore fields a halo ring. The same markers appear on the
minimap, drawn from the same list as the main view.

There are **two independent reasons** to stop drawing entities, and both are needed:

1. **Sub-pixel** (`LodDensityPixels`, 0.35 px). A marker clamped to 2 px while the entity is 0.09 px wide is a 22×
   exaggeration; a fleet drawn that way reads as far denser and larger than it is.
2. **Saturation** (`LodSaturationFraction`, 15 % of the viewport). Population-driven, and self-tuning: estimated
   coverage is `visible × pointPixels² / viewportPixels`, so the boundary moves on its own when the population
   changes rather than being a magic zoom number.

At 1 000 ships only the first ever fires — 1 000 two-pixel markers cover 0.3 % of a 1600×900 viewport. Encoding
only saturation would have left the density tier **unreachable at any zoom**. Both boundaries are hysteretic
(`LodHysteresis`) so parking the wheel on one does not strobe the scene, and the density field fades *in* underneath
the points before they stop being drawn, so the switch reads as the picture resolving rather than as a cut.

Press **L** to force a tier (auto → 0 → 1 → 2 → auto), or `--autoViewHeight=` to screenshot one from the command
line — a claim about pixels has to be checked at a stated zoom.

### The aggregate

`DensityField` bins the world and shades each bin by count, hue by faction mix. It has **two interchangeable
sources**, kept deliberately:

- `--densitySource=entities` — bins every live entity. O(N), knows factions. **Ground truth.**
- `--densitySource=cells` — reads the engine's own per-cell occupancy counters. O(cells): *independent of
  population*, but no faction split.

Both count all five archetypes, so their totals are directly comparable. Any disagreement is a real defect in the
engine's occupancy accounting, not a rendering artefact. Press **D** to switch. Moving the default from the first to
the second is a measurement, not a preference — which is why both exist.

### Minimap

Bottom-right, always on (**N** toggles). Justified by one number: a 3 km tactical view over a 100 km world shows
**nine millionths of the map by area** — at that ratio panning is a search, not navigation. Click or drag it to jump.

It renders the *same* `DensityField` as the LOD 2 overlay at a different transform. A minimap fed by its own
aggregation would be free to disagree with the view it is helping you navigate.

---

## Culling — the renderer as a client of the index

`--culling` (default on, **C** toggles) resolves "what can the camera see" through **Typhon's own two-level index**,
not by enumerating everything and rejecting:

1. Camera rect → cell range (**level 1**), visiting only those cells.
2. Each cell's `CellSpatialIndex` holds a compact SoA of its clusters' AABBs — a linear scan rejects the misses
   (**level 2**).
3. Survivors' chunk ids go to `GetClusterEnumerator(ids, …)`, so only those clusters are ever opened.

The counters matter as much as the culling. The HUD's `candidates` vs `passed` **is the level-2 rejection rate,
measured on the actual camera every frame** rather than on a synthetic probe — so a degenerate cluster AABB shows up
as the renderer opening clusters for entities nowhere near the screen. Switching culling off is the A/B that shows
what the index is worth.

One deliberate exception: the **diagnostics pass is never culled**. Cluster AABB geometry for the HUD and the
selectivity probe must keep describing the whole world, or those numbers quietly become "the part of the world you
happen to be looking at". It walks every cluster (O(clusters), ~1/30 of entities); only the entity pass is culled.

---

## What it measures — the headline

The engine has never reported query **selectivity**: *"what percentage of the data a query processes is actually
useful?"* — a requirement stated on 2026-03-13 and never built. This demo computes it.

`--cellSweep` boots a fresh engine per cell size, runs the battle, and reports selectivity per query radius:

```
CELL-SIZE SWEEP — world 100000m, 5000 ships, 4000 ticks per run   (the dense case)

  cell    grid   clusters  meanAABB%  |  r=20     r=50     r=100    r=200    r=400    r=800    r=1600
  ------------------------------------------------------------------------------------------------------
   1000  101x101       30      35.6%  |   3.03%    4.35%    8.73%   26.10%   52.87%   86.54%   96.81%
   2000   51x51        23      36.6%  |   1.49%    3.73%    8.47%   17.67%   33.61%   59.75%   97.47%
   4000   26x26        19       5.5%  |   4.86%   10.29%   21.71%   54.29%   86.04%   91.12%   97.99%
   8000   13x13        22      61.5%  |   0.88%    1.33%    3.32%    8.41%   16.81%   22.35%   37.66%
```

Two things fall straight out:

1. **A projectile hit test (r=20 m) runs at 0.9–4.9 % selectivity** — the broadphase makes it examine twenty to a
   hundred entities for every one it wants. At that rejection rate it is not an index, it is a formality.

2. **`meanAABB%` — mean cluster AABB as a fraction of one cell — does not improve monotonically with cell size.**
   Shrinking cells shrinks cluster AABBs *proportionally*, because membership is drawn from whatever cell the
   cluster lives in, so the ratio stays roughly where it was. **No cell size fixes selectivity.** Only changing how
   entities are assigned to clusters, or replacing the bounding box with a descriptor that can express gaps, can.

### The pathology is density-dependent — read the sparse case too

The same sweep at **1 000 ships** in the same 100 km world looks almost healthy: 100 % selectivity from r=400
upward, and `meanAABB%` down to 1.1 % at the 2 000 m cell. That is not a contradiction and not a fix.

At one ship per square kilometre a query barely overlaps anything, so there is almost nothing for the broadphase to
reject and it cannot be caught failing to. **A sparse world flatters the index.** The measurement only becomes
diagnostic once entities are packed tightly enough that clusters overlap — which is why `scenarios/stress.json`
exists and why the numbers above are quoted at 5 000 ships.

The corollary matters for the demo's own scaling work: *the renderer's* culling benefits from exactly the same
index, so its cost per frame will degrade in the same regime, at the same time, for the same reason.

The single-frame sweep is also printed by `--autoTicks`, and the interactive HUD shows it live for a probe you drag
around with the right mouse button.

---

## Controls

| | |
|---|---|
| **MMB drag** | pan |
| **Wheel** | zoom at cursor |
| **LMB** | select a ship → its Typhon internals in the HUD |
| **RMB drag** | move the query probe (live selectivity readout) |
| **Space** | pause · **.** single-step · **[ ]** slower/faster · **0** reset speed · **F** frame world |
| **1..9** | toggle cells · heat · cluster AABBs · ships · shots · target lines · selectivity · asteroids · cluster-colour mode |
| **N** | minimap · **L** force LOD tier · **C** culling · **D** density source |
| **H** | HUD · **P** probe · **M** database file map · **F12** screenshot + frame report · **Esc** quit |

## What you see

| Overlay | Meaning |
|---|---|
| Grey grid | **Level 1** — the spatial grid. `cellCount` is Morton-padded to a power of two, so it exceeds `GridWidth × GridHeight` |
| Blue cell fill | per-cell `EntityCount` (cross-archetype, from the internal `CellState`) |
| **Green boxes** | **Level 2** — ship cluster AABBs. The overlap you see *is* the problem |
| Yellow boxes | projectile cluster AABBs |
| Purple boxes | station cluster AABBs (static — they never migrate) |
| **Red boxes** | a cluster whose AABB centre is in a **different cell than its recorded home** — hysteresis drift, made visible |
| Triangles | fighters, coloured by faction; brighter = heavy hull |
| **Octagons** | **miners** — pale blue / pale orange. They mine asteroids and carry material home |
| **Irregular polygons** | asteroids; the rock shrinks as it is depleted. Deliberately not a square — a plain axis-aligned box reads as backdrop, being the shape of the cell grid and of every UI panel, so it looked like scenery rather than something you interact with. The outline is derived from the entity id, so a given rock keeps its shape |
| **Orange outline** | a cluster whose AABB exceeds `FillMaxCellArea` cells — spatially degenerate, fill suppressed |
| **Soft colour wash** | the LOD 2 density field — brightness = count, hue = faction mix. Appears only when entities have collapsed |
| **Double-boxed square** | a station in marker mode (far zoom), faction-coloured |
| **Ringed square** | an ore field in marker mode (far zoom) |
| Bottom-right panel | minimap: the same density field and the same landmarks, whole world, camera footprint outlined |
| Second window | database file map: one tile per page, colour = page kind, brightness = recently written (**M**) |

---

## Gameplay

### Layout — many local wars, not one front

Stations are placed on a **staggered lattice with the factions interleaved in a checkerboard**, so every base has
enemies as its nearest neighbours. Asteroids are anchored at the **midpoints of the closest enemy station pairs**,
so each ore field sits equidistant between two hostile bases and is fought over locally.

The ore layout matters more than the station layout, and it is not obvious why: **miners go to ore and fighters
hunt miners, so wherever the ore is, is where the war is.** Three asteroids in a ring at the map centre produced
exactly one battle, in the middle, regardless of where the stations sat.

Two details that were wrong on the first attempt, and are worth knowing because both looked correct:

- **Rally targets must be local.** Fighters with no target head for the *nearest enemy station*, never a centre of
  mass. An average position is a single point, so "head for the enemy's centre of mass" sends every fighter on the
  map to the same place — with interleaved factions that place is the middle of the map, which is exactly the
  global scrum the lattice exists to break up. Layout alone cannot fix a global rally rule.
- **Ore anchors need a minimum separation.** Taking the N shortest cross-faction midpoints outright puts the three
  long cross-map pairs within a few kilometres of the map centre, so three of the eight asteroids pile up there and
  quietly rebuild the central ore field. The separation is relaxed rather than abandoned if it cannot be met, so
  `AsteroidCount` is always honoured.

Measured with `--autoTicks`, which reports contested bins (density bins holding both factions — the number of
simultaneous fronts) and their RMS spread:

| Layout | Contested bins | Spread |
|---|---|---|
| `edges` + `ring` (the old one, kept as `battle-line.json`) | 6 | 23.9 km |
| `lattice` + `contested` (default, 3 stations/faction) | **24** | **27.8 km** |
| `lattice` + `contested`, `--stationsPerFaction=4` | **32** | **30.6 km** |

Four to five times the number of places a fight is happening, spread over a wider area. `--stationLayout=edges`
restores the single battle line, which is still the better view for watching cell migration sweep across the grid
in one direction.

### Gunnery — why fighters could not kill each other

Ships fire straight at the target's last recorded position, with no lead. Two fighters would circle indefinitely
without resolving. Measured over 8 000 ticks, the hit rate was **9.7 %**, and the reason was three errors that were
each an order of magnitude larger than the thing they had to land inside:

| Error source | Before | After |
|---|---|---|
| **Aim staleness** — fires at the position from its last acquisition | up to **445 m** | 60 m |
| **Flight drift** — target moves while the shot travels | 356 m | 120 m |
| `ShotHitRadius` — what it has to land inside | 25 m | 35 m |
| **Hit rate** | **9.7 %** | **38.0 %** |

Three findings from that measurement, none of which were guessable:

1. **Staleness, not aim, was the biggest term.** `TargetReacquireTicks` was 40, so a shot went where the enemy had
   been two thirds of a second earlier. That is not a prediction problem — the gun was aimed at stale data.
2. **Firing the acquisition query 5× more often is CHEAPER.** 40 → 8 ticks took the tick from 3.46 ms to 2.60 ms.
   Engagements that resolve stop accumulating ships that keep querying; the assumption that a more responsive
   sensor must cost more was exactly backwards.
3. **The fix belongs on the projectile, not the ship.** Miss distance is `flightTime × targetSpeed`, so what
   matters is the RATIO of ship speed to shot speed. Raising `ShotSpeed` 1500 → 3000 took the hit rate from 9.7 %
   to 38 % *with ships unchanged*. Slowing ships is the other half of the same ratio and costs pace; once shots are
   fast, cutting ship speed 667 → 450 moves the hit rate only 38 % → 41 %.

`FireRootTicks` (10) holds a ship still briefly after it fires, making it an easier target for return fire. Worth
about 2 points of hit rate on its own — it is a readable behaviour more than a fix.

> **Consequence:** the standing fleet roughly halved, ~3 000 → ~1 600. Ships had been accumulating *because* they
> could not kill each other. If you want the larger fleet back it now needs more ore or tougher hulls, not a bigger
> cap.

### The contested pickup

One pickup exists at a time, at a contested ore anchor. Winning it takes **200 hits from one faction** — each side
keeps its own tally, and the first to the number takes the effect.

| Effect | Duration | What it does |
|---|---|---|
| **POWER** | 30 s | every shot does double damage |
| **SHIELD** | 15 s | total immunity — half the duration because it is the strongest, and the side that wins a race is usually the side already ahead |
| **SPEED** | 30 s | every ship moves 1.5× faster, miners included |
| **MINING** | 45 s | ore rate and cargo capacity ×3 — the longest, and the only effect worth **more to the faction that is behind** |

**Per-faction tallies, not one shared counter.** A single counter where the last hit wins inverts the mechanic:
every hit before the winning one is work donated to a rival, so the optimal play becomes waiting. Separate tallies
make every shot advance only the side that fired it.

**The engage-or-race decision is parameter-free.** A fighter near the objective shoots *whichever is closer* — the
nearest enemy, or the pickup. That single rule produces both behaviours without scripting either: on the fringe of
the crowd the pickup is nearer so you race; inside the crowd an enemy is nearer so you fight. It self-balances,
because shots spent on enemies are shots not spent on your own tally, and every shot an opponent doesn't fire is a
point they don't score — denial and racing are the same currency.

**Progress decays** (one point per faction per 30 ticks), so a tally measures *sustained* pressure rather than
ground you held once.

**The pull is regional (~30 km), not global.** A pickup every fighter on the map converged on would empty every
other front for the duration of the contest, undoing the interleaved layout. At one lattice spacing the two or
three nearest bases contest it — a big local battle while the rest of the map keeps fighting — and the race is won
by whoever is locally stronger rather than always by the globally stronger faction.

The race is drawn as two bars on the pickup itself at **every LOD tier including the density view**, on the
minimap, and in the HUD:

```
CONTEST  POWER at (19,52) km   A 11076/200 [==..........]   B 19545/200 [===.........]
```

Without that readout a 200-hit contest is indistinguishable from a delay — you can see a crowd around an object but
not who is winning or by how much.

> **Measured side effect:** the pickups visibly soften the runaway. Before them a typical 14 000-tick run ended
> 2 500 v ~420; with them, 2 500 v 1 143 — and in one run the *trailing* faction took MINING and POWER and went on
> to lead 2 064 v 1 200. That is the comeback lever working, and it is why MINING exists.

### Economy

Two factions, each with stations, run an economy that gates the war:

- **Miners** (35 % of spawns) find the nearest asteroid via a radius query, park on it, deplete its capacity into
  their hold, and carry it home. Delivering adds to the faction's material pool.
- **Asteroids** (8 by default) sit on fixed anchors, drift slowly, and respawn on a slow timer once depleted — a
  respawn returns to the *vacant* anchor, so an ore field stays a stable place worth contesting instead of a lottery
  that relocates the war every few minutes.
- **Ships cost material.** A faction with no working miners stops reinforcing, and the runaway is decisive: a
  typical 12 000-tick run ends 2 500 v ~550 with the loser on 10 material. `MinerFloorRatio` does **not** rescue it
  — the loser's miners are farmed as fast as they are replaced, which was measured again at this scale.

> **The ship cap is not what sets the fleet size.** Raising `MaxShipsPerFaction` five-fold on its own did nothing:
> the population settled straight back to ~1 050, because the steady state is deaths against production. At 5×
> ships the loss rate was ~17 ships/s while ore funded 1.9/s. Ore SUPPLY has to scale with the fleet, so
> `ShipCost`, `CargoMax`, `MineRate` and `AsteroidCapacity` all moved with it. `CargoMax` turned out to be the real
> throughput knob: a 100 km map makes the round trip long, so delivery is limited by **trips**, not by how much ore
> exists or how fast it comes out of the rock.
- **Fighters hunt the enemy economy.** With no target and no recent damage, a fighter rallies to the **enemy's**
  miner centre of mass. Under fire it falls back to defend its own miners (`EscortRadius`).

  > The rally rule used to be the reverse — idle fighters returned to their *own* miners once they drifted more
  > than `EscortRadius` away. That was stable at 32 km and became a trap at 100 km: a fighter approached its own
  > economy, flipped to the enemy centroid once inside the radius, drifted back out, flipped again — **an orbit
  > around friendly miners that never crossed the map.** Fourteen thousand ticks produced 10 500 ore mined and
  > **zero shots fired**. Nothing was wrong with the acquisition query; the two sides simply never came within
  > sensor range. Hunting the enemy economy converges by construction, because both factions' miners head for the
  > same ore, and it is what the design called for anyway.

### A note on the cluster-AABB fill

Cluster AABBs are outlined at `--clusterBorderAlpha=0.3` and filled at `--clusterFillAlpha=0.001` — the faintest
fill 8-bit alpha can draw. Overlapping fills compound, so a single box is nearly invisible while a pile of them
glows; the brightness *is* the overlap. Two things this exposed immediately:

- **Projectile and asteroid cluster AABBs routinely span several cells**, because a cluster's members are whatever
  arrived in allocation order and projectiles/asteroids are created all over the map. Those are the big translucent
  blocks. It is the allocation-ordered-membership problem, visible at a glance.
- Boxes larger than `--fillMaxCellArea=2` cell-areas are **not** filled — one such quad covers the viewport and
  hides everything else. They get an orange outline and a count in the HUD instead.

## Scenarios

```bash
# huge cells (12.5 km, 8x8) — the "cell >> cluster" intent
dotnet run -c Release -- --scenario=scenarios/huge-cells.json

# many small cells (500 m, 200x200) — more migration, marginally better selectivity
dotnet run -c Release -- --scenario=scenarios/fine-grid.json

# economy-forward: ore scattered wide, so the fighting spreads with it. Best minimap scenario
dotnet run -c Release -- --scenario=scenarios/mining-focus.json

# the OLD layout: opposing columns, ore in the middle, one battle line that wanders
dotnet run -c Release -- --scenario=scenarios/battle-line.json

# stress: 10k ships. Expect the SIMULATION to be the wall long before the renderer is
dotnet run -c Release -- --scenario=scenarios/stress.json

# research runs (headless, no window interaction needed)
dotnet run -c Release -- --cellSweep=500,1000,2000,4000,8000,16000 --autoTicks=4000
dotnet run -c Release -- --autoTicks=6000 --autoShot=frame.png         # screenshot + frame report
dotnet run -c Release -- --autoTicks=6000 --autoViewHeight=3000        # verify LOD 0 at a stated zoom
dotnet run -c Release -- --autoTicks=6000 --autoRect=0,0,800,450       # report on one rect only
```

> Runs need **~6 000 ticks** to reach a steady state at 100 km — the first few thousand are transit. A 900-tick run
> reports `mined 0 killed 0` and that is warm-up, not a defect.

Every field of `Config` is settable as `--fieldName=value` and dumpable with `--printConfig`; the JSON scenario
files use the same names. Adding a knob to `Config.cs` automatically makes it settable and documented.

## What the rendering work deliberately did NOT do

The scale, LOD, minimap and culling above are the *mechanism* for holding more entities. None of it is an
optimisation pass, and two numbers are left standing on purpose because they are the next thing to measure:

- **Cluster occupancy is poor at this scale.** A typical default run sits at ~164 ship clusters for ~380 ships —
  **2.3 entities per cluster against a capacity of 8–64, and over half the clusters hold exactly one entity**
  (the HUD counts them as `singletons`). Clusters are per-(archetype, cell), so ships spread thinly across a
  100 km world fragment into one nearly-empty cluster per occupied cell. This is the documented tension seen from
  the other side: **AABB quality wants small cells, occupancy wants large cells, and one global `CellSize` decides
  both.**
- **Projectile hit-testing is untouched**, and it is the term that will dominate first: live shots scale with ship
  count, and each runs the low-selectivity query above every tick.

The renderer is also not yet free of the world's total size: the diagnostics pass is O(clusters) by design, and the
default `--densitySource=entities` is O(N). Both have a cheaper replacement already wired (`cells`), waiting on a
measurement rather than on an opinion.

## Self-verification

`FrameProbe` reads the rendered frame back and describes it in text — colour-family pixel counts plus an ASCII
coverage map — so rendering can be checked without a human looking at it. `FrameProbe.Check` asserts the frame is
neither empty nor uniform and that multiple overlay colours are present. This caught a genuine bug during
development: screenshots were solid black because `Texture.Update` was reading the back buffer *after* `Display()`.

`--autoTicks` renders **eight warm-up frames** before the measured one. A single cold call reports JIT and
first-touch allocation as per-frame cost — the density build came out at a flat ~2 ms whether it binned 128
entities or 2 000, which is the signature of fixed overhead rather than of work. Warm, it is **0.05–0.15 ms**, a
20–40× difference. It also lets the LOD hysteresis settle, so the reported tier is the one the view converges to
rather than the first guess.

---

## Notes for the engine

Things this build ran into that are about Typhon, not about the demo:

1. **Component field order matters.** Typhon packs `[Field]` members tightly; the CLR pads a `Sequential` struct to
   natural alignment. A `byte, short, int, short` component read back **shifted** — `Faction` returned
   `SpawnCooldown`'s value. Ordering fields largest-first makes the two layouts coincide. Nothing warns about this.

2. **`[Migrate-Orphan] … TryUpdateInPlace returned false (entity gone). Rolling back dst slot.`** is printed to the
   console by the engine whenever an entity is destroyed in the same tick it migrates — routine in any game with
   short-lived projectiles. It is handled correctly, but a `Console.WriteLine` in the migration path is not something
   a server wants at volume.

3. **`OutlierGuardFires` is unreadable in-process.** It exists only as a field on a write-only profiler span. Adding
   an accumulator beside `LastTickMigrationCount` would be ~3 lines and matches the existing pattern.

4. **`SpatialGridAccessor` cannot return `WorldMin`/`WorldMax`.** The config must be cached by the caller.

5. **No public cluster-granularity query.** `ClusterSpatialQuery` always narrowphases to entities, and a renderer
   wants the opposite: *clusters* overlapping a box, so it can batch over each cluster's SoA spans. The culling
   path here reaches `ArchetypeClusterState.PerCellIndex` through `InternalsVisibleTo` and reimplements the two
   broadphase stages by hand. It is thirty lines, it is exactly what `AabbClusterEnumerator` already does before it
   narrowphases, and every consumer that batches by cluster will need it. Worth exposing.

6. **`ArchetypeAccessor` has no "get cluster by chunk id".** The tier-partition overload
   `GetClusterEnumerator(int[] ids, 0, 1)` works as a stand-in and avoids an EntityMap probe per hit — worth 18× on
   the acquisition path here.

**Not in `Typhon.slnx`** — deliberately. SFML.Net pulls native binaries per-RID, and the merge gate builds the
solution on Linux; adding it there would make CI depend on a demo's graphics stack. Build it explicitly:
`dotnet build demo/SpaceBattle/SpaceBattle.csproj -c Release`.

This assembly is on the engine's `InternalsVisibleTo` list: per-cell `CellState`, the cluster→cell map and the
migration counters have no public surface, and showing exactly that hidden state is the tool's purpose.
