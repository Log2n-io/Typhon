---
uid: feature-querying-temporal-queries
title: 'Temporal Queries (Point-in-Time Read & Revision History)'
description: 'Opt-in per-component history retention enabling reads of past state and full revision timelines.'
---

# Temporal Queries (Point-in-Time Read & Revision History)
> Opt-in per-component history retention enabling reads of past state and full revision timelines.

**Status:** 📋 Planned · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Querying](./README.md)

## 🎯 What it solves

MVCC keeps multiple revisions of a component alive only as long as some active transaction can still see them — as soon as nothing needs an old revision, it's reclaimed. There is no way today to ask "what was this entity's position 5 seconds ago?" or "show me the full edit history of this trade record." Use cases like gameplay replay/rewind, undo-redo, audit trails, and debugging "how did we get into this state" all need state that normal MVCC visibility intentionally discards.

## ⚙️ How it works (in brief)

You opt a component into retention with `[TemporalRetention(KeepCount = N)]` (or the runtime equivalent); components without it are unaffected and behave exactly as today. Retained components keep their last N committed revisions instead of discarding them as soon as MVCC visibility allows, and two read APIs work against that history: a point-in-time read for a specific transaction sequence number, and a full chronological enumeration. Both APIs are callable on any component, but only return useful results once retention is configured — without it, history is gone before you can query it.

## 💻 Usage

```csharp
// Illustrative only — design complete, not implemented yet.
// [Component]
// [TemporalRetention(KeepCount = 50)]
// public struct PlayerPosition
// {
//     [Field] public float X;
//     [Field] public float Y;
//     [Field] public float Z;
// }
//
// // Point-in-time read
// if (tx.ReadEntityAtVersion<PlayerPosition>(entityId, targetTSN, out var pastPos))
// {
//     Rewind(pastPos);
// }
//
// // Full revision history, oldest to newest
// foreach (var snapshot in tx.GetRevisionHistory<PlayerPosition>(entityId))
// {
//     Console.WriteLine($"TSN {snapshot.TSN}: {snapshot.Component}");
// }
```

| Option | Default | Effect |
|---|---|---|
| `TemporalRetention.KeepCount` | none (opt-in) | Keeps the N most recent committed revisions per entity |
| `TemporalRetention.KeepFor` / `MaxRevisions` | none (Phase 3) | Time-windowed retention with optional hard cap — deferred, needs a TSN↔DateTime mapping table |
| `dbe.ConfigureRetention<T>(...)` | n/a | Runtime override of the attribute-declared policy |

## ⚠️ Guarantees & limits

- **Not implemented.** No code under `src/Typhon.Engine/Querying` or elsewhere defines `[TemporalRetention]`, `ReadEntityAtVersion`, or `GetRevisionHistory` today; this is a complete design awaiting a build slot.
- **Opt-in, per component type.** Components without `[TemporalRetention]` keep today's lightweight revision chain — zero structural or performance overhead.
- **Not microsecond-latency.** Designed for ~150ns-1.2µs point-in-time reads (chain-length dependent) and multi-microsecond periodic compaction — a different, slower tier than normal MVCC reads (~100ns), by design.
- **`KeepCount(N)` only at first ship.** Time-based `KeepFor` retention is deferred to a later phase pending a TSN↔DateTime mapping table; an approximate tick-rate conversion was explicitly rejected because GC is destructive and irreversible.
- **Retention never overrides active-transaction visibility.** An open transaction's snapshot is never pruned out from under it, regardless of retention policy — retention can only extend the garbage-collection window, never shrink it.
- **Page cache cost is real.** Each retained revision consumes page cache pages; e.g. `KeepCount(50)` across 10K entities is estimated at ~7.5 MB / ~940 pages — size your cache budget accordingly.
- **Universal API, conditional usefulness.** `ReadEntityAtVersion`/`GetRevisionHistory` compile and run against any component, but without retention configured they reliably miss (history already reclaimed).

## 🔗 Related

- Related feature: [Persistent Views](./persistent-views.md) — a different live-data mechanism (current-state delta tracking, not history)
- Sibling: [Temporal (Point-in-Time) Index Query](../Indexing/temporal-index-query.md) — the index-side counterpart reconstructing past key membership from version history

<!-- Deep dive: claude/design/Querying/temporal-queries.md — full design (retention model, chunk directory layout, compaction algorithm, phased implementation plan) -->
<!-- Research: claude/research/Querying/TemporalQueries.md -->
<!-- ADR: claude/adr/003-mvcc-snapshot-isolation.md, claude/adr/023-circular-buffer-revision-chains.md -->
