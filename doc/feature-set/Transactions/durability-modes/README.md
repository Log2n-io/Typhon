---
uid: feature-transactions-durability-modes-index
title: 'Durability Modes (Deferred / GroupCommit / Immediate)'
description: 'Per-UoW control of WAL flush timing — trade commit latency for the data-at-risk window on crash.'
---

# Durability Modes (Deferred / GroupCommit / Immediate)
> Per-UoW control of WAL flush timing — trade commit latency for the data-at-risk window on crash.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Transactions](../README.md)

## 🎯 What it solves

The Unit of Work (UoW) is Typhon's durability boundary — the closest equivalent to a session or connection in a
traditional database. Different workloads sharing one engine instance need different durability guarantees: a
game tick can tolerate losing the last ~16ms of work on crash, a player trade must never lose anything, and a
general request handler sits somewhere in between. A single global durability setting would force every workload
onto the slowest, most conservative choice. `DurabilityMode` lets each UoW pick its own commit-latency vs.
data-at-risk trade-off once, at creation, independent of every other UoW running concurrently.

## ⚙️ How it works (in brief)

`DurabilityMode` is fixed when a UoW is created and controls only **when** that UoW's WAL records are forced to
stable media (FUA) — never whether a transaction commits or becomes MVCC-visible. Every `tx.Commit()` inside the
UoW serializes its WAL record to the commit buffer in ~1-2µs regardless of mode; the mode decides whether that
record stays buffered until you ask (`Deferred`), gets swept up by the WAL writer's periodic flush
(`GroupCommit`, default every 5ms), or blocks the calling thread for the FUA round-trip before `Commit()` returns
(`Immediate`, ~15-85µs). The transaction is always the unit of crash atomicity in every mode — recovery replays a
clean prefix of committed work, never a half-applied transaction.

## 💻 Usage

```csharp
// Game tick — batch many transactions, flush once at the tick boundary
using var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred);
foreach (var player in activePlayers)
{
    using var tx = uow.CreateTransaction();
    ref var pos = ref tx.OpenMut(player.Id).Write(Player.Position);
    ApplyMovement(ref pos);
    tx.Commit();              // ~1-2µs — WAL record buffered, not yet on disk
}
await uow.FlushAsync();       // one FUA for the whole tick's worth of changes

// General request handler — bounded data-at-risk, no per-tx wait
using var req = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit);
using var tx2 = req.CreateTransaction();
ref var hp = ref tx2.OpenMut(targetId).Write(Player.Health);
hp.Current -= damage;
tx2.Commit();                 // ~1-2µs — durable within WalWriterOptions.GroupCommitIntervalMs (default 5ms)

// Financial trade — zero data-at-risk
using var trade = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
ref var wallet = ref trade.OpenMut(buyerId).Write(Player.Wallet);
wallet.Gold -= price;
trade.Commit();               // blocks ~15-85µs — WAL FUA complete before returning
```

| Mode | Commit latency | Data-at-risk window | Best for |
|------|-----------------|----------------------|----------|
| `Deferred` | ~1-2µs | until `uow.Flush()` / `FlushAsync()` | game ticks, batch imports |
| `GroupCommit` | ~1-2µs | ≤ `GroupCommitIntervalMs` (default 5ms) | general request handlers |
| `Immediate` | ~15-85µs | zero | trades, irreversible state changes |

## ⚠️ Guarantees & limits

- Mode is fixed for the UoW's lifetime — there is no API to change it mid-UoW; open a second UoW for a
  different trade-off.
- Atomicity and MVCC isolation are unaffected by mode — only the post-crash data-loss window changes.
- `GroupCommitIntervalMs` is an engine-wide WAL writer setting (`DatabaseEngineOptions.Wal`), shared by every
  `GroupCommit` UoW; the WAL writer also flushes early once the ring buffer exceeds 80% capacity, so the
  interval is a maximum delay, not a guarantee.
- Disposing a `Deferred` UoW does **not** flush — unflushed work stays volatile until something else flushes
  the WAL (explicit `Flush()`/`FlushAsync()`, the GroupCommit timer, or back-pressure). `GroupCommit` and
  `Immediate` UoWs flush on dispose.
- `Immediate` can throw `CommitDurabilityUncertainException` if the FUA confirmation doesn't arrive in time —
  the transaction is already committed and MVCC-visible; this means "durability unconfirmed," never a rollback.
- Two orthogonal knobs extend this axis without changing UoW mode: [Per-Transaction Durability
  Override](./durability-override-escalation.md) escalates a single operation's flush timing, and
  [SingleVersion Durability Discipline](../durability-discipline/README.md) controls *how* a `SingleVersion`
  write becomes durable, independent of flush timing.
- Max durable tx/s: ~12K-65K for `Immediate` (FUA round-trip bound) vs. millions for `GroupCommit`/`Deferred`
  (CPU-bound, amortized FUA).

## 🧪 Tests

- [WalIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/WalIntegrationTests.cs) —
  `WAL_GameTick_DeferredBatch_FlushAtEnd` (batched `Deferred` commits + single end-of-tick flush),
  `WAL_CriticalTrade_ImmediateAtomicity` (`Immediate` zero-loss trade), `WAL_MixedDurability_AllModesCoexist`
  (all three modes running concurrently), `WAL_Commit_DurableLsnAdvances` (parametrized across all three modes)

## 🔗 Related

- Sub-features: [Per-Transaction Durability Override](./durability-override-escalation.md)
- Related feature: [SingleVersion Durability Discipline](../durability-discipline/README.md) — the orthogonal
  per-transaction axis (TickFence/Commit) that decides *how* a `SingleVersion` write reaches the WAL

<!-- Deep dive: claude/overview/02-execution.md §2.1 Unit of Work (#21-unit-of-work), §2.3 Durability Modes (#23-durability-modes), claude/overview/README.md -->
<!-- ADRs: ADR-005 — Durability Mode Per Unit of Work — claude/adr/005-durability-mode-per-uow.md, ADR-036 — WAL Durability Modes Architecture — claude/adr/036-wal-durability-modes.md -->
<!-- Rules: claude/rules/durability.md — module UoW Registry -->
