# AntHill — Typhon's real-time showcase

**AntHill runs 100,000 ants as live database entities.** Every ant's position, velocity, genome and
state lives in [Typhon](../../../README.md) — a real-time ACID database engine — and is read, mutated
and committed **60 times per second** by a scheduled system graph running across 16 worker threads.
Godot 4 is only the front-end: it draws whatever the database published this tick.

That is the point of the demo. A conventional database would be nowhere near this loop; AntHill exists
to show that Typhon's transaction, MVCC, spatial-index and scheduling machinery is fast enough to *be*
the simulation state, not a place you flush it to afterwards.

<!-- Screenshot: drop a capture here when one is current. -->

---

## What you're actually looking at

| | |
|---|---|
| **100,000 ants** | 5 colonies, 4 castes (worker / soldier / larva / queen), foraging, fighting, dying, respawning |
| **5 nests · 50 food sources** | food carried home builds per-nest reserves; sources deplete |
| **8 spiders** | predators that hunt via a spatial proximity query; once killed they sit out 30 s, then respawn at a random world edge |
| **Pheromone field** | 1000×1000 cells, 3 channels (food trail / home trail / fight alarm), deposited at 60 Hz, evaporated at 10 Hz |
| **Fire** | Drossel–Schwabl forest-fire cellular automaton at 10 Hz |
| **Vegetation** | 100,000 plants that burn and despawn as the fire front passes |
| **Day/night + Daisyworld** | 600 sim-second cycle driving terrain, ant and sky brightness |

World size is 20,000 simulation units, rendered as a 100 m × 100 m terrain with a procedural Perlin
heightmap. Ants walk the slopes; the camera refuses to fly below it.

---

## Running it

**Requirements:** [Godot 4.6+ **.NET/Mono** build](https://godotengine.org/download) and the
**.NET 10 SDK**. (`Godot.NET.Sdk/4.6.2`, `net10.0` — see `AntHill.Demo.csproj`.) On Windows the
renderer requests D3D12.

**From the Godot editor** — open `demo/AntHill/AntHill.Demo/project.godot`, then <kbd>F5</kbd>.

**From the command line:**

```bash
godot --path demo/AntHill/AntHill.Demo
```

**Build only** (useful for a fast compile check without launching):

```bash
dotnet build demo/AntHill/AntHill.Demo/AntHill.Demo.csproj
```

The engine creates its database next to the running binary and **deletes it on every startup** — each
run begins from a fresh spawn. The write-ahead log stays enabled, so the durability cost you see in the
profiler is real, not an artefact of a disabled WAL.

> Expect a moment of silence at launch while 100,000 entities are spawned and committed.

---

## Controls

Press <kbd>F1</kbd> in-game for this same list, bottom-left. Both come from the same handlers
(`GameCamera.cs`, `Main._UnhandledInput`) — if they ever disagree, the code wins.

### Camera — free-flight, FPS-style

| Input | Action |
|---|---|
| <kbd>W</kbd> <kbd>A</kbd> <kbd>S</kbd> <kbd>D</kbd> | Fly along the camera's own axes — pitch down and <kbd>W</kbd> dives toward the ground |
| <kbd>Space</kbd> / <kbd>Ctrl</kbd> | Climb / descend on the world axis, independent of where you're looking |
| <kbd>Shift</kbd> | Sprint |
| **Right-drag** | Mouselook (yaw + pitch). Cursor is captured until you release |
| **Mouse wheel** | Adjust *movement speed* — this is a free-cam, not an orbit rig, so there is no zoom |
| <kbd>T</kbd> | Toggle top-down ↔ isometric tilt |
| <kbd>Ctrl</kbd>+<kbd>T</kbd> | Cinematic tilt (85°) |

The camera never drops below 2 m of terrain clearance — fly at a hill and it rides over the top.

### Layers & panels

| Input | Action |
|---|---|
| <kbd>H</kbd> | Pheromone heatmap overlay on the ground |
| <kbd>M</kbd> | Minimap (bottom-right) — **click it to fly there** |
| <kbd>F1</kbd> | Debug HUD + the on-screen control reference |
| <kbd>Esc</kbd> | Settings panel |

### Time

| Input | Action |
|---|---|
| <kbd>`</kbd> | Pause / resume |
| <kbd>[</kbd> <kbd>]</kbd> | Step speed through 0 · 0.5× · 1× · 2× · 4× · 10× |

Speed is a `dt` multiplier applied inside the system bodies, not a runtime throttle: the engine keeps
ticking at 60 Hz even at 0×, every system still runs, and frames are still published. Pausing therefore
freezes the world without freezing the loop — you can fly around a stopped simulation and inspect it,
and the profiler keeps recording the tick cost of doing nothing.

### God-game tools

Pick a tool, then **left-click the ground** to apply it.

| Key | Tool | Effect |
|---|---|---|
| <kbd>1</kbd> | Pointer | Neutral — clicks do nothing |
| <kbd>2</kbd> | Food | Drop a new food source (8,000 units) |
| <kbd>3</kbd> | Rock | Place an obstacle ants must path around |
| <kbd>4</kbd> | Cull | Kill every ant within a 1 m radius |
| <kbd>5</kbd> | Ignite | Start a fire — watch it spread through the vegetation |
| <kbd>P</kbd> | Pause | One-shot pause toggle; the palette springs back to Pointer |

Tool clicks are queued from Godot's input thread and drained by `ToolCommandSystem` in the **Input**
phase, so a placement lands in the *same tick* the simulation runs — no one-frame lag.

Settings offers a *"Snap tools to 1 m grid"* toggle if you want tidy rock walls.

---

## Reading the HUD

Everything below is toggled by <kbd>F1</kbd> (or the settings checkbox — they stay in sync).

**Top-left** — simulation state: time scale, ant and nest counts, foraging vs returning split, food
sources remaining, food delivered, nest reserves, deaths.

**Below it** — engine and render telemetry: FPS, draw calls, visible ants, per-system tick timings, LOD
band, and the instance-texture upload size.

**LOD band** tells you which rendering regime you're in, derived from the visible ground width at the
camera's focus point:

| Band | Visible width | What you get |
|---|---|---|
| **Loupe** | < 5 m | Individual ants, full detail |
| **Foot** | 5 – 30 m | Individuals, cross-fading toward density |
| **Patch** | > 30 m | Density field — the ant carpet as a heat surface |

**Simulation tiers** (`T0`–`T3` in the HUD) are a different axis: concentric rings around the camera
that control *simulation* fidelity, not rendering. On-camera cells run every tick at T0; distant cells
are amortized down to T3, with per-step rate multipliers that keep the time-integrated behaviour
(pheromone deposits, energy decay) correct despite running less often. This is why 100k ants stay
affordable — you only pay full price for what you're looking at.

---

## How the demo is built

Three projects, one logical demo:

```
demo/AntHill/
├── AntHill.Core/            Simulation + all Typhon usage. ZERO Godot dependency.
├── AntHill.Demo/            ← you are here. Godot app: rendering, UI, input.
├── AntHill.Harness/         Headless console runner — same simulation, no renderer.
└── AntHill.Harness.Tests/   Tests for the harness's scenario tooling.
```

The Godot-free split is deliberate: it lets `AntHill.Harness` run the identical workload headlessly for
benchmarking and CI-style validation, and it keeps engine-facing code honest — nothing in `AntHill.Core`
can quietly reach for a `Node`.

### The tick pipeline

`TyphonBridge.BuildSchedule` declares one DAG named `AntHill` with four phases. Systems declare which
components and resources they read and write; the engine **derives the dependency graph from those
declarations** rather than from a hand-maintained edge list, and rejects undeclared conflicts at startup.

| Phase | Systems | Work |
|---|---|---|
| **Input** | `ToolCommand`, `EnvironmentTick`, `TierAssignment` | drain tool clicks · advance day/night · re-tier the spatial grid from the camera |
| **Simulation** | `AntUpdate`, `SpiderUpdate` | the whole ant model in one parallel cluster walk; then predator hunting |
| **Trail** | `PheroDecay`, `FireTick`, `VegetationTick` | ambient environment sweeps at 10 Hz |
| **Render** | `AntStatsAggregator`, `PrepareRenderBuffer`, `HeatmapRgbaPack`, `FillRenderBuffer`, `PublishRenderFrame` | aggregate events · downsample the pheromone field · fill per-worker instance buffers · publish the frame |

`AntUpdate` is the hot one — a single walk per cluster doing energy decay and respawn, position
integration with edge bounce, food/nest interaction, pheromone steering and pheromone deposit, all in
registers. It replaced a 14-system tier-split topology that paid for redundant cluster walks every tick.

Phases are an ordering *contract*, not a barrier: the scheduler dispatches a later-phase system eagerly
as soon as its actual data dependencies are satisfied, rather than waiting for the whole phase to drain.

### Simulation ↔ render handoff

Each worker fills its **own** render buffer (12 floats per visible ant) — no locks on the write side.
`PublishRenderFrame` swaps them into a frame that Godot picks up on its next `_Process`. The renderer
packs that into a single `MultiMeshInstance3D` plus one RGBA32F state texture at **16 bytes per
instance**; a vertex shader unpacks position, yaw, colour and flags per ant. One draw call, 100k ants.

### Typhon features on display

| Feature | Where |
|---|---|
| ECS archetypes & components | `Ant` = `WorldBounds` + `Velocity` + `Genetics` + `AntState` (`ECS/Archetypes.cs`) |
| `SingleVersion` storage mode | every AntHill component — no MVCC history needed for entities rewritten every tick |
| Spatial index | `[SpatialIndex]` on `WorldBounds.Bounds`; 20×20 grid of 1000-unit cells with 5% migration hysteresis |
| Proximity queries | `EcsQuery.WhereNearby` — spider hunting, food smelling |
| Declarative system scheduling | `Reads`/`Writes`/`ReadsResource`/`ReadsEvents` → auto-derived DAG |
| Event queues | `AntDied`, `FoodPickedUp`, `FoodDelivered` → producer→consumer edges in the graph |
| Parallel chunked systems | `.Parallel().ChunksPerWorker(...)` on the ant walk and the heatmap reduce |
| WAL durability | enabled, at 60 Hz, under full load |
| Profiler & tracing | see below |

---

## Profiling

Profiling is configured by [`typhon.telemetry.json`](./typhon.telemetry.json), copied to the output
directory. It ships with essentially everything on — GC, allocations, CPU sampling, scheduler, tick
fence, storage, spatial, query.

Pass profiler arguments after Godot's `++` user-arg separator:

```bash
# Write a trace file
godot --path demo/AntHill/AntHill.Demo ++ --trace anthill.typhon-trace

# Or open a live TCP session the Workbench can attach to
godot --path demo/AntHill/AntHill.Demo ++ --live
```

`--live [port]` takes an optional port; `--live-wait <ms>` blocks startup until a client connects.
Traces open in the Typhon Workbench, where the System DAG view will show the graph described above —
including the event-queue arrows between `AntUpdate` and `AntStatsAggregator`.

---

## Headless runs

For benchmarking without a renderer, use the sibling harness:

```bash
cd demo/AntHill/AntHill.Harness
dotnet run -c Release -- --duration 10                    # ad-hoc, prints per-system p50/p99
dotnet run -c Release -- --scenario <scenario.yaml>       # declarative validation scenario
```

Scenarios can vary ant count, worker count, RNG seed and tier mode — including a `ForceTier0` mode that
disables camera-distance amortization so the *entire* world runs at full fidelity, which is the
worst-case stress configuration. See
[`claude/design/AntHill/ScriptableValidationHarness.md`](../../../claude/design/AntHill/ScriptableValidationHarness.md).

---

## Related reading

- [`claude/overview/13-runtime.md`](../../../claude/overview/13-runtime.md) — the scheduler and system dispatch this demo exercises
- [`claude/design/Runtime/07-system-access-declarations.md`](../../../claude/design/Runtime/07-system-access-declarations.md) — how access declarations become a DAG
- [`claude/overview/04-data.md`](../../../claude/overview/04-data.md) — MVCC, component tables, storage modes
- [`rules/runtime-scheduling.md`](../../../rules/runtime-scheduling.md) — the invariants the schedule is validated against at startup
