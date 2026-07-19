---
uid: feature-transactions-durability-discipline-durability-discipline-commit
title: 'Commit Discipline (Variant-A Staging)'
description: 'Atomic, zero-loss SingleVersion writes — durable and visible together at Commit(), with no revision chain.'
---

# Commit Discipline (Variant-A Staging)
> Atomic, zero-loss `SingleVersion` writes — durable and visible together at `Commit()`, with no revision chain.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Transactions](../README.md)

**Assumes:** [SingleVersion (Tick-Fence Durability)](../../Ecs/storage-modes/storage-mode-singleversion.md)

## 🎯 What it solves

A `SingleVersion` write under the default `TickFence` discipline can lose up to one tick on crash — fine for
regenerable state, unacceptable for a teleport, an item pickup, or a currency debit. The traditional fix is to
make the component `Versioned`, but that pays a ~6x write-cost tax (copy-on-write content-chunk allocation,
revision-chain append, eventual GC) for snapshot isolation and AS-OF history these writes never use. `Commit`
discipline closes that gap: zero-loss, atomic durability at near-`SingleVersion` write cost, with no chain.

## ⚙️ How it works (in brief)

A `Commit`-discipline write stages its value into a per-transaction native arena — the cluster HEAD slot is never
touched before commit (CM-01). The first write to a given (entity, component) pair seeds the staging slot from
the current HEAD, so partial-field writes are correct. At `Commit()`, every staged value is appended to the WAL
as an ordinary slot record (carrying a `Committed` flag, telemetry only) and, after the append, published in
place: memcpy into HEAD, exact secondary B+Tree index reconciled, slot marked dirty — the same publish step
`Versioned` uses for its exact-index guarantee. `Rollback()` is O(1): the arena is discarded and HEAD was never
touched, so there is nothing to undo. Discipline is uniform per transaction (CM-02) — once any write escalates,
every `SingleVersion` write that transaction makes is staged the same way.

## 💻 Usage

```csharp
[Component("Game.Wallet", 1, StorageMode = StorageMode.SingleVersion,
           DefaultDiscipline = DurabilityDiscipline.Commit)]   // optional: every tx touching this escalates
struct Wallet { public long Gold; }

[Archetype]
partial class Player : Archetype<Player>
{
    public static readonly Comp<Position> Pos = Register<Position>();
    public static readonly Comp<Wallet> Wallet = Register<Wallet>();
}

// Explicit escalation for one critical operation:
using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit);
ref var pos = ref tx.OpenMut(playerId).Write(Player.Pos);
pos.X = teleportTarget.X;
pos.Y = teleportTarget.Y;
tx.Commit();                  // staged value WAL-logged then published — zero loss on crash

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

- All of a transaction's `Commit`-discipline writes become visible together at commit, or none do — `Rollback()`
  discards the staged values and HEAD is unaffected (CM-01).
- Read-your-own-writes works for point reads (`EntityRef.Read`/`Write`) inside the writing transaction. Bulk span
  reads (`ClusterRef.GetSpan<T>`) taken *inside* that same transaction do **not** see staged values — they read
  HEAD. Read after commit, or through a different transaction, instead.
- Isolation is **read-committed**, not snapshot — a `Commit`-discipline component used with `ReadsSnapshot` fails
  loudly at scheduler `Build()` (rule CM-04); use `Versioned` for snapshot/AS-OF reads.
- No revision chain and no deferred-cleanup GC cost — rollback is an arena reset, not a chain unwind (AC-6).
- An indexed field additionally pays a B+Tree `Move` (~200 ns) at commit, the same cost a `Versioned` write
  pays — the ≤25 ns / ≤60 ns targets are for non-indexed fields. Spatial indexing stays fence-batched for all
  modes (no extra commit-time cost).
- A duplicate WAL record from a concurrent tick fence re-emitting the just-committed value is benign
  (last-writer-wins by LSN, rule CM-03) — at most one redundant record, never a correctness issue.
- Recovery needs no discipline-specific code: a `Commit`-discipline write is an ordinary WAL slot record, replayed
  by the same `RecoveryApplier` slot upsert as every other write (AC-7).
- Composes with any `DurabilityMode`: discipline picks the write→WAL mechanism, mode still picks the flush
  timing — pairing `Commit` discipline with `DurabilityMode.Deferred` still buffers the WAL record until
  `Flush()`.

## 🧪 Tests

- [CommittedDisciplineTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/CommittedDisciplineTests.cs) —
  staging/publish (`CommitDiscipline_Write_PublishesAtCommit`), read-your-own-writes, rollback discards staged
  values (`CommitDiscipline_Rollback_DiscardsStaged`), multi-write atomicity, indexed-field commit cost
- [CommittedDisciplineRecoveryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CrashRecovery/CommittedDisciplineRecoveryTests.cs)
  — zero-loss crash survival, mixed `Commit`/`TickFence` writes under churn, indexed cluster spawn across a
  consolidating checkpoint crash

## 🔗 Related

- Parent feature: [SingleVersion Durability Discipline](./README.md)
- Sibling: [TickFence discipline (default)](./durability-discipline-tickfence.md)

<!-- Deep dive: claude/overview/02-execution.md — Durability Discipline (SingleVersion) (#durability-discipline-singleversion), claude/design/Ecs/committed-storage-mode.md -->
<!-- ADR: ADR-057 — Committed Durability Discipline — claude/adr/057-committed-durability-discipline.md -->
<!-- Rules: claude/design/Durability/MinimalWal/07-rules.md — module CM -->
