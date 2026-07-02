# AccessControlSmall (compact RW lock)
> 32-bit reader-writer lock for thousands of embedded per-node/per-page latches with short critical sections.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](../README.md)

## 🎯 What it solves
B+Tree nodes, page headers, archetype cluster slots — structures that exist by the thousands and each need their own lock, where an 8-byte `AccessControl` would waste memory and waiter fairness isn't worth the bookkeeping. AccessControlSmall trims the footprint to 4 bytes while keeping the same `WaitContext`-based timeout/cancellation contract as the rest of the family.

## ⚙️ How it works (in brief)
State is implicit rather than explicit: a non-zero thread-ID field means Exclusive, a non-zero 15-bit counter means Shared, both zero means Idle — so the common-case Idle→Exclusive transition is a single CAS against zero. There's no waiter tracking, so under sustained read pressure a pending writer can be starved (reach for `AccessControl` if that matters). Misuse — re-entering exclusive on the same thread, exiting from the wrong thread, promoting without holding shared, counter overflow — throws `InvalidOperationException` instead of silently corrupting state.

## 💻 Usage
```csharp
// Each B+Tree node / page / cluster slot embeds its own 4-byte latch
public struct BTreeNode
{
    public AccessControlSmall Latch;
    // ... keys, values, child pointers
}

// Short critical section, infinite wait (WaitContext.Null = zero overhead)
node.Latch.EnterSharedAccess(ref WaitContext.Null);
try { return ref node.Keys[index]; }
finally { node.Latch.ExitSharedAccess(); }

// Exclusive-only usage — a single state-machine guard, no readers
var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));
if (latch.EnterExclusiveAccess(ref ctx))
{
    try { /* mutate */ }
    finally { latch.ExitExclusiveAccess(); }
}

// Generic enter/exit when shared-vs-exclusive is decided at runtime
node.Latch.Enter(exclusive: true, ref ctx);
try { /* ... */ }
finally { node.Latch.Exit(exclusive: true); }
```

## ⚠️ Guarantees & limits
- 4 bytes per instance — designed to be embedded inline by the thousands (per-node, per-page).
- No waiter fairness: continuous shared acquisitions can starve a pending exclusive/promote.
- No re-entrancy: re-entering exclusive access on the same thread throws rather than deadlocking.
- Max 32,767 concurrent shared holders (15-bit counter); overflow throws `InvalidOperationException`.
- `WasContended` is a sticky diagnostic bit, cleared only by `Reset()`.
- `internal` type — engine plumbing, not callable from application code.

## 🧪 Tests
- [AccessControlSmallTests](../../../../test/Typhon.Engine.Tests/Concurrency/AccessControlSmallTests.cs) — idle/shared/exclusive transitions, misuse throws (exit without enter, wrong-thread exit), multi-thread blocking.

## 🔗 Related
- Parent feature: [Reader-Writer & Resource Lifecycle Locks](./README.md)
- Sibling: [AccessControl](./access-control.md) — full-featured RW lock with waiter fairness, for low-instance-count high-contention resources.
- Sibling: [ResourceAccessControl](./resource-access-control.md) — 3-mode lifecycle lock for structures where growth mustn't block readers.

<!-- Deep dive: claude/design/Foundation/Concurrency/AccessControlSmall.md -->
