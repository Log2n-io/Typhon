---
uid: demo-anthill
title: AntHill
description: A 3D forest-floor ant-colony simulation proving Typhon's beyond-RAM ECS — hot/cold paging, three storage modes, crash recovery, spatial queries, and parallel dispatch.
---

# AntHill

**AntHill** is a single-user god-game built on Typhon and Godot 4: a 100 m × 100 m forest-floor microcosm populated by millions of insect-scale creatures — ants foraging via emergent pheromone trails, spider predators, a spreading fire cellular automaton coupled to vegetation, and interactive tools to drop food, place rocks, cull, and ignite. It runs Typhon **embedded in the Godot process** (no client/server), with a 3D orthographic top-down camera spanning a 200× zoom range from a single-ant loupe to the whole patch. It exists to prove, on screen, that a persistent database-backed world larger than RAM can be simulated in real time.

## The Typhon proof

Every headline capability maps to a Typhon feature that an all-memory ECS cannot provide:

| Feature | Typhon proof |
|---------|-------------|
| **Beyond-RAM storage** | ~10M ants ≈ 640 MB on disk, only ~64 MB in the page cache — the world does not fit in RAM by design |
| **Hot/cold partitioning** | The camera viewport defines the hot zone (~100K ants); pan and cold ants page in from SSD, old ants evict via clock-sweep |
| **Three storage modes** | Position (`SingleVersion`, direct mutation), Genetics (`Versioned`, ACID + MVCC), Pheromone (`Transient`, heap-only) coexist in one world |
| **Crash recovery** | `kill -9` then restart: genetics survive (WAL + checkpoint), pheromone trails are correctly gone — the highways dissolve and must re-form |
| **Spatial R-Tree** | "Find food / ants within radius" runs as a native 2D spatial query (`R2Df32`) on indexed position fields |
| **Parallel dispatch** | Foraging, pheromone, environment, and render systems run each tick on the hot set via the DAG scheduler |

To keep the world alive everywhere without simulating 10M entities at full rate, AntHill uses a **four-tier simulation model**: full sim near the camera (60 Hz), movement-only just outside it (60 Hz), coarse amortized ticking further out (~1 Hz), and a per-colony statistical aggregate for the rest — with pheromone evaporation running globally every tick so cold areas never show ghost trails.

## Tech stack

- **Engine:** Typhon, embedded via `ProjectReference` — one process, renderers pull directly from ECS SoA memory.
- **Client/rendering:** Godot 4.6, C# / .NET 10, Forward+ renderer. Creatures are drawn with per-archetype `MultiMeshInstance3D` and custom vertex/fragment shaders ("alive without animation" via shader-driven bob + align-to-velocity). The ground is a single shader-composited plane (heightmap + pheromone heatmap + density). Fire and smoke use `GpuParticles3D`.
- **Persistence:** WAL + checkpoint files on disk, independent of the Godot process — the basis of the kill-and-recover demo.

## Current status

AntHill is **buildable and runnable** (Godot 4.6 + .NET 10). It is developed in ten phases (0–9), each a runnable vertical slice:

| Phase | Scope | Status |
|-------|-------|--------|
| 0 | 2D → 3D orthographic migration, camera, MultiMesh renderer | ✅ Working |
| 1 | LOD camera bands, procedural heightmap, settings/debug overlays | ✅ Working |
| 2 | Rendering at scale (~100K workers), render-buffer pipeline | ✅ Working |
| 3 | Stigmergy — foraging + pheromone trails, merged update system | ✅ Working |
| 4 | Interaction & HUD — tools (food/rock/cull/ignite/pause), event log | ✅ Working |
| 5 | Conflict — spider predators + caste/colony schema | ◻ Partial (predators in; inter-colony combat not wired) |
| 6 | Environment — day/night, fire CA, vegetation, obstacles | ✅ Substantially done |
| 7 | Persistence UI (save/load, rewind/timeline scrub) | ⏳ Not started |
| 8 | Art commitment — real meshes, animations, particles | ⏳ Not started |
| 9 | Trailer polish | ⏳ Not started |

There is **no trailer or gameplay video yet** — art (Phase 8) and trailer (Phase 9) are the last, deliberately deferred phases. The current build uses primitive meshes plus the shader "alive" trick.

> 📷 Screenshots/trailer to follow (demo art is Phase 8-9).

## Build & run

AntHill lives at [`demo/AntHill/`](https://github.com/Log2n-io/Typhon/tree/main/demo/AntHill) and is organized as:

- **AntHill.Core** — the simulation (ECS schema, systems, pheromone/fire/vegetation grids).
- **AntHill.Demo** — the Godot 4 project (scenes, renderers, camera, HUD).
- **AntHill.Harness** — a headless driver for running the simulation without the Godot client.

Open the `AntHill.Demo` Godot project in Godot 4.6 (with the .NET 10 SDK installed) and run it, or drive the simulation headless through `AntHill.Harness`.

## Links

- **Source:** [`demo/AntHill/`](https://github.com/Log2n-io/Typhon/tree/main/demo/AntHill)
- **Simulation core:** [`demo/AntHill/AntHill.Core`](https://github.com/Log2n-io/Typhon/tree/main/demo/AntHill)
- **Godot project:** [`demo/AntHill/AntHill.Demo`](https://github.com/Log2n-io/Typhon/tree/main/demo/AntHill)

The full build spec (phase-by-phase plan, camera/LOD design, component schema, and the divergences between plan and shipped code) lives in the project's demo design documents alongside the source.
