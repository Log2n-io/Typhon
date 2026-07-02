# Per-Transaction Durability Override
> Escalate one critical operation to zero-loss durability without raising the durability mode of the surrounding batch.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Transactions](../README.md)

## 🎯 What it solves

A `Deferred` or `GroupCommit` UoW is the right choice for the bulk of a workload, but a single operation inside
that batch — a rare item drop, a purchase — may need zero data-at-risk. Forcing the *entire* UoW to `Immediate`
would pay the ~15-85µs FUA cost on every commit in the batch, not just the one that matters. Per-transaction
escalation lets one operation get `Immediate`-grade durability while the rest of the batch keeps its cheaper
mode.

## ⚙️ How it works (in brief)

Escalation is scoped to a transaction-sized UoW, not a flag on a single `Commit()` call: you open a dedicated,
short-lived UoW with `DurabilityMode.Immediate` for just the critical operation, commit it, and continue the
surrounding `Deferred`/`GroupCommit` batch unaffected. From top-level code this is
`dbe.CreateQuickTransaction(DurabilityMode.Immediate)`; from inside a scheduled system — where you don't own a
top-level `Transaction` — `TickContext.CreateSideTransaction(DurabilityMode.Immediate)` does the same thing
without leaving the tick. Escalation only ever raises durability for the operation it wraps; it never changes
the mode of any other UoW.

## 💻 Usage

```csharp
using var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred);

// Fast batch — volatile until uow.Flush()
foreach (var mob in npcs)
{
    using var tx = uow.CreateTransaction();
    UpdateAI(tx, mob);
    tx.Commit();                  // ~1-2µs, buffered
}

// One critical operation mid-batch — escalate via its own UoW
using (var drop = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
{
    ref var inv = ref drop.OpenMut(playerId).Write(Player.Inventory);
    inv.Add(legendaryItem);
    drop.Commit();                // ~15-85µs — durable on return, independent of `uow`'s mode
}

await uow.FlushAsync();           // flush the remaining buffered batch
```

```csharp
// From inside a scheduled system — same escalation via the side-transaction idiom
void GrantRareDrop(ref TickContext ctx, EntityId playerId, ItemId item)
{
    using var side = ctx.CreateSideTransaction(DurabilityMode.Immediate);
    ref var inv = ref side.OpenMut(playerId).Write(Player.Inventory);
    inv.Add(item);
    side.Commit();
}
```

| Approach | Scope | Cost paid by | Use when |
|----------|-------|---------------|----------|
| Whole-UoW `Immediate` | every commit in the UoW | every transaction (~15-85µs each) | the entire batch needs zero loss |
| Dedicated escalation UoW / side-tx | one operation | just that operation | one rare critical operation inside a cheaper batch |

## ⚠️ Guarantees & limits

- Escalation only raises durability (`Deferred`/`GroupCommit` → `Immediate`); there is no mechanism to lower a
  UoW's mode for a single commit.
- The `DurabilityOverride` enum (`Default` / `Immediate`) is declared on the public API surface
  (`DurabilityMode.cs`) per ADR-005 as a future
  `Commit(DurabilityOverride)` parameter, but it is **not yet wired into `Transaction.Commit()`** — there is no
  single-call escalation today. Use a dedicated `Immediate`-mode UoW (`CreateQuickTransaction`) or
  side-transaction (`ctx.CreateSideTransaction`) for the escalated operation instead; both reuse the same
  WAL-writer signal/wait path the override was designed around.
- A `CreateQuickTransaction` UoW auto-disposes with its transaction, so the escalated operation's flush happens
  on `Dispose()` even if `Commit()` already returned.
- The escalated operation is its own atomic unit — it does not share a commit/rollback boundary with the
  surrounding batch's transactions.
- `Immediate` escalation can throw `CommitDurabilityUncertainException` if the FUA confirmation doesn't arrive
  before the deadline — the operation is already committed and MVCC-visible; treat this as "durability
  unconfirmed," not a rollback.

## 🧪 Tests

- [SideTransactionTests](../../../../test/Typhon.Engine.Tests/Runtime/SideTransactionTests.cs) — the
  `ctx.CreateSideTransaction(DurabilityMode.Immediate)` escalation idiom: independent commit, snapshot-isolation
  invisibility to the main tick transaction, caller-owned lifecycle

## 🔗 Related

- Parent feature: [Durability Modes](./README.md)
- Sibling: [CreateQuickTransaction](../transaction-creation-patterns/transaction-creation-quick.md) — the entry
  point this escalation idiom uses to open its dedicated `Immediate`-mode UoW

<!-- Deep dive: claude/overview/02-execution.md §2.2 Transaction Commit Path (#22-transaction-commit-path), claude/design/Transactions/05-unit-of-work.md §3 (#3-48--durabilitymode-enum--state-machine-types) -->
<!-- ADR: ADR-005 — Durability Mode Per Unit of Work — claude/adr/005-durability-mode-per-uow.md -->
