---
uid: concept-scheduler
title: 'Scheduler & phases'
description: 'From each system''s declared reads/writes the engine derives the execution graph once and rejects unsafe schedules at build time. Tracks, DAGs, and phases give structural order; access declarations give data order.'
---

# Scheduler & phases

> **In one line:** from each [system](xref:concept-system)'s declared reads/writes, the engine **derives the execution graph once** and rejects unsafe schedules **at build time**.

The [runtime](xref:concept-runtime) walks a fixed structure you declare at startup: **Track → DAG → Phase → System**. Phases (`Input` / `Simulation` / `Output` / `Cleanup`, or your own) are a DAG-local total order, but not a barrier. Within and across phases, two systems may run concurrently *unless a declared access conflict or an explicit edge connects them*, directly or through other systems; that is the only way a `Simulation` system waits for an `Input` system.

Access declarations are the heart of it. `Reads<T>` / `Writes<T>` state what a system touches; `ReadsFresh<T>` wants this tick's value (ordered after the writer), `ReadsSnapshot<T>` accepts last tick's (runs *concurrently* with the writer — Versioned-only). Two unordered writers of the same component in the same phase is a **build-time error**, not a production race. `After`/`Before` add explicit edges when you need them.

## How it relates

- **[System](xref:concept-system)** — supplies the access declarations the scheduler consumes.
- **[Runtime](xref:concept-runtime)** — executes the derived graph every tick.
- **[Storage mode](xref:concept-storage-mode)** — `ReadsSnapshot<T>` requires a `Versioned` `T` (history to hand out).
- **[Snapshot isolation](xref:concept-snapshot-isolation)** — what a snapshot read returns.

## In the API

- [`Phase`](xref:Typhon.Engine.Phase) — the built-in phases ([`Input`](xref:Typhon.Engine.Phase.Input) / [`Simulation`](xref:Typhon.Engine.Phase.Simulation) / [`Output`](xref:Typhon.Engine.Phase.Output) / [`Cleanup`](xref:Typhon.Engine.Phase.Cleanup)).
- [`SystemBuilder`](xref:Typhon.Engine.SystemBuilder) — where access and ordering are declared ([`Reads`](xref:Typhon.Engine.SystemBuilder.Reads*)/[`Writes`](xref:Typhon.Engine.SystemBuilder.Writes*)/[`ReadsSnapshot`](xref:Typhon.Engine.SystemBuilder.ReadsSnapshot*)/[`After`](xref:Typhon.Engine.SystemBuilder.After*)/…).

## Learn & use

- **Narrative:** [Guide ch.5 §3–4 — declaring access, ordering](xref:guide-systems)
- **Feature detail:** [declarative scheduling](xref:feature-runtime-declarative-scheduling-index) · [track/DAG/phase partitioning](xref:feature-runtime-declarative-scheduling-track-dag-phase-partitioning) · [access conflict detection](xref:feature-runtime-declarative-scheduling-access-conflict-detection)
