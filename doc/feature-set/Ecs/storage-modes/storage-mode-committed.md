---
uid: feature-ecs-storage-modes-storage-mode-committed
title: 'Committed Durability Discipline'
description: 'Zero-loss, atomic commits on the SingleVersion layout — without paying for a Versioned revision chain.'
---

# Committed Durability Discipline
> Zero-loss, atomic commits on the SingleVersion layout — without paying for a Versioned revision chain.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Ecs](../README.md)

**Assumes:** [SingleVersion (Tick-Fence Durable)](./storage-mode-singleversion.md)

## 🎯 What it solves

Some writes need both `SingleVersion`'s cheap layout and `Versioned`'s commit-time guarantees: a teleport, an
item pickup, a currency debit must be atomic and never lost, but doesn't need snapshot isolation or AS-OF
queries. Choosing `Versioned` for these pays ~6× the write cost for a revision chain you will never read
historically; choosing plain `SingleVersion` risks losing the write if the process crashes before the next tick
fence. Committed discipline closes that gap **on the same `SingleVersion` component** — it's a transaction-time
choice, not a schema change.

## ⚙️ How it works (in brief)

`Commit` is a runtime `DurabilityDiscipline` (`TickFence` default, or `Commit`), set per transaction or
side-transaction — not a new `StorageMode` and not a layout change. Under `Commit`, a write stages into a
per-transaction arena instead of touching the cluster HEAD slot; at `Transaction.Commit()` the staged values are
WAL-appended as ordinary durable records and only then published in place (HEAD memcpy plus exact index
reconciliation). `tx.Rollback()` just discards the arena — the HEAD was never touched, so rollback is O(1).
Every `SingleVersion` component you write inside a `Commit`-discipline transaction is staged this way (the
discipline is uniform per transaction).

## 💻 Usage

```csharp
[Component("Game.Wallet", 1, StorageMode = StorageMode.SingleVersion,
           DefaultDiscipline = DurabilityDiscipline.Commit)]   // optional: any tx touching Wallet escalates
public struct Wallet { public long Gold; }

[Archetype]
partial class Player : Archetype<Player>
{
    public static readonly Comp<Wallet> Wallet = Register<Wallet>();
}

// Explicit escalation for one critical operation:
using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit);
var e = tx.OpenMut(playerId);
e.Write(Player.Wallet).Gold -= price;
tx.Commit();                  // staged value WAL-logged + published atomically — zero loss on crash

// From inside a scheduled system, via the side-transaction idiom:
using var side = ctx.CreateSideTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit);
side.OpenMut(playerId).Write(Player.Wallet).Gold += reward;
side.Commit();
```

| Discipline | Write cost (Zen 4) | Durable when | Loss window | Isolation |
|------------|---------------------|--------------|-------------|-----------|
| `TickFence` (default) | ~40 ns | at the next tick fence | ≤ 1 tick | tick-fence |
| `Commit` | ~40 ns + commit publish | at `Commit()` | zero | read-committed |

## ⚠️ Guarantees & limits

- Applies only to `StorageMode.SingleVersion` components — `Versioned` is always commit-scoped already (no
  benefit), `Transient` is never durable (the discipline knob is meaningless there).
- Discipline is fixed per transaction: once any write escalates a transaction to `Commit` — explicitly, or
  because a component declares `DefaultDiscipline = DurabilityDiscipline.Commit` — every `SingleVersion` write
  in that transaction is commit-staged.
- Read-your-own-writes works for point reads (`EntityRef.Read`/`Write`) inside the writing transaction. Bulk
  span reads (`ClusterRef.GetSpan<T>`) inside that same transaction do **not** see staged values — read HEAD
  through a side-transaction or after commit instead.
- Isolation is read-committed, not snapshot — `ReadsSnapshot` on a `Commit`-discipline (or plain
  `SingleVersion`) component fails loudly at scheduler `Build()` time; use `Versioned` for snapshot/AS-OF reads.
- No revision chain, no deferred-cleanup GC cost — rollback is an arena reset, not a chain unwind.
- Recovery needs no discipline-specific code: a `Commit`-discipline write is an ordinary WAL slot record,
  replayed through the same path as every other write.

## 🧪 Tests

- [CommittedDisciplineTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/CommittedDisciplineTests.cs) — arena staging, publish-at-commit, read-your-own-writes, rollback discards the arena, cluster and non-cluster paths
- [CommittedDisciplineRecoveryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CrashRecovery/CommittedDisciplineRecoveryTests.cs) — crash recovery of `Commit`-discipline writes through ordinary WAL replay

## 🔗 Related

- Also documented from the durability-mode angle: [Durability Modes — Committed Durability Discipline](../../Durability/durability-modes/committed-discipline.md)
- Parent feature: [Storage Modes](./README.md)

<!-- Deep dive: claude/design/Ecs/committed-storage-mode.md, claude/adr/057-committed-durability-discipline.md, claude/design/Durability/MinimalWal/05-committed-mode.md -->
