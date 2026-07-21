---
uid: feature-storage-epoch-dirty-tracking
title: 'Epoch-Based Page Protection & Dirty-Page Tracking'
description: 'Per-page eviction safety built from an epoch tag plus three counters, replacing per-page reference counting entirely.'
---

# Epoch-Based Page Protection & Dirty-Page Tracking
> Per-page eviction safety built from an epoch tag plus three counters, replacing per-page reference counting entirely.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Storage](./README.md)

## 🎯 What it solves

The page cache must never reclaim a memory page that a live reader still has a raw pointer into, that has modifications not yet durable on disk, or that a B+Tree write is mid-flight on — but the *general* "don't evict what's in use" problem is already solved by [Epoch-Based Resource Protection](../Foundation/epoch-based-resource-protection.md). What's specific to the page cache is everything epoch scoping alone can't express: a page can still be "in use" after the transaction that wrote it has exited its scope (it's dirty and not yet checkpointed), or while a B+Tree's lock-free write is in flight on it (no exclusive latch is held), or while a `ChunkAccessor` slot holds a cached raw pointer into it across an eviction. Each of these needs its own protection signal layered on top of the epoch tag.

## ⚙️ How it works (in brief)

Every cached memory page carries an `AccessEpoch` tag (stamped to the current epoch on each access, via compare-and-swap so it only ever increases) plus three independent counters: `DirtyCounter` (modifications pending checkpoint write-back), `ActiveChunkWriters` (in-flight lock-free B+Tree writes, which checkpoint's snapshot must not race), and `SlotRefCount` (live `ChunkAccessor` slot references to the page's raw address). A page is only eligible for clock-sweep eviction when all four say it's safe: epoch stale, no dirty marks, no active writers, no slot references. Dirty marks are produced by a `ChangeSet` attached to a unit of work — every mutated page is registered exactly once, the mark count is conservation-tracked (so concurrent unit-of-work scopes touching the same page never under- or over-release it), and disposal drains the UoW's marks back to the one outstanding mark the next checkpoint cycle needs to find and clear by writing the page.

## 💻 Usage

This is transparent engine plumbing — every component write, index update, and B+Tree mutation registers its dirty pages and exits its epoch scope automatically. There is no API to mark a page dirty or to pin it directly:

```csharp
using var uow = db.CreateUnitOfWork();            // owns the ChangeSet for this scope
using var tx = uow.CreateTransaction();           // epoch scope entered here

EntityRef e = tx.OpenMut(entityId);
ref Position p = ref e.Write<Position>();         // touched pages: epoch-tagged + DirtyCounter++
p.X += 1f;
tx.Commit();                                      // epoch scope exited; dirty marks released to 1
                                                   // (checkpoint writes the page later, DirtyCounter -> 0)
```

The one place application code interacts with this layer is sizing the cache and handling the failure mode when it's undersized — a single transaction's working set must fit within `DatabaseCacheSize`, since every page it touches is epoch-protected for the scope's whole lifetime:

```csharp
services.AddScopedManagedPagedMemoryMappedFile(options =>
{
    options.DatabaseCacheSize = 64UL * 1024 * 1024;   // 8192 pages @ 8KB; size above the largest expected UoW working set
});

try
{
    BulkInsertThousandsOfEntities(db);
}
catch (PageCacheBackpressureTimeoutException ex)
{
    // No evictable page after waiting TimeoutOptions.Current.PageCacheBackpressureTimeout (default 5s).
    // ex.DirtyPageCount / ex.EpochProtectedCount say which protection is holding pages back.
    // Fix: enlarge DatabaseCacheSize, or split the transaction into smaller batches.
}
```

| Option | Default | Effect |
|--------|---------|--------|
| `PagedMMFOptions.DatabaseCacheSize` (or `TyphonOptions.PageCacheSize`) | 256 MiB | Total page-cache budget (bytes); must exceed the largest single transaction's unique-page working set. 8 MiB minimum; below a 64 MiB recommended floor logs a warning |
| `TimeoutOptions.Current.PageCacheBackpressureTimeout` | 5s | How long allocation waits for a page to become evictable before throwing |

## ⚠️ Guarantees & limits

- **A clean, epoch-stale, unreferenced page is always reclaimable** — eviction never blocks on anything but these four signals; there's no separate "this page is special" escape hatch to reason about.
- **Dirty data is never evicted before it's durable** — `DirtyCounter` only reaches zero after the checkpoint has actually written the page; rollback and crash-recovery paths cannot make the count go negative.
- **In-flight lock-free B+Tree writes are checkpoint-safe** — `ActiveChunkWriters` blocks the checkpoint's page snapshot, not eviction; it exists because optimistic B+Tree writes don't take the page's exclusive latch the way ordinary writes do.
- **Raw pointers stay valid across deferred slot eviction** — `SlotRefCount` protects a page for as long as any `ChunkAccessor` slot — even one logically evicted from its warm cache — might still be dereferenced through a cached `byte*`/`ref T`.
- **Known limitation — working set must fit the cache**: because protection is granted for the whole epoch scope, a transaction touching more unique pages than the cache holds cannot proceed — every page it touches becomes unevictable by its own epoch tag, a circular dependency with no automatic resolution. Size `DatabaseCacheSize` above the largest expected transaction's page footprint.
- No per-page synchronized increment/decrement on the read path — only writes register dirty marks; the epoch tag (shared with the underlying scope mechanism) is already paid for once per transaction.

## 🧪 Tests

- [EpochPageCacheTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/EpochPageCacheTests.cs) — epoch-tagged page stays protected while a scope is active, becomes evictable only after scope exit
- [ChangeSetConservationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/ChangeSetConservationTests.cs) — `DirtyCounter` conservation across add/release/reset/checkpoint-ack, never over- or under-releases
- [CheckpointManagerTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CheckpointManagerTests.cs) — `ActiveChunkWriters` coverage gate blocks checkpoint capture on a pinned page and unblocks once released

## 🔗 Related

- Related feature: [Epoch-Based Resource Protection](../Foundation/epoch-based-resource-protection.md) (the underlying scope mechanism), [Page Allocation & Occupancy Tracking](./page-allocation-occupancy.md) (the layer below, file-page bookkeeping)

<!-- Deep dive: claude/design/Storage/PageCache/02-page-states.md, claude/design/Storage/PageCache/06-changesets.md, claude/design/Storage/PageCache/07-concurrency.md -->
<!-- ADR: claude/adr/033-epoch-based-page-eviction.md -->
<!-- Rules: claude/rules/durability.md — module Page Safety (PS-01..PS-09) -->
