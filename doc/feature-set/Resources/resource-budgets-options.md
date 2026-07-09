---
uid: feature-resources-resource-budgets-options
title: 'Resource Budget Configuration (ResourceOptions)'
description: 'Startup-time sizing of every fixed/growable resource limit, with a Validate() sanity check.'
---

# Resource Budget Configuration (ResourceOptions)
> Startup-time sizing of every fixed/growable resource limit, with a Validate() sanity check.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Resources](./README.md)

## 🎯 What it solves
Typhon's memory-bound components (page cache, WAL ring/segments, shadow buffer) must be sized
before the engine starts — there's no GC to grow them lazily, and getting it wrong either wastes
memory or causes runtime exhaustion under load. Applications need one place to declare these
limits, in domain units (pages, transactions, bytes), and a way to catch a misconfiguration —
fixed allocations that don't fit the declared memory budget — at startup instead of in production.

## ⚙️ How it works (in brief)
`ResourceOptions` is a plain settings object hung off `DatabaseEngineOptions.Resources`. Each
property maps to one bounded resource's limit (page cache pages, max active transactions, WAL ring
bytes, WAL segment count/size, shadow buffer pages, checkpoint thresholds) and ships with a sane
default. Components never see this object directly — each receives only its own limit at
construction. Calling `Validate()` sums the resources that are allocated immediately at startup
(page cache, WAL ring + segments, shadow buffer) and throws if that total exceeds
`TotalMemoryBudgetBytes`. Growable resources (active transaction count, index nodes, query
buffers) are intentionally excluded — they're bounded by runtime caps, not upfront allocation.

## 💻 Usage
```csharp
using Typhon.Engine;

// DatabaseEngine's constructor is internal — set the budget through the DI extension (see DI
// Registration & Wiring) or directly on DatabaseEngineOptions if you already have one.
services.AddDatabaseEngine(opt =>
{
    opt.Resources = new ResourceOptions
    {
        PageCachePages = 262144,              // 2 GB (8 KB/page)
        MaxActiveTransactions = 1000,
        WalRingBufferSizeBytes = 8 << 20,     // 8 MB
        WalMaxSegments = 4,
        WalMaxSegmentSizeBytes = 64L << 20,   // 64 MB each
        ShadowBufferPages = 512,              // 4 MB
        TotalMemoryBudgetBytes = 4L << 30,    // 4 GB ceiling for fixed allocations
    };

    // Throws InvalidOperationException if fixed allocations exceed TotalMemoryBudgetBytes.
    opt.Resources.Validate();
});
```

| Option | Default | Effect |
|---|---|---|
| `PageCachePages` | 256 (2 MB) | Page cache size; fixed at startup |
| `MaxPageCachePages` | 16384 (128 MB) | Reserved for future dynamic cache sizing; not enforced today |
| `MaxActiveTransactions` | 1000 | `CreateTransaction` throws `ResourceExhaustedException` beyond this |
| `TransactionPoolSize` | 16 | Pooled `Transaction` objects; pool miss allocates new (Degrade) |
| `WalRingBufferSizeBytes` | 8 MB | Commit threads block once the ring drains slower than it fills |
| `WalBackPressureThreshold` | 0.8 | Fraction of ring capacity where back-pressure kicks in |
| `WalMaxSegmentSizeBytes` / `WalMaxSegments` | 64 MB / 4 | WAL segment file sizing; exhausting all forces a checkpoint |
| `CheckpointMaxDirtyPages` | 10000 | Dirty-page threshold that forces an early checkpoint |
| `CheckpointIntervalMs` | 30000 | Idle checkpoint cadence |
| `ShadowBufferPages` | 512 (4 MB) | CoW backup buffer; writers block when full |
| `TotalMemoryBudgetBytes` | 4 GB | Ceiling checked by `Validate()` against fixed allocations |

## ⚠️ Guarantees & limits
- Set once at construction; there is no supported way to change `ResourceOptions` after the engine
  starts — resizing requires a restart.
- `Validate()` only checks **fixed** allocations (page cache + WAL ring + WAL segments + shadow
  buffer). Growable resources (transactions, index nodes, query buffers) are not part of the check
  — they're capped at runtime instead, so a passing `Validate()` is not a guarantee against all
  exhaustion.
- `CalculateFixedAllocationBytes()` / `CalculateAvailableBudgetBytes()` let you inspect the
  breakdown and headroom before or after `Validate()`.
- Each component receives only its own limit (constructor injection) — there is no way to read
  another component's budget back out of a live engine via this type.
- The exhaustion policy each limit triggers (FailFast, Wait, Evict, Degrade) is fixed per-component
  and not configurable here — see the resource graph's `ExhaustionPolicy` metadata for what happens
  when a given limit is hit.

## 🧪 Tests
- [ResourceOptionsTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/ResourceOptionsTests.cs) — defaults, `Validate()` pass/fail against
  the memory budget, `CalculateFixedAllocationBytes`/`CalculateAvailableBudgetBytes`, `DatabaseEngineOptions.Resources` wiring

## 🔗 Related
- Sibling: [Exhaustion Policy & ResourceExhaustedException](./exhaustion-policy-handling.md) — what happens when a configured limit is hit.
- Sibling: [DI Registration & Wiring](./resources-di-wiring.md) — where `ResourceOptions` is threaded into constructed services.
- Source: `src/Typhon.Engine/Resources/public/ResourceOptions.cs`

<!-- Deep dive: claude/design/Resources/07-budgets-exhaustion.md, claude/overview/08-resources.md §8.7 -->
