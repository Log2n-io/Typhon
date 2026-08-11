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
construction. There is no overall memory budget and no manual validation call: the
`TotalMemoryBudgetBytes` property and the `Validate()` method were removed in #148 as vestigial
(they governed no allocation). Each wired knob is range-checked automatically at DI resolution by
`DatabaseEngineOptionsValidator`.

## 💻 Usage
```csharp
using Typhon.Engine;

// DatabaseEngine's constructor is internal — set the budget through the DI extension (see DI
// Registration & Wiring) or directly on DatabaseEngineOptions if you already have one.
services.AddDatabaseEngine(opt =>
{
    opt.Resources = new ResourceOptions
    {
        MaxActiveTransactions       = 1000,
        WalRingBufferSizeBytes      = 64 << 20,   // 64 MB (default — 2 × 32 MB halves)
        PageChecksumVerification    = PageChecksumVerification.OnLoad,
        CheckpointIntervalMs        = 30_000,
        CheckpointBarrierTimeoutMs  = 30_000,
    };

    // No Validate() call — every knob above is range-checked at DI resolution.
});

// Page-cache size is NOT a ResourceOptions knob — it lives on the paged store:
services.AddManagedPagedMMF(o => o.DatabaseCacheSize = 512UL << 20);   // 512 MiB
```

| Option | Default | Effect |
|---|---|---|
| `MaxActiveTransactions` | 1000 | `CreateTransaction` throws `ResourceExhaustedException` beyond this |
| `WalRingBufferSizeBytes` | 64 MB | Total pinned; 2 × 32 MB halves. Commit threads block once the ring drains slower than it fills. Sized for tail latency — lower for memory-constrained deployments |
| `PageChecksumVerification` | `OnLoad` | CRC every page load · only during recovery · recovery-suspect mode |
| `CheckpointIntervalMs` | 30000 | Idle checkpoint cadence |
| `CheckpointBarrierTimeoutMs` | 30000 | How long a checkpoint waits for its barrier before giving up |

That is the entire type. #148 removed `PageCachePages`, `MaxPageCachePages`, `TransactionPoolSize`,
`WalBackPressureThreshold`, `WalMaxSegmentSizeBytes`, `WalMaxSegments`, `CheckpointMaxDirtyPages` and
`ShadowBufferPages` as vestigial — nothing read them. Page-cache size is
`PagedMMFOptions.DatabaseCacheSize` (default 256 MiB); WAL segment sizing and group-commit cadence are
`WalWriterOptions`; the transaction pool is a `const 16` in `TransactionChain`, not a knob.

## ⚠️ Guarantees & limits
- Set once at construction; there is no supported way to change `ResourceOptions` after the engine
  starts — resizing requires a restart.
- Validation is **range-checking, not budgeting**. `DatabaseEngineOptionsValidator` rejects a
  non-positive `MaxActiveTransactions`, `WalRingBufferSizeBytes`, `CheckpointIntervalMs` or
  `CheckpointBarrierTimeoutMs` (and the wired `WalWriterOptions` sizes) at DI resolution. Nothing
  sums your allocations against a memory ceiling — a configuration that passes can still ask for
  more RAM than the machine has.
- There is no `Validate()`, `CalculateFixedAllocationBytes()` or `CalculateAvailableBudgetBytes()`
  to call — all three went with #148's purge.
- Each component receives only its own limit (constructor injection) — there is no way to read
  another component's budget back out of a live engine via this type.
- The exhaustion policy each limit triggers (FailFast, Wait, Evict, Degrade) is fixed per-component
  and not configurable here — see the resource graph's `ExhaustionPolicy` metadata for what happens
  when a given limit is hit.

## 🧪 Tests
- [ResourceOptionsTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/ResourceOptionsTests.cs) — the shipped defaults, plus the
  `ExhaustionPolicy` / `ResourceExhaustedException` surface. The range-checking itself lives in
  [Options Validation](../Hosting/engine-options-configuration/options-validation.md)

## 🔗 Related
- Sibling: [Exhaustion Policy & ResourceExhaustedException](./exhaustion-policy-handling.md) — what happens when a configured limit is hit.
- Sibling: [DI Registration & Wiring](./resources-di-wiring.md) — where `ResourceOptions` is threaded into constructed services.
- Source: `src/Typhon.Engine/Resources/public/ResourceOptions.cs`

<!-- Deep dive: claude/design/Resources/07-budgets-exhaustion.md, claude/overview/08-resources.md §8.7 -->
