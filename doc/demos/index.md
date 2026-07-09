---
uid: demo-index
title: Demos
description: Showcase applications that prove Typhon's beyond-RAM ECS thesis — persistent worlds larger than RAM with crash recovery, ACID, and microsecond access.
---

# Demos

> **Typhon is the only ECS engine that isn't all-memory.** It stores and processes databases larger than RAM — with crash recovery, ACID transactions, and microsecond-level access — all in one embedded engine.

Every mainstream ECS framework (Unity DOTS, Flecs, EnTT, Bevy, Unreal Mass) is an in-memory data structure: the whole world lives in RAM, and when the process dies the world dies with it. Persistence, if it exists, is a bolt-on serialize/deserialize step. Typhon inverts that. The world is a **memory-mapped, page-backed, MVCC database** that the ECS runtime reads and writes in place. The hot set lives in the page cache; everything else sits on SSD and pages in on demand — zero-copy, no deserialization. Kill the process and the durable state survives; the correct parts come back, the ephemeral parts are gone.

This demo exists to make that concrete. It's a pitch/showcase page — a link-out to source and design, not a hosted interactive build.

## What the demo proves

AntHill is built to exercise capabilities no all-memory ECS can offer:

- **Beyond-RAM storage** — the database on disk is many times larger than the page cache; only the visible/active slice is resident.
- **Hot/cold partitioning** — pan or jump the camera and entities page in from SSD as they enter the active zone, and evict as they leave.
- **Three storage modes in one world** — pick durability per component: `SingleVersion` (fast direct mutation), `Versioned` (ACID + MVCC), and `Transient` (heap-only, intentionally lost on crash).
- **Crash recovery** — `kill -9` and restart: durable component data (genetics) is intact; `Transient` data (pheromone trails) is correctly gone.
- **Spatial R-Tree queries** — native "find things within radius" over 2D coordinates (`R2Df32`).
- **Parallel dispatch** — multiple systems running each tick on the hot set via the DAG scheduler.

AntHill uses a **four-tier simulation architecture** (full sim near the camera, movement-only nearby, coarse amortized ticking further out, statistical aggregate for the rest) so the world stays alive everywhere without simulating every entity at full fidelity every tick — the same "living world at O(observers) cost" pattern that production MMOs use.

## The demo

**[AntHill](anthill.md)** — a 3D forest-floor microcosm: an ant colony with emergent pheromone foraging, predators, fire, and interactive god-game tools, built on Typhon + Godot 4. Buildable today (phases 0–6 working). See the [AntHill page](anthill.md) for the full breakdown.

| Dimension | AntHill |
|-----------|---------|
| **Entities** | 10M+ (ants + pheromone grid) |
| **DB size** | ~640 MB |
| **Spatial index** | 2D float (`R2Df32`) |
| **ACID proof** | Atomic food pickup / deposit |
| **Transient proof** | Pheromone trails vanish on crash |
| **Visual impact** | Emergent trail-highway formation |
| **Godot rendering** | 3D `MultiMeshInstance3D` (ortho top-down) |
| **Build complexity** | Medium (~10 days) |

## Tech stack

| Layer | Technology |
|-------|-----------|
| Engine | Typhon (embedded C# ACID database + ECS runtime) |
| Client | Godot 4 (C# / GodotSharp), .NET 10 |
| Rendering | `MultiMeshInstance3D` + custom vertex/fragment shaders |
| Integration | Typhon embedded in-process — renderers read straight from ECS memory, no client/server protocol |

> 📷 Screenshots/trailer to follow (demo art is Phase 8-9).
