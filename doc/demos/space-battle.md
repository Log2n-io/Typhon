---
uid: demo-space-battle
title: SpaceBattle
description: A headless .NET 10 real-time combat benchmark demonstrating Typhon's ECS, runtime scheduling, spatial clustering, durable tick fences, recovery, and asynchronous observability.
---

# SpaceBattle

**SpaceBattle** is a headless real-time combat benchmark built on Typhon and .NET 10. It creates a deterministic fleet of 50,000 ships in a bounded three-dimensional world, advances the battle at a fixed 25 TPS cadence, and persists every completed simulation tick. It exists to prove that Typhon's ECS, scheduler, spatial maintenance, durability boundary, recovery behavior, and telemetry can work together in one sustained workload.

Unlike AntHill, SpaceBattle deliberately has no renderer, web server, command prompt, or gameplay input. The console is an observation surface for a reproducible simulation, not the simulation itself.

## The Typhon proof

Every headline capability maps to a Typhon feature that an all-memory combat loop would not provide by itself:

| Feature | Typhon proof |
|---------|-------------|
| **Persistent ECS world** | Ships, target locks, and the simulation run are stored as Typhon entities and components rather than transient process state |
| **Deterministic simulation** | A fixed seed, fixed 0.04-second step, stable entity identities, and deterministic roster sampling make runs reproducible |
| **Runtime scheduling** | Movement, targeting, lock maintenance, combat, damage resolution, and output execute through an ordered `SpaceBattle` DAG |
| **Spatial clustering** | The XY spatial grid maintains clustered ship data while movement, lock range, and weapon range use exact three-dimensional distances |
| **Durability boundary** | The tick fence persists the completed combat state; a graceful pause adds an immediate-durability checkpoint |
| **Crash recovery** | Restarting an incomplete run validates its identity, restores the last safe state, and rebuilds derived runtime state |
| **Asynchronous observability** | Typed per-worker damage queues, performance histograms, and resource snapshots expose the workload without changing its logical time |

The battle starts with a 250-tick staging period, then ships wander, acquire targets, maintain locks, fire, resolve simultaneous damage, and eventually produce a winner, draw, or timeout. A tick overrun is reported but never changes the fixed simulation delta.

## Tech stack

- **Engine:** Typhon, embedded through `ProjectReference` — the console process owns the database and ECS runtime directly.
- **Workload:** C# / .NET 10 console application with a fixed 25 TPS simulation loop.
- **Data model:** Three ECS archetypes for the simulation run, ships, and target locks; enabled components represent equipment state.
- **Spatial model:** An XY spatial grid for cluster broadphase and maintenance, with exact 3D distance rules for movement and combat.
- **Persistence:** Tick-fence durability plus an application-level pause checkpoint; startup validation rejects incompatible or ambiguous databases.
- **Observability:** Console log snapshots every 25 ticks, resource snapshots every 125 ticks, rolling p50/p95/p99 tick timings, and optional Typhon trace output.

## Current status

SpaceBattle is **buildable and runnable** as a headless benchmark. The production workload and its companion test suite cover the complete simulation lifecycle:

| Capability | Status |
|------------|--------|
| Deterministic 50,000-ship workload | ✅ Working |
| ECS schema, target locks, movement, and combat | ✅ Working |
| Ordered runtime DAG and per-worker damage queues | ✅ Working |
| Spatial clustering and three-dimensional combat rules | ✅ Working |
| Tick-fence persistence and safe `Ctrl+C` pause | ✅ Working |
| Resume validation and terminal outcome protection | ✅ Working |
| Asynchronous performance and resource observations | ✅ Working |
| Graphical client | Deliberately headless |

## Build & run

SpaceBattle lives at [`demo/SpaceBattle/`](https://github.com/yhcedpn/Typhon/tree/main/demo/SpaceBattle) and is organized as:

- **SpaceBattle** — the .NET 10 console application, ECS schema, simulation systems, persistence, recovery, and observation publisher.
- **SpaceBattle.Tests** — deterministic NUnit tests for initialization, behavior, combat, runtime progression, observability, pause, and recovery.

Install the **.NET 10 SDK**, then run the production workload from the repository root:

```powershell
dotnet run --project demo/SpaceBattle/SpaceBattle.Main/SpaceBattle.Main.csproj --configuration Release
```

The first run bulk-loads 50,000 ships before starting the runtime. The default database is `default.typhon` under `demo/SpaceBattle/bin/Release/net10.0/`. A battle advances for at most 45,000 completed ticks (30 minutes of logical simulation time) and normally ends earlier with one or zero ships remaining.

Press `Ctrl+C` once to request a safe pause. The current tick is allowed to finish, its completed state is checkpointed, and the next invocation of the same command resumes the incomplete run. Completed and timed-out databases are never overwritten; change the `RunName` constant in [`Program.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattle.Main/Program.cs) to start an independent run.

Run the small verification suite without building the whole solution:

```powershell
dotnet test demo/SpaceBattle/SpaceBattle.Tests/SpaceBattle.Tests.csproj --configuration Release --filter 'FullyQualifiedName~SpaceBattle.Tests.InitialWorldTests'
dotnet test demo/SpaceBattle/SpaceBattle.Tests/SpaceBattle.Tests.csproj --configuration Release
```

The console reports initialization separately from tick performance. Runtime lines include the process segment, completed tick, living ships, target locks, shots, hits, deaths, p50/p95/p99 tick duration, overruns, and the most utilized resource node. Deep profiling is disabled by default; set `EnableDeepProfiling` to `true` in [`Program.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattle.Main/Program.cs) to write a `.typhon-trace` beside the database.

## Links

- **Source:** [`demo/SpaceBattle/`](https://github.com/yhcedpn/Typhon/tree/main/demo/SpaceBattle)
- **Console entry point:** [`Program.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattle.Main/Program.cs)
- **Simulation host:** [`SpaceBattleHost.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattle.Main/SpaceBattleHost.cs)
- **Runtime and systems:** [`SpaceBattleSimulation.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattle.Main/SpaceBattleSimulation.cs)
- **Tests:** [`demo/SpaceBattle/SpaceBattle.Tests`](https://github.com/yhcedpn/Typhon/tree/main/demo/SpaceBattle/SpaceBattle.Tests)

The Typhon concepts behind the demo are documented in the [ECS overview](../in-depth-overview/06-ecs.md), [spatial overview](../in-depth-overview/07-spatial.md), [runtime overview](../in-depth-overview/10-runtime.md), [durability overview](../in-depth-overview/11-durability.md), and [observability overview](../in-depth-overview/12-observability.md).
