---
uid: concept-storage-mode
title: 'Storage mode'
description: 'A per-component, design-time choice of memory layout and ACID guarantees — Versioned (MVCC/snapshot/ACID), SingleVersion (fast in-place, tick-fence durable), or Transient (heap only).'
---

# Storage mode

> **In one line:** a **per-component**, design-time choice of memory layout and ACID guarantees — `Versioned`, `SingleVersion`, or `Transient`.

Set on the `[Component]` attribute and **immutable after registration**. Because it lives on the component *type*, one archetype freely mixes all three. The mode decides whether MVCC/[isolation](xref:concept-snapshot-isolation) exists at all — and what a write costs.

| Mode | Isolation | Durability | Write cost (Zen 4) |
|---|---|---|---|
| `Versioned` (default) | snapshot isolation, MVCC history | zero loss, full ACID | ~250 ns |
| `SingleVersion` | none — live, last-writer-wins | ≤ 1 [tick](xref:concept-tick) loss (tick-fence WAL) | ~40 ns |
| `Transient` | none — live | none — heap only, gone on crash | ~40 ns |

> Measured on Ryzen 9 7950X (Zen 4), .NET 10 Release, hot cache. A `Versioned` write is ~6× a fast-mode write (copy-on-write: allocate a chunk, copy the value, append a revision, stamp a TSN); reads follow suit — ~80 ns vs ~15 ns (~5×).

> 📌 **`Committed` is not a fourth mode.** It is the [`Commit` durability discipline](xref:concept-durability) layered on the byte-identical `SingleVersion` layout — commit-time, zero-loss, atomic durability *without* a revision chain.

## How it relates

- **[Snapshot isolation](xref:concept-snapshot-isolation)** — only `Versioned` provides it.
- **[Durability — mode & discipline](xref:concept-durability)** — the `Commit` discipline applies *only* to the `SingleVersion` layout.
- **[Transaction](xref:concept-transaction)** — what "transactional" guarantees per mode.
- **[Tick fence](xref:concept-tick-fence)** — where `SingleVersion` durability is realised.
- **[Cluster storage](xref:concept-cluster-storage)** — the *implicit* consequence: one `SingleVersion`/`Transient` component flips the whole archetype to clustered SoA (~50× faster bulk iteration).

## In the API

- [`StorageMode`](xref:Typhon.Schema.Definition.StorageMode) — the enum ([`Versioned`](xref:Typhon.Schema.Definition.StorageMode.Versioned) / [`SingleVersion`](xref:Typhon.Schema.Definition.StorageMode.SingleVersion) / [`Transient`](xref:Typhon.Schema.Definition.StorageMode.Transient)), set via `[Component(StorageMode = ...)]`.
- [`DurabilityDiscipline`](xref:Typhon.Schema.Definition.DurabilityDiscipline) — the SingleVersion [`TickFence`](xref:Typhon.Schema.Definition.DurabilityDiscipline.TickFence) ⇄ [`Commit`](xref:Typhon.Schema.Definition.DurabilityDiscipline.Commit) escalation.

## Learn & use

- **Narrative:** [Guide ch.2 §2 — the decision that matters most](xref:guide-modeling)
- **Reference:** [Isolation & durability cheat sheet](xref:guide-isolation-durability)
- **Feature detail:** [Storage modes](xref:feature-ecs-storage-modes-index) — [Versioned](xref:feature-ecs-storage-modes-storage-mode-versioned) · [SingleVersion](xref:feature-ecs-storage-modes-storage-mode-singleversion) · [Transient](xref:feature-ecs-storage-modes-storage-mode-transient) · [Committed](xref:feature-ecs-storage-modes-storage-mode-committed)
