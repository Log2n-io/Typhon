# Side-Transactions (Immediate Durability)
> A transaction you open and commit from inside a tick system that becomes durable on its own, independent of whether the tick's main UoW ever flushes.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)
**Assumes:** [Durability Modes (Deferred / GroupCommit / Immediate)](../../Transactions/durability-modes/README.md)

## 🎯 What it solves

The tick's main UoW is Deferred — if the server crashes mid-tick, the whole tick's writes are lost (acceptable
for position/health, recalculated next tick). Economy-critical operations — trades, purchases, loot drops,
player progression — cannot tolerate that: a crash must never silently erase a completed trade. Side-
transactions give a system an escape hatch to commit specific writes with immediate, crash-safe durability
without forcing every other write in the tick to pay that cost.

## ⚙️ How it works (in brief)

`TickContext.CreateSideTransaction(DurabilityMode, DurabilityDiscipline)` opens a brand-new `Transaction` (with
its own `UnitOfWork`) on the current thread. It is short-lived by design: open it, do the writes, `Commit()`,
`Dispose()` — all within the same system callback. Because it commits independently with its own TSN, the
calling system's main `ctx.Transaction` (whose TSN was fixed when it was created) does NOT see the side-
transaction's writes — only systems that run later in the tick (new Transaction, higher TSN) can. Side-
transactions see each other's commits, in commit order.

## 💻 Usage

```csharp
void ProcessTrade(TickContext ctx, TradeRequest trade)
{
    // Validate using the system's main Transaction (read-only here).
    var buyer = ctx.Transaction.Open(trade.BuyerId);
    var seller = ctx.Transaction.Open(trade.SellerId);
    if (!ValidateTrade(buyer, seller, trade)) return;

    // Execute the trade in an Immediate side-transaction — durable the instant Commit() returns.
    using var tradeTx = ctx.CreateSideTransaction(DurabilityMode.Immediate);
    var buyerMut = tradeTx.OpenMut(trade.BuyerId);
    var sellerMut = tradeTx.OpenMut(trade.SellerId);
    ref Wallet bw = ref buyerMut.Write(Player.Wallet);
    ref Wallet sw = ref sellerMut.Write(Player.Wallet);
    bw.Gold -= trade.Price;
    sw.Gold += trade.Price;

    tradeTx.Commit();   // FUA WAL flush — durable now, independent of the tick's main UoW.
}
```

| Option | Default | Effect |
|---|---|---|
| `DurabilityMode` | `Immediate` | FUA on `Commit()` — blocks until the WAL record is on stable media (~15-85µs) |
| `DurabilityDiscipline` | `TickFence` | `SingleVersion` writes batch durability at the tick fence; pass `Commit` for atomic, zero-loss, commit-scoped writes (no revision chain) on `SingleVersion` components |

## ⚠️ Guarantees & limits

- Caller owns the returned `Transaction` — must `Commit()` + `Dispose()` it; unlike `ctx.Transaction`, the
  Runtime does not manage its lifecycle.
- Commits independently of the tick's main UoW — survives a crash even if the rest of the tick never flushes.
- NOT visible to the calling system's `ctx.Transaction` (snapshot isolation, its TSN is fixed at creation).
  Visible to systems that run later in the same tick. Use typed event queues for explicit same-tick
  coordination — don't rely on TSN ordering.
- Side-transactions see each other's commits, sequentially, in ascending-TSN order.
- Writes Versioned storage mode (full MVCC, WAL records) by default; pass `DurabilityDiscipline.Commit` for
  `SingleVersion`-layout components to get atomic, zero-loss commits without paying for a revision chain.
- No conflict with Patate scatter writes in the same tick — side-transactions write Versioned components,
  Patate scatter writes SingleVersion/Transient components in place; different storage paths, no overlap.
- Intended for short-lived, single-purpose commits, not as a substitute for the main tick Transaction's bulk
  entity operations.

## 🔗 Related

- Parent feature: [Tick Lifecycle & Transaction Management](./README.md)
- Sibling: [Side-Transactions for Immediate Durability](../side-transactions.md) — same feature, separately catalogued under the top-level Runtime list; not a duplicate to merge, kept for now.

<!-- Deep dive: claude/design/Runtime/01-tick-lifecycle.md §Durability Mode Coordination -->
