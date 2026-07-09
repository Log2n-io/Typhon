---
uid: feature-foundation-epoch-based-resource-protection
title: 'Epoch-Based Resource Protection'
description: 'Lock-free scheme that keeps in-flight cache pages alive with 2 obligations per transaction, not per-page ref-counting.'
---

# Epoch-Based Resource Protection
> Lock-free scheme that keeps in-flight cache pages alive with 2 obligations per transaction, not per-page ref-counting.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](./README.md)

## 🎯 What it solves

Every transaction reads and writes pages in Typhon's memory-mapped page cache while other threads concurrently evict cold pages to make room. The cache must never reclaim a page a live transaction is still touching, but tracking that safely can't cost a synchronized increment/decrement on every single page access — at microsecond transaction latencies, that overhead (and the leak risk of a missed release) is unacceptable. Epoch-based protection gives every transaction blanket protection for all pages it touches, for the cost of two cheap calls instead of one per page.

## ⚙️ How it works (in brief)

A transaction "enters" an epoch scope when it starts and "exits" when it ends; entering pins the engine's current global epoch counter to that thread. Every page the transaction touches gets stamped with that epoch value. The page cache computes the lowest epoch still pinned by any active thread and treats it as a watermark: pages stamped at or above the watermark are still possibly in use and are skipped during eviction; everything below it is fair game. Because protection is granted to the whole scope rather than per page, there is nothing to release page-by-page and nothing to leak — but a long-running transaction holds back eviction for *every* page touched since it started, not just its own.

## 💻 Usage

This is transparent engine plumbing — transactions enter/exit epoch scopes automatically, there's nothing to call directly:

```csharp
using var tx = dbe.CreateQuickTransaction();  // epoch scope entered here

EntityRef e = tx.OpenMut(entityId);
ref Position p = ref e.Write(Unit.Pos);       // pages touched are epoch-protected
p.X += 1f;
tx.Commit();                                  // epoch scope exited on tx dispose
```

The only thing application code can observe directly is read-only diagnostics on `DatabaseEngine.EpochManager`, useful for spotting a transaction that's holding the cache back from evicting:

```csharp
var epochs = dbe.EpochManager;
long behind = epochs.GlobalEpoch - epochs.MinActiveEpoch;  // large gap → a long-lived scope is stalling eviction
int liveThreads = epochs.ActiveSlotCount;
```

| Property | Meaning |
|----------|---------|
| `GlobalEpoch` | Current epoch counter, advances on every outermost scope exit. |
| `MinActiveEpoch` | Lowest epoch any active thread is pinned at — the eviction watermark. |
| `ActiveSlotCount` | Number of threads currently registered with the engine. |
| `IsCurrentThreadInScope` | Whether the calling thread is currently inside a scope. |

## ⚠️ Guarantees & limits

- **Constant cost regardless of pages touched** — one scope enter and one scope exit per transaction; a transaction touching 1 page or 10,000 pages pays the same protection overhead.
- **No leaks possible** — there is no per-page handle to forget to release; scope exit is the only release point.
- **Protection is by time window, not by identity** — a page is protected if its epoch overlaps *any* active scope, even one that never touched that page. Keep transactions/unit-of-work scopes short; a long-running one delays eviction of pages it never accessed.
- **Wait-free fast path** — entering/exiting a scope is a few nanoseconds (thread-local read/write plus one atomic increment), no contention with other threads in the common case.
- Page reclamation under heavy, sustained concurrency can lag further behind than a ref-counted scheme would, trading some extra cache pressure for the lock-free guarantee.

## 🧪 Tests
- [EpochManagerTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Concurrency/EpochManagerTests.cs) — enter/exit scope epoch advancement, nested scopes, `MinActiveEpoch` across multiple threads, thread-death slot reclamation, registry exhaustion.
- [EpochPageCacheTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/EpochPageCacheTests.cs) — epoch-tagged pages surviving eviction while a scope is active, eviction unblocked after scope exit.

## 🔗 Related

- Sibling: [Optimistic Lock Coupling (per-node concurrency)](../Indexing/olc-concurrency.md) — B+Tree/R-Tree lock-free reads rely on this epoch protection to keep pages alive.
- Sibling: [Reader-Writer & Resource Lifecycle Locks](./access-control-lock-family/README.md) — the complementary lock-based concurrency primitive family.

<!-- Deep dive: claude/design/Foundation/Concurrency/EpochSystem.md -->
<!-- ADR: claude/adr/033-epoch-based-page-eviction.md -->
<!-- Overview: claude/overview/01-concurrency.md §1.7 -->
