---
uid: guide-isolation-durability
title: 'Cheat sheet — Isolation & durability'
description: 'One page to come back to. Typhon separates visibility (isolation) from survival (durability), and gives you three independent dials to control them. This sheet maps all of it — the two clocks, the three dials, the storage-mode matrix, and the crash contract.'
---

# Cheat sheet — Isolation & durability

Most databases fuse two ideas into one word, *commit*: the moment a change becomes **visible** to others is the same moment it becomes **safe on disk**. Typhon splits them. That decoupling is what buys microsecond commits and per-component cost control — but it means "I committed" answers *two* questions, and you need to know which one you asked.

This page is a reference, not a tutorial. Read [ch.3](03-transactions.md) once to learn the story; come back **here** whenever you need to reassure yourself about a guarantee.

---

## 1. The one idea: two clocks, not one

Typhon runs two independent clocks. A commit advances both, but they cross their *safe* line at different moments.

```
 VISIBILITY clock  (isolation)         DURABILITY clock  (survival)
 ───────────────────────────           ────────────────────────────
 measured in  TSN                      measured in  LSN
 "who can see this change?"            "will this change survive a crash?"

 A reader sees every change with       A change is safe once its commit
 TSN ≤ its own snapshot TSN,           record is fsync'd to the WAL
 fixed when the reader started.        (DurableLsn ≥ its LSN).

        ▲ becomes VISIBLE at Commit           ▲ becomes DURABLE later
        │ (immediately, to future readers)     │ (when the WAL flushes — you
        │                                        choose when: see dial #3)
```

> 💡 **The whole cheat sheet in one sentence:** *visible* and *durable* are different events on different clocks — `Commit()` returning guarantees the first, and only the `Immediate` durability mode guarantees the second at the same instant.

Two facts that follow, and surprise people:

- A committed change can be **visible but not yet durable** — normal in `Deferred`/`GroupCommit`. A crash before the flush loses it, even though other transactions already read it.
- **Checkpointing adds zero durability.** Your data is durable the moment its WAL record is fsync'd. The checkpoint only *consolidates* WAL into the data file so old WAL segments can be recycled. It's automatic and you never wait for it.

---

## 2. The three dials you control

Isolation and durability aren't one selector — they're **three orthogonal knobs**. Confusing them is the #1 source of "wait, which guarantee do I have?".

| Dial | Scope | Values | Default | Decides |
|---|---|---|---|---|
| **`StorageMode`** | per **component type**, fixed at registration | `Versioned` · `SingleVersion` · `Transient` | `Versioned` | memory **layout** + whether MVCC/isolation exists at all |
| **`DurabilityDiscipline`** | per **transaction** (only on `SingleVersion` layout) | `TickFence` · `Commit` | `TickFence` | *how* a SingleVersion write becomes durable |
| **`DurabilityMode`** | per **UnitOfWork** | `Deferred` · `GroupCommit` · `Immediate` | `GroupCommit` (typical) | *when* the WAL is flushed to disk |

They compose freely. A `SingleVersion` component written under the `Commit` discipline inside an `Immediate` UoW is a real, valid combination — layout says "one in-place slot," discipline says "stage and make this write atomic + zero-loss," mode says "fsync before `Commit()` returns."

> 📌 **`Committed` is not a storage mode.** It's the `Commit` *discipline* applied to the byte-identical `SingleVersion` layout — a way to get atomic, zero-loss durability *without* paying for MVCC. See §8.

---

## 3. What happens on `Commit()`

```
Commit()
  │
  ├─ 1. validate + stage   (conflict checks; nothing visible yet)
  ├─ 2. append to WAL      (record in the commit buffer — still volatile)
  ├─ 3. PUBLISH            ← the change becomes VISIBLE to future readers here
  │
  └─ 4. flush/fsync        ← the change becomes DURABLE here
                             Immediate:  before Commit() returns
                             GroupCommit: within ~5 ms
                             Deferred:    only when you call Flush()
```

- **`Immediate`** — step 4 completes before `Commit()` returns. Durable ≈ visible. Zero loss.
- **`GroupCommit` / `Deferred`** — `Commit()` returns after step 3. The change is **visible now, durable later**.

`Commit()` returning is *not* a disk guarantee — except under `Immediate`. That's the trade you picked when you chose the mode.

---

## 4. Storage-mode guarantee matrix

The single authoritative table. `SingleVersion` appears twice because its **discipline** changes its durability and isolation without changing its layout.

| | **Versioned** | **SingleVersion**<br>(`TickFence`, default) | **SingleVersion**<br>(`Commit` discipline) | **Transient** |
|---|---|---|---|---|
| **Isolation** | snapshot isolation (consistent as-of your TSN) | none — reads see the latest in-place value | read-committed (staged writes hidden until commit) | none — latest value |
| **Visible to others before `Commit`** | no | yes, immediately | no (staged) | yes, immediately |
| **`Rollback` reverts the write** | yes | no (already happened in place) | yes — O(1), staging discarded | no |
| **Durability** | zero loss, full ACID | ≤ 1 tick loss (tick-fence WAL) | zero loss, atomic at `Commit` | none |
| **Survives a crash** | yes, to exact pre-crash state | yes, to the last completed tick | yes, to the committing transaction | no — gone |
| **MVCC / history** | revision chain (COW) | single slot, no history | single slot, no history | single slot, no history |
| **Concurrent writers** | conflict-detected | last-writer-wins | last-writer-wins | last-writer-wins (*you* own it) |
| **Write cost (Zen 4)** | ~250 ns | ~40 ns | ~40 ns + commit publish | ~40 ns |
| **How you select it** | `[Component(StorageMode = Versioned)]` (default) | `[Component(StorageMode = SingleVersion)]` | SingleVersion component + `uow.CreateTransaction(discipline: Commit)` — or `[Component(..., DefaultDiscipline = Commit)]` | `[Component(StorageMode = Transient)]` |

> ⚠️ **A transaction is a true ACID envelope only for the `Versioned` data it touches.** For `SingleVersion`/`Transient` *data*, a transaction still gives you thread affinity, atomic entity spawn/destroy, and a consistent snapshot of any *Versioned* components in the same archetype — but no isolation, rollback, or commit-timed durability on those components' values, unless you opt the write into the `Commit` discipline.

---

## 5. The contract, in plain language

1. **`Commit()` is instant and makes your change visible to future readers — but "visible" ≠ "safe on disk."** Under `Deferred`/`GroupCommit`, a committed change can be lost on crash until the WAL flushes.
2. **You choose the durability point, per UnitOfWork:** `Deferred` (durable only when you call `Flush()`), `GroupCommit` (durable within ~5 ms), `Immediate` (durable before `Commit()` returns). A single critical transaction can *escalate* to `Immediate` — never downgrade.
3. **Isolation is snapshot isolation, fixed at transaction start.** You see everything committed with `TSN ≤ yours`, plus your own uncommitted writes; you never see anyone else's uncommitted writes, nor any commit that landed after your transaction began. A fixed snapshot means **no dirty, non-repeatable, or phantom reads**; it is still weaker than serializable — the anomaly it allows is **write skew**.
4. **On crash you recover an atomic, durably-committed *prefix* of your transactions.** Each recovered transaction is all-or-nothing; what you lose is bounded exactly by your durability mode's window.
5. **Checkpointing is invisible to you.** It never changes what's durable or visible — it only reclaims WAL space, automatically.

---

## 6. Crash: what you keep, what you lose

| If you were using… | On crash you lose… |
|---|---|
| `Immediate` UoW | nothing — every returned `Commit()` is on disk |
| `GroupCommit` UoW | at most the last flush interval (~5 ms) of commits |
| `Deferred` UoW | everything committed since your last `Flush()` |
| `SingleVersion` (`TickFence`) component | at most the last tick's writes to that component |
| `SingleVersion` (`Commit` discipline) component | nothing — the committing transaction is durable |
| `Transient` component | all of it — never persisted |

Recovery replays the WAL and reconstructs every transaction whose commit record made it to disk, as an atomic unit. A transaction is either fully recovered or not present — never half-applied.

---

## 7. "I need… → use…"

| I need… | Choose |
|---|---|
| ACID state, snapshot reads, rollback (inventory, currency, score) | **`Versioned`** |
| Hot field overwritten every tick, OK to lose ≤1 tick (position, velocity, health) | **`SingleVersion`** (default `TickFence`) |
| A `SingleVersion` write that must be atomic + never lost, but I don't need MVCC (teleport, item pickup, currency debit) | `SingleVersion` + **`Commit` discipline** |
| Pure per-frame scratch that must *not* survive restart (targeting temporaries, input buffers) | **`Transient`** |
| Microsecond commits, a few ms of loss acceptable (general game/server tick) | **`GroupCommit`** |
| Never acknowledge a commit that isn't on disk (financial, irreversible) | **`Immediate`** |
| Flush once at the end of a bulk load | **`Deferred`**, then `Flush()` |
| Read millions of entities across all cores at one consistent snapshot | `PointInTimeAccessor` — see §8 & [ch.5](05-systems.md) |

---

## 8. Naming traps

Three names in this area mislead. Learn them once here.

- **`PointInTimeAccessor` is *not* time travel.** It is a **frozen *current* snapshot** used to fan a read-only pass across many worker threads at one TSN — the read engine behind parallel systems ([ch.5](05-systems.md)). "Point in time" means *one fixed current moment for all workers*, not reading historical versions. **There is no user-facing historical / as-of-past-version read API today.** (Snapshot isolation already gives you a consistent view *as of your transaction's start* — that is the only "as-of" you get.)

- **"Tick fence" names three related things.** In this guide it always means **the per-tick durability step** that batches dirty `SingleVersion` writes into the WAL (`dbe.WriteTickFence(n)`, run automatically by the runtime each tick). It is *not* a memory fence, and the parallel-execution machinery that speeds that step up (`RuntimeOptions.EnableParallelFence`) is an internal performance detail you never call.

- **`Committed` is a *discipline*, not a `StorageMode`.** There are exactly three storage modes (`Versioned`/`SingleVersion`/`Transient`). `Committed` is `DurabilityDiscipline.Commit` layered on the `SingleVersion` layout — see the `Commit` column in §4.

And the three dials one more time, because collapsing them is the root confusion:

- **`StorageMode`** = *layout* (design-time, per component).
- **`DurabilityDiscipline`** = *how* a SingleVersion write is made durable (per transaction).
- **`DurabilityMode`** = *when* the WAL flushes (per UnitOfWork).

---

## 9. Mini-glossary

| Term | Meaning (user's-eye view) |
|---|---|
| **TSN** | Transaction Sequence Number — the *visibility* clock. Your transaction's snapshot = "everything with TSN ≤ mine." |
| **LSN** | Log Sequence Number — the *durability* clock. A change is safe once its record's LSN ≤ `DurableLsn`. |
| **Snapshot isolation** | You read a consistent frozen view fixed at transaction start; readers never lock or wait. Prevents phantoms; weaker than serializable (permits write skew). |
| **UnitOfWork** | The durability boundary — owns the `DurabilityMode`, groups transactions into one flush cycle. |
| **Tick** | One simulation step under the runtime. One UoW per tick; many transactions per tick. |
| **Tick fence** | The per-tick step that makes dirty `SingleVersion` writes durable (≤1 tick loss). |
| **WAL** | Write-Ahead Log — the append-only file a commit is written to; fsync'ing it is what makes a commit durable. |
| **Checkpoint** | Background consolidation of WAL into the data file. Adds no durability; only recycles WAL space. |
| **DurableLsn** | Highest LSN actually fsync'd to stable media. |

---

## 🔗 See also

- **[Chapter 2 — Modeling](02-modeling.md):** choosing a `StorageMode` per component (the decision that matters most).
- **[Chapter 3 — Transactions](03-transactions.md):** the narrative version of this sheet — UoW vs Transaction, commit/rollback, durability modes.
- **[Chapter 5 — Systems](05-systems.md):** how the runtime drives one UoW per tick, the tick fence, and parallel reads.
- **Feature catalog — [Storage Modes](../feature-set/Ecs/storage-modes/README.md):** per-mode deep dives, tests, and the full feature matrix.
