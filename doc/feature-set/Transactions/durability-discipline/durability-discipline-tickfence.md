---
uid: feature-transactions-durability-discipline-durability-discipline-tickfence
title: 'TickFence Discipline (Default)'
description: 'The default, lowest-cost SingleVersion write — durable at the next tick fence, not at commit.'
---

# TickFence Discipline (Default)
> The default, lowest-cost `SingleVersion` write — durable at the next tick fence, not at commit.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Transactions](../README.md)

**Assumes:** [SingleVersion (Tick-Fence Durability)](../../Ecs/storage-modes/storage-mode-singleversion.md)

## 🎯 What it solves

The bulk of a real-time simulation's writes — positions, velocities, AI state, anything the next tick
recomputes anyway — don't need per-write durability. Paying a WAL append, or worse a revision-chain allocation,
on every one of these writes would dominate the tick budget for no benefit: if the process crashes, the next
tick would have overwritten the value regardless. `TickFence` discipline gives these writes the cheapest
possible path while still bounding the data-at-risk window to a single tick.

## ⚙️ How it works (in brief)

A `TickFence`-discipline write is an in-place store directly into the `SingleVersion` cluster slot (HEAD) — no
staging, no per-write WAL append. The write marks the slot's chunk in the table's `DirtyBitmap`; at the tick
fence, the engine batches every dirty slot since the last fence into WAL records in one pass. This is the same
path every `SingleVersion` write has always used — `TickFence` is simply the name for it now that `Commit` exists
as an alternative. It is the implicit default: any transaction created without an explicit `discipline:` argument
uses it.

## 💻 Usage

```csharp
[Archetype(42)]
partial class Player : Archetype<Player>
{
    public static readonly Comp<Position> Pos = Register<Position>();   // StorageMode.SingleVersion
}

// Game tick — TickFence is the default, no argument needed
using var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred);
using var tx = uow.CreateTransaction();
ref var pos = ref tx.OpenMut(playerId).Write(Player.Pos);
pos.X += velocity.X * dt;
pos.Y += velocity.Y * dt;
tx.Commit();                  // ~40 ns write; value rides the next tick fence to the WAL

// Equally explicit, if calling out the choice matters at the call site:
using var tx2 = uow.CreateTransaction(discipline: DurabilityDiscipline.TickFence);
```

## ⚠️ Guarantees & limits

- Commit latency is unaffected by this write — there is no per-write WAL or commit-time cost; the cost is paid
  once per tick at the fence, amortized across every dirty `SingleVersion` slot.
- Data-at-risk window is **up to one tick** (~16ms at 60fps): a crash between the write and the next fence loses
  it. Acceptable for state the simulation regenerates or where a one-tick rollback is harmless.
- Tick-fence isolation — distinct from `Commit`'s read-committed: bulk and point reads by other transactions
  always read HEAD directly (no chain to walk, no staging to consult), so a reader can observe a value the
  writer hasn't yet flushed to WAL.
- A `SingleVersion` component used with `ReadsSnapshot` is rejected at scheduler `Build()` (rule CM-04) under
  either discipline — `TickFence` has no history to freeze to.
- Mixing disciplines on the same component across different transactions is fine — `TickFence` and `Commit`
  writes to the same slot compose correctly at recovery (last-writer-wins by LSN).
- A component declared `[Component(DefaultDiscipline = DurabilityDiscipline.Commit)]` cannot be written
  `TickFence`-style: the first write to it escalates the whole transaction to `Commit` (CM-02). Once any
  `TickFence` in-place write has already happened in that transaction, later escalation to `Commit` throws —
  pick the discipline before the first write.

## 🧪 Tests

- [StorageModeTickFenceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/StorageModeTickFenceTests.cs) —
  `WriteTickFence_ClearsDirtyBitmap` (in-place write, dirty-bitmap batching), `WriteTickFence_VersionedAndTransient_Skipped`
  (discipline only applies to `SingleVersion`)
- [TickFenceE2ETests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/TickFenceE2ETests.cs) — end-to-end
  write/reopen survival, last-write-wins across multiple updates, multi-entity recovery, only-dirty-entities-written

## 🔗 Related

- Parent feature: [SingleVersion Durability Discipline](./README.md)
- Sibling: [Commit discipline (Variant-A staging)](./durability-discipline-commit.md)

<!-- Deep dive: claude/overview/02-execution.md — Durability Discipline (SingleVersion) (#durability-discipline-singleversion) -->
<!-- ADR: ADR-057 — Committed Durability Discipline — claude/adr/057-committed-durability-discipline.md -->
