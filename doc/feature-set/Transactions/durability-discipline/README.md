---
uid: feature-transactions-durability-discipline-index
title: 'SingleVersion Durability Discipline (TickFence / Commit)'
description: 'Per-transaction knob that picks how a SingleVersion write becomes durable, orthogonal to DurabilityMode.'
---

# SingleVersion Durability Discipline (TickFence / Commit)
> Per-transaction knob that picks how a `SingleVersion` write becomes durable, orthogonal to `DurabilityMode`.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Transactions](../README.md)

**Assumes:** [SingleVersion (Tick-Fence Durability)](../../Ecs/storage-modes/storage-mode-singleversion.md)

## 🎯 What it solves

`SingleVersion` components write in ~3ns (an in-place store) but are durable only at the next tick fence — up to
one tick of writes can be lost on crash. Some writes need atomicity and zero loss — a teleport, an item pickup, a
currency debit — without the snapshot isolation or AS-OF history a `Versioned` component provides. Routing those
writes through `Versioned` buys zero-loss durability at ~6x the write cost (a revision-chain allocation plus its
GC) for an isolation guarantee they never use. `DurabilityDiscipline` is a second, transaction-scoped knob that
makes a `SingleVersion` write commit-durable without changing the component's layout or paying that tax.

## ⚙️ How it works (in brief)

`DurabilityDiscipline { TickFence (default) | Commit }` is fixed when a transaction is created — via
`dbe.CreateQuickTransaction(discipline:)`, `uow.CreateTransaction(discipline:)`, or
`ctx.CreateSideTransaction(mode, discipline)` — and never changes for that transaction's lifetime. It is
orthogonal to `DurabilityMode`: `DurabilityMode` decides *when* a UoW's WAL records reach stable media,
`DurabilityDiscipline` decides *how* a `SingleVersion` write reaches the WAL in the first place. The two compose
freely — `Commit` discipline under a `Deferred` UoW still buffers its WAL record until `Flush()`. Discipline is
**uniform per transaction** (CM-02): a component declared `[Component(DefaultDiscipline = Commit)]` silently
escalates the whole transaction to `Commit` on first touch; once escalated, every `SingleVersion` write that
transaction makes is commit-staged.

## Sub-features

| Sub-feature | Use when | Write cost (Zen 4) | Loss window |
|-------------|----------|---------------------|--------------|
| [TickFence discipline (default)](./durability-discipline-tickfence.md) | High-frequency, loss-tolerant data (positions, AI state, anything the next tick re-derives) | ~3 ns | ≤ 1 tick |
| [Commit discipline (Variant-A staging)](./durability-discipline-commit.md) | Atomic, zero-loss writes that don't need snapshot isolation (teleport, item pickup, currency debit) | ~23 ns stage / ~65 ns publish | zero |

## ⚠️ Guarantees & limits

- Applies only to `StorageMode.SingleVersion` components — `Versioned` writes are already commit-scoped (no
  benefit from this knob), `Transient` is never durable (the knob is meaningless there).
- Discipline is fixed at transaction start; there is no API to change it mid-transaction (CM-02).
- Both disciplines give read-committed isolation, not snapshot isolation — a `SingleVersion` component (under
  either discipline) used with `ReadsSnapshot` fails loudly at scheduler `Build()` time (rule CM-04); use
  `Versioned` for snapshot/AS-OF reads.
- Composes with any `DurabilityMode` — discipline picks the write→WAL mechanism, mode still picks the flush
  timing.
- No revision chain is ever allocated for `SingleVersion` writes under either discipline; recovery replays both
  through the same `RecoveryApplier` slot upsert (LSN-ordered, last-writer-wins) — zero discipline-specific
  recovery code.

## 🧪 Tests

- [CommittedDisciplineTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/CommittedDisciplineTests.cs) —
  `DefaultDiscipline_Commit_EscalatesTransaction` covers the CM-02 uniform-per-transaction escalation rule that
  makes the two disciplines mutually exclusive within one transaction
- [StorageModeTickFenceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/StorageModeTickFenceTests.cs) — the
  default `TickFence` path this knob overrides

## 🔗 Related

- Sub-features: [TickFence discipline (default)](./durability-discipline-tickfence.md), [Commit discipline
  (Variant-A staging)](./durability-discipline-commit.md)
- Related feature: [Durability Modes](../durability-modes/README.md) — the orthogonal per-UoW axis that decides
  *when* WAL records reach stable media

<!-- Deep dive: claude/overview/02-execution.md — Durability Discipline (SingleVersion) (#durability-discipline-singleversion), claude/design/Ecs/committed-storage-mode.md -->
<!-- ADR: ADR-057 — Committed Durability Discipline — claude/adr/057-committed-durability-discipline.md -->
<!-- Rules: claude/design/Durability/MinimalWal/07-rules.md — module CM -->
