---
uid: feature-foundation-access-control-lock-family-access-control
title: 'AccessControl (full-featured RW lock)'
description: '64-bit reader-writer lock with waiter fairness, in-place promotion, and contention tracking.'
---

# AccessControl (full-featured RW lock)
> 64-bit reader-writer lock with waiter fairness, in-place promotion, and contention tracking.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](../README.md)

## 🎯 What it solves
Some engine structures are touched by many threads but exist as a single shared instance — the transaction pool's central lock, a page cache's occupancy map, a segment's buffer lock. For these, raw lock size doesn't matter, but starving a writer under continuous read pressure does. AccessControl gives that handful of high-traffic, low-instance-count structures a true reader-writer lock with fairness guarantees and built-in contention visibility, without the overhead of .NET's `ReaderWriterLockSlim`.

## ⚙️ How it works (in brief)
The entire lock state — mode, shared-reader count, three waiter counters, the exclusive holder's thread ID, a sticky contention flag — packs into one 64-bit word, mutated only via CAS. A **writer-preferring "class of waiters" fairness policy** blocks new shared (read) acquisitions whenever an exclusive or promoter waiter exists, so continuous readers can never starve a pending writer indefinitely — though there's no strict FIFO ordering within a waiter class. A thread holding Shared can call `TryPromoteToExclusiveAccess` to upgrade in place (no release-then-reacquire window) when it's the sole reader; `DemoteFromExclusiveAccess` reverses it.

## 💻 Usage
```csharp
private AccessControl _control;

// Shared (read) access, bounded wait
var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(100));
if (_control.EnterSharedAccess(ref ctx))
{
    try { /* read */ }
    finally { _control.ExitSharedAccess(); }
}

// Exclusive (write) access, infinite wait — WaitContext.Null costs nothing extra
_control.EnterExclusiveAccess(ref WaitContext.Null);
try { /* write */ }
finally { _control.ExitExclusiveAccess(); }

// Read, then upgrade in place only if needed
if (_control.EnterSharedAccess(ref ctx))
{
    try
    {
        if (NeedsUpdate() && _control.TryPromoteToExclusiveAccess(ref ctx))
        {
            try { ApplyUpdate(); }
            finally { _control.DemoteFromExclusiveAccess(); }
        }
    }
    finally { _control.ExitSharedAccess(); }
}
```

| Acquisition | Method | Blocking |
|---|---|---|
| Shared (read) | `EnterSharedAccess(ref WaitContext)` / `TryEnterSharedAccess()` | Waits / non-blocking |
| Exclusive (write) | `EnterExclusiveAccess(ref WaitContext)` / `TryEnterExclusiveAccess()` | Waits / non-blocking |
| Promote shared → exclusive | `TryPromoteToExclusiveAccess(ref WaitContext)` | Waits; fails if other readers are present |
| Demote exclusive → shared | `DemoteFromExclusiveAccess()` | Never blocks |

## ⚠️ Guarantees & limits
- 8 bytes per instance — reserve for low-instance-count, high-contention resources; use `AccessControlSmall` for per-node/per-page locks numbering in the thousands.
- Writer-preferring fairness via three sticky waiter counters; priority order is Promoter > Exclusive > Shared.
- Max 255 concurrent shared holders and max 255 waiters per class (8-bit counters); overflow is a `Debug.Assert` in this type (compare `AccessControlSmall`, which throws on overflow).
- `WasContended` is a sticky diagnostic bit, cleared only by `Reset()`.
- `internal` type — engine plumbing, not callable from application code.

## 🧪 Tests
- [AccessControlTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Concurrency/AccessControlTests.cs) — shared/exclusive acquisition, in-place promote/demote, timeout and cancellation paths, `WasContended` sticky bit, multi-thread blocking/contention.

## 🔗 Related
- Parent feature: [Reader-Writer & Resource Lifecycle Locks](./README.md)
- Sibling: [AccessControlSmall](./access-control-small.md) — compact 4-byte variant for thousands of embedded per-node/per-page latches.
- Sibling: [ResourceAccessControl](./resource-access-control.md) — 3-mode lifecycle lock for structures where growth mustn't block readers.

<!-- Deep dive: claude/design/Foundation/Concurrency/AccessControl.md, claude/adr/017-64bit-access-control-state.md, claude/adr/018-adaptive-spin-wait.md -->
