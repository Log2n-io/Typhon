# Transaction Creation Patterns
> Three ways to obtain a `Transaction`, depending on how long the work lives and whether it writes at all.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Transactions](../README.md)

## 🎯 What it solves

Every write or read against Typhon starts with a `Transaction`, but not every caller needs the same scaffolding
around it. A game tick wants one `UnitOfWork` shared by dozens of transactions with an explicit flush at the end; a
REPL command or a test wants a single transaction with zero ceremony; a query handler that never writes shouldn't
pay for a durability boundary it will never use. Picking the wrong pattern either adds boilerplate (manual UoW
management for a one-off write) or wastes a scarce resource (a UoW Registry slot held by a pure read). These three
entry points cover those cases without forcing every caller through the same full hierarchy.

## ⚙️ How it works (in brief)

All three patterns ultimately produce a `Transaction` pulled from the same pooled `TransactionChain`, so they share
identical MVCC semantics, TSN assignment, and auto-rollback-on-`Dispose` behavior. They differ only in *who owns the
`UnitOfWork`* — the durability boundary — and how much of it gets allocated. Standard creation gives the caller an
explicit `UnitOfWork` to reuse across many transactions. `CreateQuickTransaction` hides the `UnitOfWork` behind the
`Transaction` it returns, coupling their lifetimes. `CreateReadOnlyTransaction` skips the `UnitOfWork` entirely —
no durability mode, no registry slot, no `ChangeSet`.

## Sub-features

| Pattern | Entry point | UoW lifetime | Use when |
|---|---|---|---|
| [Standard](./transaction-creation-standard.md) | `dbe.CreateUnitOfWork(mode)` then `uow.CreateTransaction()` | Caller-managed, spans N transactions | Game tick, request batch, bulk import |
| [Quick transaction](./transaction-creation-quick.md) | `dbe.CreateQuickTransaction(mode)` | Disposed together with the one `Transaction` it owns | Single-shot write, REPL, tests |
| [Read-only transaction](./transaction-creation-readonly.md) | `dbe.CreateReadOnlyTransaction()` | None — no UoW is allocated | Snapshot reads, query dispatch |

## ⚠️ Guarantees & limits

- All three return the same `Transaction` type — there is no read-only-specific or quick-specific subclass; the
  difference is entirely in how (or whether) a `UnitOfWork` backs it.
- Every pattern is single-thread-affine and auto-rolls-back on `Dispose()` if not explicitly committed — see
  [Transaction Lifecycle, Thread Affinity & Pooling](../transaction-lifecycle-pooling.md) for the shared lifecycle
  guarantees that apply regardless of which pattern created the transaction.
- Choosing a pattern is a one-time decision per call site, not per transaction instance — there is no API to
  convert a `Transaction` from one pattern to another after creation (e.g., a read-only transaction can never start
  writing).
- `UnitOfWork` instances are not pooled; only `Transaction` instances are (`TransactionChain`, capacity 16).

## 🧪 Tests

- [UnitOfWorkTests](../../../../test/Typhon.Engine.Tests/Execution/UnitOfWorkTests.cs) — standard pattern
  (`UoW_CreateTransaction_ReturnsValidTx`, `UoW_MultipleTransactions_ShareIdentity`) and quick-transaction pattern
  (`QuickTx_*`) side by side
- [TransactionChainTests](../../../../test/Typhon.Engine.Tests/Data/TransactionChainTests.cs) — read-only pattern
  (`ReadOnly_*`): no `UnitOfWork`, throws on write, snapshot isolation

## 🔗 Related

- Related features: [Transaction Lifecycle, Thread Affinity &
  Pooling](../transaction-lifecycle-pooling.md) (full lifecycle guarantees), [Unit of
  Work](../unit-of-work.md) (the durability boundary itself), [Durability Modes](../durability-modes/README.md)
- Sub-features: [Standard (UnitOfWork + CreateTransaction)](./transaction-creation-standard.md),
  [CreateQuickTransaction](./transaction-creation-quick.md), [CreateReadOnlyTransaction](./transaction-creation-readonly.md)

<!-- Deep dive: claude/design/Transactions/transaction-overview.md §1 Three-Tier API Hierarchy (#1-three-tier-api-hierarchy) -->
<!-- Overview: claude/overview/02-execution.md §2.1 Unit of Work (#21-unit-of-work) -->
<!-- ADR: 001 — Three-Tier API Hierarchy — claude/adr/001-three-tier-api-hierarchy.md -->
