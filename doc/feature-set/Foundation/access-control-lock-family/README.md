# Reader-Writer & Resource Lifecycle Locks
> CAS-only, allocation-free spin-locks every concurrent engine structure embeds for shared/exclusive or lifecycle access.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](../README.md)

## 🎯 What it solves
Typhon's microsecond-level latency targets rule out OS-level synchronization (mutexes, semaphores, `ReaderWriterLockSlim`) for the thousands of small, short-lived locks the engine needs — page latches, B+Tree node locks, component-table guards, transaction-pool locks. Three purpose-built lock shapes cover the recurring access patterns the engine actually needs — general reader/writer, a compact reader/writer for high-density embedding, and a three-mode lifecycle lock for structures that grow while being read — without the cost of kernel objects or heap allocation.

## ⚙️ How it works (in brief)
All three pack their entire state into a single atomic word (8, 4, and 4 bytes respectively) and transition exclusively via `Interlocked.CompareExchange` — no wait queues, no kernel handles, no managed allocation. Contention spins via .NET's `SpinWait` (busy-spin → yield → sleep) and never parks a thread. Every blocking entry point takes a `ref WaitContext` (monotonic deadline + `CancellationToken`); pass `ref WaitContext.Null` for an infinite, zero-overhead wait. Picking the right one is a tradeoff between memory footprint, waiter fairness, and access semantics — see the sub-features below.

## Sub-features

| Sub-feature | Size | Use it when... |
|---|---|---|
| [AccessControl](./access-control.md) | 8 bytes | Few instances, many threads, writer starvation under read pressure is unacceptable |
| [AccessControlSmall](./access-control-small.md) | 4 bytes | Thousands of instances embedded per-node/per-page, fairness isn't needed |
| [ResourceAccessControl](./resource-access-control.md) | 4 bytes | Structural growth must not block concurrent readers; destruction must drain everything |

## ⚠️ Guarantees & limits
- All three are `internal` engine types — application code never constructs or calls them directly; they're the plumbing inside `ComponentTable`, `TransactionChain`, page latches, B+Tree nodes, segment chains, etc.
- Thread IDs are stored in exactly 16 bits everywhere (max 65,535) — consistent overflow headroom across the family, sized for 500+ core servers.
- Pure userspace spin-wait, never OS-level parking — cheap for the engine's typical sub-microsecond hold times, wasteful if misused for long critical sections.
- No deadlock detection by design: correctness rests on Typhon's MVCC (no cross-transaction lock holding) and strict latch-coupling order in the B+Tree/R-Tree, not on runtime detection in these primitives.
- Contention surfaces through the profiler's trace-event stream (`TyphonEvent.Emit*`, gated and JIT-eliminated when the profiler is off) — not an opt-in callback interface.

## 🧪 Tests
- [AccessControlTests](../../../../test/Typhon.Engine.Tests/Concurrency/AccessControlTests.cs) — full-featured RW lock: shared/exclusive/promote-demote, fairness under contention, deadline+cancellation wiring.
- [AccessControlSmallTests](../../../../test/Typhon.Engine.Tests/Concurrency/AccessControlSmallTests.cs) — compact RW lock: idle/shared/exclusive transitions, misuse-throws contract.
- [ResourceAccessControlTests](../../../../test/Typhon.Engine.Tests/Concurrency/ResourceAccessControlTests.cs) — 3-mode Accessing/Modify/Destroy semantics, `MODIFY_PENDING` fairness, promote/demote.

## 🔗 Related
- Sub-features: [AccessControl](./access-control.md), [AccessControlSmall](./access-control-small.md), [ResourceAccessControl](./resource-access-control.md)

<!-- Deep dive: claude/design/Foundation/Concurrency/AccessControlFamily.md, claude/overview/01-concurrency.md §1.3-1.5, claude/adr/031-unified-concurrency-patterns.md -->
