---
uid: concept-tick
title: 'Tick'
description: 'A tick is one iteration of the runtime''s loop — a single step of your simulation or a game frame. Each tick runs every system once, wraps them in one Unit of Work, and advances time.'
---

# Tick

> **In one line:** one iteration of the runtime's loop — a single **step of your simulation** (a game frame). Each tick runs every [system](xref:concept-system) once, then advances.

The [runtime](xref:concept-runtime) drives a **metronome**: it fires a tick at a fixed rate (60 Hz by default) on a dedicated thread, runs your [systems](xref:concept-system) over the world, and repeats — the classic game/simulation loop, but with the transaction plumbing handled for you. `ctx.DeltaTime` is the seconds elapsed since the previous tick; multiply rates by it and your logic runs at the same speed regardless of tick rate.

The fixed cadence is a *choice*. For pure parallel computation (no real-time pacing) you run ticks as fast as the work completes — each tick is simply "one parallel pass" over your systems. Either way, a tick is the atomic unit of progress.

**Within a tick**, the runtime enforces a discipline you don't have to write:

- **One [Unit of Work](xref:concept-unit-of-work) per tick**, flushed at tick end — so all of a tick's commits share one durability cycle.
- **One [transaction](xref:concept-transaction) per system**, created on its worker thread and committed by the scheduler. Each successive system gets a higher TSN, so a later system sees an earlier one's committed writes — [visibility](xref:concept-snapshot-isolation) advances *across* systems within the tick.
- At tick end, the **[tick fence](xref:concept-tick-fence)** makes `SingleVersion` writes durable (≤ 1 tick loss) just before the UoW flushes.

> 💡 **The side-transaction escape hatch.** "One UoW per tick" is the default, not a cage: for a write that must be durable *right now* — a purchase, a trade — call `ctx.CreateSideTransaction(DurabilityMode.Immediate)` to commit a [transaction](xref:concept-transaction) you own, independently of the tick. See [Unit of Work](xref:concept-unit-of-work) and [Durability](xref:concept-durability).

## How it relates

- **[Typhon runtime](xref:concept-runtime)** — drives the tick loop and its cadence.
- **[System](xref:concept-system)** — every system runs once per tick.
- **[Unit of Work](xref:concept-unit-of-work)** — exactly one opened and flushed per tick.
- **[Tick fence](xref:concept-tick-fence)** — the durability boundary that closes each tick.
- **[Snapshot isolation](xref:concept-snapshot-isolation)** — visibility advances system-to-system as TSNs increase within the tick.
- **[TickContext](xref:concept-tick-context)** — the object each system's `Execute` receives to touch the world this tick.

## In the API

- [`TickContext`](xref:Typhon.Engine.TickContext) — what a system's `Execute` receives ([`DeltaTime`](xref:Typhon.Engine.TickContext.DeltaTime), [`TickNumber`](xref:Typhon.Engine.TickContext.TickNumber), [`Transaction`](xref:Typhon.Engine.TickContext.Transaction), [`Accessor`](xref:Typhon.Engine.TickContext.Accessor), [`Entities`](xref:Typhon.Engine.TickContext.Entities), [`CreateSideTransaction`](xref:Typhon.Engine.TickContext.CreateSideTransaction*)).
- [`TyphonRuntime`](xref:Typhon.Engine.TyphonRuntime) — [`Start`](xref:Typhon.Engine.TyphonRuntime.Start*) / [`Shutdown`](xref:Typhon.Engine.TyphonRuntime.Shutdown*) / [`CurrentTickNumber`](xref:Typhon.Engine.TyphonRuntime.CurrentTickNumber); [`RuntimeOptions.BaseTickRate`](xref:Typhon.Engine.RuntimeOptions.BaseTickRate) sets the cadence.

## Learn & use

- **Narrative:** [Guide ch.5 — systems & the tick loop](xref:guide-systems)
- **Feature detail:** [tick lifecycle](xref:feature-runtime-tick-lifecycle-index) · [tick execution engine](xref:feature-runtime-tick-execution-engine-index) · [side transactions](xref:feature-runtime-side-transactions)
