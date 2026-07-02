# Side-Transactions for Immediate Durability
> Commit economy-critical writes durably mid-tick, independent of the tick's main UoW.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](./README.md)
**Assumes:** [Durability Modes (Deferred / GroupCommit / Immediate)](../Transactions/durability-modes/README.md)

## 🎯 What it solves

The tick's main UoW uses Deferred durability — a single WAL flush at tick end, so a crash mid-tick
loses the whole tick. That's fine for position or health (recomputed next tick), but not for a
trade, a purchase, or a loot drop: losing those is a player-visible, often irreversible, problem.
Side-transactions give a system a way to commit specific writes with stronger durability than the
tick around it, without restructuring the tick's UoW or slowing down everything else in it.

## ⚙️ How it works (in brief)

`ctx.CreateSideTransaction(...)` opens a new `Transaction` with its own durability mode, separate
from the system's main `ctx.Transaction`. It is short-lived: do the writes, `Commit()`, dispose.
Because it gets its own TSN, the calling system's main `Transaction` — whose snapshot was already
fixed — does not see the side-transaction's result; only systems that start later in the tick
(higher TSN) can. Side-transactions committed against each other (e.g. two trades in sequence) see
one another in commit order. The caller fully owns the side-transaction's lifecycle — the runtime
does not auto-commit or auto-dispose it the way it does for `ctx.Transaction`.

## 💻 Usage

```csharp
protected override void Execute(TickContext ctx)
{
    var buyer = ctx.Transaction.Open(trade.BuyerId);
    var seller = ctx.Transaction.Open(trade.SellerId);
    if (!ValidateTrade(buyer, seller, trade))
    {
        return;
    }

    using var tradeTx = ctx.CreateSideTransaction(DurabilityMode.Immediate);
    var buyerMut = tradeTx.OpenMut(trade.BuyerId);
    var sellerMut = tradeTx.OpenMut(trade.SellerId);

    ref var bw = ref buyerMut.Write(Player.Wallet);
    ref var sw = ref sellerMut.Write(Player.Wallet);
    bw.Gold -= trade.Price;
    sw.Gold += trade.Price;

    tradeTx.Commit(); // FUA WAL flush — durable now, even if the rest of the tick is lost
}
```

| Option | Default | Effect |
|---|---|---|
| `mode` (`DurabilityMode`) | `Immediate` | `Immediate` blocks `Commit()` until the WAL record is on stable media (~15-85µs); `Deferred`/`GroupCommit` are also accepted but defeat the point of a side-transaction |
| `discipline` (`DurabilityDiscipline`) | `TickFence` | Only matters for `SingleVersion`-layout components; pass `Commit` for atomic, zero-loss, no-revision-chain writes without full MVCC. No effect on `Versioned` components (always commit-scoped) |

## ⚠️ Guarantees & limits

- A committed side-transaction survives a crash even if the enclosing tick's UoW never flushes.
- The calling system's `ctx.Transaction` never observes a side-transaction's writes (its snapshot
  TSN was fixed before the side-tx committed) — use a [typed event queue](./typed-event-queues.md)
  to hand data to a later system in the same tick instead of relying on transaction visibility.
- Subsequent systems in the tick (new `Transaction`, higher TSN) can see committed side-tx writes,
  but this is incidental ordering, not a contract — don't depend on it across DAG reorderings.
- The caller must `Commit()` and dispose the side-transaction explicitly; nothing rolls it into the
  main tick's UoW lifecycle.
- `Immediate` durability costs ~15-85µs per commit (FUA) versus ~0.1ms for the *entire* tick's
  Deferred flush — use it only for the operations that need it, not as a default.
- Declare side-tx writes with `SystemBuilder.SideWrites<T>()` so tooling (Schema Inspector,
  Profiler) can report them; this declaration is informational only and does not affect scheduler
  ordering or add DAG edges.

## 🧪 Tests

- [SideTransactionTests](../../../test/Typhon.Engine.Tests/Runtime/SideTransactionTests.cs) — independent commit survives the enclosing tick, invisible to the calling system's own `ctx.Transaction`, caller owns lifecycle

## 🔗 Related

- Related feature: [Typed Event Queues](./typed-event-queues.md)
- Sibling: [Side-Transactions (Immediate Durability)](./tick-lifecycle/side-transactions.md) — same feature, separately written up under the tick-lifecycle grouping; not a duplicate to merge, kept for now.

<!-- Deep dive: claude/design/Runtime/01-tick-lifecycle.md §Immediate Side-Transactions -->
<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md §Q12 -->
