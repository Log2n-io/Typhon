---
uid: guide-transactions
title: '3 — Changing data: transactions & durability'
description: 'Every change to your data enters Typhon through a transaction. Chapter 1 used the one-line CreateQuickTransaction(); this chapter opens the box: the two…'
---

# 3 — Changing data: transactions & durability

Every change to your data enters Typhon through a **transaction**. Chapter 1 used the one-line `CreateQuickTransaction()`; this chapter opens the box: the two layers a transaction sits in, how `Commit` and `Rollback` behave, how you trade commit latency against crash-safety, and — crucially — what "transactional" actually means for each storage mode.

This is a **key** chapter. The mental model here is what keeps your data correct under concurrency and crashes.

> 📖 **Two ideas most databases fuse, Typhon keeps separate:** *isolation* (who can see a change) and *durability* (whether it survives a crash). You control them with three independent dials — `StorageMode` (per component), `DurabilityDiscipline` (per transaction), `DurabilityMode` (per UnitOfWork). This chapter tells the story; the **[Isolation & durability cheat sheet](isolation-durability-cheatsheet.md)** is the one-page reference to come back to.

---

## 1. Two layers: a UnitOfWork wraps Transactions

Typhon splits "what's atomic" from "what's durable" into two objects:

- A **`Transaction`** is the unit of **isolation**: one writer, one consistent read snapshot, one atomic set of changes that either all commit or all roll back.
- A **`UnitOfWork`** is the unit of **durability**: it decides *when* committed changes become crash-safe (hit the WAL). One UoW can contain several transactions sharing one durability cycle.

```csharp
using var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit);
using var tx  = uow.CreateTransaction();
// ... mutate ...
tx.Commit();
// uow flushes per its DurabilityMode when disposed
```

`CreateQuickTransaction(mode)` from ch.1 is just the common case folded into one call — it makes a UoW, makes one transaction in it, and ties their lifetimes together. Use the explicit form when you want several transactions under one durability cycle, or finer control over flushing.

> 💡 **Why two layers, and why is a `Transaction` single-thread-affine?** A transaction is owned by the thread that created it and must never be touched from another — so the engine puts **no locks on the transaction object itself**, which is what makes opening one essentially free. Durability, by contrast, is a batched, cross-cutting concern (one fsync can make many transactions durable at once), so it lives one level up in the UoW. In the runtime ([ch.5](05-systems.md)) this maps cleanly: one UoW per tick, one transaction per system.

---

## 2. Writing, committing, rolling back

Inside a transaction you spawn, mutate, and destroy:

```csharp
using (var tx = dbe.CreateQuickTransaction())
{
    var e = tx.OpenMut(probe);               // open for mutation
    e.Write(Harvester.Cargo).Amount += 100;  // Write<T> returns a ref you mutate in place
    tx.Commit();
}
```

- `OpenMut(id)` opens an entity for writing; `Write(Harvester.Cargo)` returns a `ref` to the component so you mutate it directly.
- `Spawn<T>(...)` / `Destroy(id)` create and remove entities (ch.1).
- `Commit()` makes the transaction's changes visible to later snapshots.
- `Rollback()` (or simply disposing without `Commit`) discards them.

**Until `Commit`, no other transaction can see your Versioned changes** — they read the previous value. That's isolation: a half-finished transaction is invisible.

`Rollback` is where storage mode bites (see §4): it cleanly reverts **Versioned** data, but an in-place **SingleVersion/Transient** write has *already happened* and stays. Rollback is not a universal undo — it's an undo of the transactional (Versioned) part.

---

## 3. Durability modes — latency vs. safety

`DurabilityMode` (set when you create the UoW/quick transaction) decides how hard the engine works to make a commit survive a crash:

| Mode | When the WAL is flushed | Commit latency | At risk on crash |
|---|---|---|---|
| `Deferred` | only on explicit flush / UoW dispose | ~1–2 µs | everything since the last flush |
| `GroupCommit` (default tuning) | automatically, ~every 5 ms | ~1–2 µs | ≤ one flush interval |
| `Immediate` | fsync on every `Commit` | ~15–85 µs | nothing |

> 💡 **`Deferred` and `GroupCommit` commit at the same speed** — both just append to the WAL buffer (~1–2 µs) and return. They differ only in *when* that buffer is flushed to disk, i.e. how much you can lose. `Immediate` is the one that pays a real latency cost, because `Commit()` blocks on the fsync.

> 💡 **Why expose this at all?** Durability is a cost you should choose per workload, not have forced on you. A bulk world-load wants `Deferred` — flush once at the end. A game tick wants `GroupCommit` — microsecond commits, at most a few milliseconds of loss if the process dies. A financial-style write wants `Immediate` — never acknowledge a commit that isn't on disk. Same API, three points on the safety/latency curve.

`Commit()` returning does **not** by itself mean "on disk" — that's what the mode controls. Under `Immediate`, a returned `Commit` *is* durable; under the others, durability lands later (or on flush).

---

## 4. Reads: snapshot isolation

A transaction reads against a **fixed snapshot** taken when it was created. Every Versioned read through it sees the database *as of that moment* — later commits by other transactions are invisible to it, for its whole life.

```csharp
using var reader = dbe.CreateReadOnlyTransaction();   // a pure-read snapshot
var cargoBefore = reader.Open(probe).Read(Harvester.Cargo).Amount;

// ... meanwhile, another transaction commits a change to probe's Cargo ...

var cargoAgain = reader.Open(probe).Read(Harvester.Cargo).Amount;
// cargoAgain == cargoBefore — the reader's snapshot didn't move
```

> 💡 **Why snapshot isolation?** It's the property that lets readers and writers run concurrently without getting in each other's way: a reader never takes a lock to "hold" the data and never waits for a writer to finish — it just keeps reading its own consistent version. The price is keeping old versions around while someone might still need them, which is exactly what *Versioned* storage pays for and *SingleVersion/Transient* opt out of. Read-only transactions (`CreateReadOnlyTransaction`) make the intent explicit: writes throw, commit is a no-op, and there's no durability bookkeeping.

For reading at one frozen snapshot across many threads in parallel, there's a dedicated accessor — covered with querying in [ch.4](04-querying.md) and the runtime in [ch.5](05-systems.md).

---

## 5. What each storage mode guarantees here

Everything above — isolation, rollback, commit-controlled durability — is the **Versioned** contract. Pull the modes back together, now from the transaction's point of view:

| | **Versioned** | **SingleVersion** | **Transient** |
|---|---|---|---|
| Visible to others before `Commit` | no | yes, immediately | yes, immediately |
| `Rollback` reverts the write | yes | no | no |
| `Commit` decides durability (`DurabilityMode`) | yes | no — tick-fenced, unless *Commit* discipline (below) | no — never persisted |
| Concurrent writers | conflict-detected | last write wins | last write wins |

So a transaction is a true ACID envelope **for the Versioned data it touches**. For SV/Transient components, the transaction still gives you three things — thread affinity, a consistent snapshot for any *Versioned* components in the same archetype, and atomic entity create/destroy — but it does **not** give you isolation, rollback, or commit-timed durability on those components' *data*. That's the deal you accepted when you chose the faster mode in [ch.2](02-modeling.md).

The practical guidance: if a value's correctness depends on "did this transaction commit?", it must be Versioned. If it doesn't, a faster mode is free performance.

> 💡 **The middle ground — a `SingleVersion` write that must never be lost.** By default a SingleVersion write is durable only at the next tick fence (≤ 1 tick of loss). When *one specific* write must be atomic **and** zero-loss — a teleport, an item pickup, a currency debit — but you don't need MVCC snapshots or history, open its transaction with the **`Commit` discipline**: `uow.CreateTransaction(discipline: DurabilityDiscipline.Commit)` (or mark the component `[Component(..., DefaultDiscipline = DurabilityDiscipline.Commit)]`). The write is staged, made atomic and zero-loss durable *at* `Commit`, then published in place — without paying for a revision chain. That's a fourth point on the safety curve; the **[cheat sheet](isolation-durability-cheatsheet.md#4-storage-mode-guarantee-matrix)** lays all four side by side.

---

## 🧭 What's next

You can now write, commit, roll back, and reason about what survives a crash and what other transactions see. Next:

- **[Chapter 4 — Querying & views](04-querying.md):** finding entities efficiently, and views that stay current as data changes.
- **[Chapter 5 — Systems & the tick loop](05-systems.md):** how the runtime drives one UoW per tick and one transaction per system, in parallel.
- **[Isolation & durability cheat sheet](isolation-durability-cheatsheet.md):** the one-page reference — the two clocks, the three dials, the full guarantee matrix, and the crash contract. Bookmark it.

## 🧩 Key concepts & types

**Concepts** (the mental model — one page each): [Transaction](../key-concepts/transaction.md) · [Unit of Work](../key-concepts/unit-of-work.md) · [Snapshot isolation](../key-concepts/snapshot-isolation.md) · [Storage mode](../key-concepts/storage-mode.md) · [Durability — mode & discipline](../key-concepts/durability.md) · [Tick fence](../key-concepts/tick-fence.md).

**Exact calls:** `DatabaseEngine.CreateUnitOfWork(DurabilityMode)` · `UnitOfWork.CreateTransaction(DurabilityDiscipline)` · `CreateQuickTransaction` / `CreateReadOnlyTransaction` · `Transaction.OpenMut` + `EntityRef.Write<T>` · `Commit()` / `Rollback()`.
