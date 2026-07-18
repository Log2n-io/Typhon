---
uid: feature-storage-page-cache
title: 'Memory-Mapped Page Cache & Clock-Sweep Eviction'
description: 'The 8 KiB page cache underneath every Typhon structure — clock-sweep eviction and async I/O instead of trusting the OS, with backpressure instead of…'
---

# Memory-Mapped Page Cache & Clock-Sweep Eviction
> The 8 KiB page cache underneath every Typhon structure — clock-sweep eviction and async I/O instead of trusting the OS, with backpressure instead of crashing when full.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Storage](./README.md)

## 🎯 What it solves

Every persistent structure in Typhon — components, indexes, revision chains, segments — is built from fixed-size 8 KiB pages backed by a memory-mapped file. Keeping every page resident would blow past available RAM on any real database; relying on the OS to page things in and out naively gives no control over what gets evicted under pressure, no way to guarantee a page a thread is mid-read on stays put, and no async I/O path. The page cache is the layer every other storage feature is built on: it decides which pages are resident, picks what to evict when full, and never lets a page disappear out from under a thread still using it.

## ⚙️ How it works (in brief)

The cache holds a fixed number of 8192-byte slots in natively-addressed, memory-mapped memory. Each slot moves through a 4-state lifecycle — `Free` → `Allocating` (loading from disk) → `Idle` (resident, readable) → `Exclusive` (single-writer latch) → back to `Idle`. When a slot is needed and none is free, clock-sweep eviction scans slots in circular order: each slot carries a 0–5 "second-chance" counter, bumped on access and decremented as the sweep passes over it, so a slot is only taken once its counter has decayed to 0 *and* it's otherwise unprotected (not dirty, not actively read). Allocating the next file page adjacent to the previous one is tried first, so sequential access lands in contiguous cache memory and several small I/Os batch into one. Reads and writes use async `RandomAccess` I/O instead of blocking the calling thread. If every slot is pinned when a new one is needed, the allocator blocks and waits (triggering a flush) rather than failing outright — up to a configurable timeout.

## 💻 Usage

The cache itself is internal plumbing — application code never calls into it directly. What you control is its size and the timeouts around it, at engine startup:

```csharp
services.AddTyphon(o => o
    .DatabaseFile("GameWorld.typhon")
    .ConfigureStorage(s => s.DatabaseCacheSize = 64 * 1024 * 1024)              // 64 MiB = 8192 pages
    .ConfigureEngine(e => e.Timeouts.PageCacheBackpressureTimeout = TimeSpan.FromSeconds(10)));

var dbe = services.BuildServiceProvider().GetRequiredService<DatabaseEngine>();

try
{
    // normal ECS/transaction work — the cache is transparent below this layer
}
catch (PageCacheBackpressureTimeoutException ex)
{
    // every page was dirty or actively in use; the working set didn't fit the cache
    log.LogWarning(ex, "page cache backpressure: {Dirty} dirty, {Pinned} pinned",
        ex.DirtyPageCount, ex.EpochProtectedCount);
}
```

| Option | Default | Effect |
|---|---|---|
| `PagedMMFOptions.DatabaseCacheSize` (via `AddManagedPagedMMF`, or the fluent `TyphonOptions.PageCacheSize`) | **256 MiB** default | Total cache capacity; must be a multiple of 8 KiB, between 2 MiB and 4 GiB. Below the 64 MiB recommended floor logs a warning (unless the internal `TestMode` is set) |
| `TimeoutOptions.PageCacheBackpressureTimeout` | 5 s | Max wait for a page to free up before `PageCacheBackpressureTimeoutException` |
| `TimeoutOptions.PageCacheLockTimeout` | 5 s | Max wait on a page state-transition lock |

## ⚠️ Guarantees & limits

- A page actively being read or written can never be evicted out from under the caller — eviction only ever takes unprotected, clean slots.
- Eviction is O(1) amortized: clock-sweep finds a candidate within a couple of sweeps at most, no per-access list maintenance.
- The second-chance counter (cap 5) gives hot pages (e.g. B+Tree roots) natural protection without manual pinning or priority tuning — a page can survive up to 5 sweeps before becoming evictable.
- Sequential file-page access tends to land in contiguous cache memory, batching what would otherwise be many small I/Os into one.
- Hard constraint: a single transaction's *working set* (distinct pages touched) must fit within the configured cache. If it doesn't, every resident page ends up pinned by that one transaction and allocation deadlocks until `PageCacheBackpressureTimeout` fires. Size the cache to the largest expected transaction, not the average.
- Cache size is fixed for the life of the open database (2 MiB–4 GiB, multiple of 8 KiB) — no dynamic resizing while running.

## 🧪 Tests

- [PagedMMFTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/PagedMMFTests.cs) — `SequentialWrites` (contiguous mem-pages batch into a single disk write), `ReliabilityTest` (concurrent read/write under deliberate cache pressure, forcing repeated clock-sweep eviction)

## 🔗 Related

- Sibling: [Epoch-Based Page Protection & Dirty-Page Tracking](epoch-dirty-tracking.md) — the four eviction-safety signals layered on top of this cache
- Sibling: [Page Integrity — CRC32C, Seqlock Snapshots & A/B Page Pairing](page-integrity.md) — CRC verification runs on every cold page load into this cache

<!-- Overview: claude/overview/03-storage.md §3.1 (PagedMMF cache architecture) -->
<!-- Design: claude/design/Storage/PageCache/02-page-states.md (state machine, eviction predicate) -->
<!-- Design: claude/design/Storage/PageCache/03-clock-sweep.md (clock-sweep algorithm in detail) -->
<!-- ADRs: claude/adr/006-8kb-page-size.md, claude/adr/007-clock-sweep-eviction.md, claude/adr/009-pinned-memory-unsafe-code.md -->
