---
uid: feature-durability-durability-modes-committed-discipline
title: 'Committed Durability Discipline'
description: 'Zero-loss, atomic writes on Typhon''s cheapest component layout — without paying for an MVCC revision chain.'
---

# Committed Durability Discipline
> Zero-loss, atomic writes on Typhon's cheapest component layout — without paying for an MVCC revision chain.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Durability](../README.md)
**Assumes:** [SingleVersion (Tick-Fence Durability)](../../Ecs/storage-modes/storage-mode-singleversion.md)

## 🎯 What it solves

`SingleVersion` components are Typhon's cheapest write path (~3ns, in-place), but by default they're only
durable at the next tick fence — up to one tick of writes (~16ms at 60fps) can be lost on crash. A large class
of writes (a player teleport, an item pickup, a currency debit) needs atomicity and zero data loss but not
MVCC snapshot isolation or AS-OF queries. Forcing those onto a `Versioned` component buys zero-loss durability
at ~6× the write cost (a revision-chain allocation plus its eventual GC) for an isolation guarantee the write
never uses. Committed discipline gives zero-loss, atomic writes on the `SingleVersion` layout at a fraction of
`Versioned`'s cost.

## ⚙️ How it works (in brief)

`DurabilityDiscipline` is a per-transaction knob (`TickFence` default, or `Commit`) — orthogonal to
`DurabilityMode`: mode still decides *when* the WAL flushes, discipline decides *how* a `SingleVersion` write
becomes durable. Under `Commit`, a write is staged into a per-transaction arena (the live HEAD slot is never
touched pre-commit); at `Commit()`, the staged values are appended to the WAL as ordinary records and only then
published in place — memcpy to HEAD plus index reconciliation. The discipline is uniform per transaction: once
any write escalates a transaction to `Commit` — explicitly, or because a component declares
`[Component(DefaultDiscipline = DurabilityDiscipline.Commit)]` — every `SingleVersion` write in that
transaction is commit-staged. Rollback is O(1): discard the arena, HEAD was never touched.

## 💻 Usage

```csharp
[Component("Game.Wallet", 1, StorageMode = StorageMode.SingleVersion,
           DefaultDiscipline = DurabilityDiscipline.Commit)]   // optional: every tx touching this escalates
struct Wallet { public long Gold; }

[Archetype(42)]
partial class Player : Archetype<Player>
{
    public static readonly Comp<Position> Pos = Register<Position>();
    public static readonly Comp<Wallet> Wallet = Register<Wallet>();
}

// Explicit escalation for one critical operation:
using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit);
var e = tx.OpenMut(playerId);
ref var pos = ref e.Write(Player.Pos);
pos.X = teleportTarget.X;
pos.Y = teleportTarget.Y;
tx.Commit();              // staged value WAL-logged + published atomically — zero loss on crash

// From inside a scheduled system, via the TickContext side-transaction idiom:
using var side = ctx.CreateSideTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit);
side.OpenMut(playerId).Write(Player.Wallet).Gold -= price;
side.Commit();
```

| Discipline | Write cost (Zen 4) | Durable when | Loss window | Isolation |
|------------|---------------------|--------------|-------------|-----------|
| `TickFence` (default) | ~40 ns | at the next tick fence | ≤ 1 tick | tick-fence |
| `Commit` | ~40 ns + commit publish | at `Commit()` | zero | read-committed |

## ⚠️ Guarantees & limits

- Applies only to `StorageMode.SingleVersion` components — `Versioned` is always commit-scoped already (no
  benefit), `Transient` is never durable (discipline is meaningless there).
- Read-your-own-writes works for point reads (`EntityRef.Read`/`Write`) inside the writing transaction. Bulk
  span reads (`ClusterRef.GetSpan<T>`) inside that same transaction do **not** see staged values — read HEAD
  through a side-transaction or after commit instead.
- Isolation is **read-committed**, not snapshot — a `Commit`-discipline (or plain `SingleVersion`) component
  used with `ReadsSnapshot` fails loudly at scheduler `Build()` time; use `Versioned` for snapshot/AS-OF reads.
- A duplicate WAL record from a concurrent tick fence re-emitting the just-committed value is benign
  (last-writer-wins by LSN) — at most one redundant record, never a correctness or double-durability issue.
- No revision chain, no deferred-cleanup GC cost — rollback is an arena reset, not a chain unwind.
- Recovery needs no discipline-specific code: a `Commit`-discipline write is an ordinary WAL slot record,
  replayed by the same apply routine as every other write.

## 🧪 Tests

- [CommittedDisciplineTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/CommittedDisciplineTests.cs) — arena staging/publish, read-your-own-writes, rollback-discards-staged, `DefaultDiscipline` escalation, cluster and non-cluster archetypes
- [CommittedDisciplineRecoveryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CrashRecovery/CommittedDisciplineRecoveryTests.cs) — hard-crash proof: a `Commit`-discipline write survives as an ordinary Slot record with zero discipline-specific recovery code

## 🔗 Related

- Parent feature: [Durability Modes](./README.md)
- Sibling: [Committed Durability Discipline](../../Ecs/storage-modes/storage-mode-committed.md) — same feature, ECS storage-mode-facing side of this durability-facing knob
- Sibling: [SingleVersion Durability Discipline (TickFence / Commit)](../../Transactions/durability-discipline/README.md) — the Transaction-level API surface for choosing this discipline

<!-- Deep dive: claude/design/Durability/MinimalWal/05-committed-mode.md, claude/adr/057-committed-durability-discipline.md, claude/design/Ecs/committed-storage-mode.md -->
<!-- Rules: claude/design/Durability/MinimalWal/07-rules.md — module CM -->
