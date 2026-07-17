---
uid: concept-tick-fence
title: 'Tick fence'
description: 'The per-tick durability boundary: at the end of each tick, dirty SingleVersion writes are batched into the WAL, so a crash loses at most the last tick. Not a memory fence.'
---

# Tick fence

> **In one line:** the per-[tick](xref:concept-tick) **durability boundary** — at the end of each tick, dirty [`SingleVersion`](xref:concept-storage-mode) writes are batched into the WAL, so a crash loses at most the last tick.

`SingleVersion` writes are in-place and *visible immediately*, but not individually logged. The tick fence is where they become **durable**: `dbe.WriteTickFence(n)` batches every dirty SingleVersion slot since the last fence into WAL records, establishing a crash-recovery boundary — you lose at most the last tick, never a torn value. The same step applies deferred [index](xref:concept-index) / [spatial](xref:concept-spatial-index) / cluster maintenance. Under the [runtime](xref:concept-runtime) it runs automatically each tick; from a bare transaction you call it yourself after writing.

This is the sharpest case of Typhon's [visible ≠ durable](xref:guide-isolation-durability) split: a SingleVersion value is visible the instant it's written, and becomes durable one fence later.

> ⚠️ **"Tick fence" is overloaded — three meanings.** Here it's the per-tick *durability step*. It is **not** a memory/CPU fence, and **not** the internal parallel-execution machinery that speeds that step up. The `TickFence` [durability discipline](xref:concept-durability) is the transaction-time enum whose durability this step realises.

## How it relates

- **[Tick](xref:concept-tick)** — the fence runs at the end of every tick.
- **[Storage mode](xref:concept-storage-mode)** — only `SingleVersion` relies on the fence (Versioned is commit-durable; Transient never persists).
- **[Durability — mode & discipline](xref:concept-durability)** — realises the default `TickFence` discipline (≤ 1 tick loss); the `Commit` discipline opts out for zero loss.
- **[WAL & checkpoint](xref:concept-wal-checkpoint)** — the fence emits the WAL records that make the writes durable.

## In the API

- [`DatabaseEngine`](xref:Typhon.Engine.DatabaseEngine) — [`WriteTickFence(...)`](xref:Typhon.Engine.DatabaseEngine.WriteTickFence*) (called for you under the runtime).
- [`DurabilityDiscipline`](xref:Typhon.Schema.Definition.DurabilityDiscipline) — [`TickFence`](xref:Typhon.Schema.Definition.DurabilityDiscipline.TickFence) (default) vs [`Commit`](xref:Typhon.Schema.Definition.DurabilityDiscipline.Commit).

## Learn & use

- **Narrative:** [Guide ch.3 — transactions & durability](xref:guide-transactions) · [ch.5 — the tick loop](xref:guide-systems)
- **Reference:** [Isolation & durability cheat sheet](xref:guide-isolation-durability)
- **Feature detail:** [TickFence discipline](xref:feature-transactions-durability-discipline-durability-discipline-tickfence) · [parallel tick fence](xref:feature-runtime-tick-lifecycle-parallel-tick-fence)
