---
uid: feature-storage-page-allocation-occupancy
title: 'Page Allocation & Occupancy Tracking'
description: 'A 3-level bitmap that allocates and tracks every 8KB page in the database file, growing the file automatically as needed.'
---

# Page Allocation & Occupancy Tracking
> A 3-level bitmap that allocates and tracks every 8KB page in the database file, growing the file automatically as needed.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Storage](./README.md)

## 🎯 What it solves

Every persistent structure in Typhon — components, indexes, revision chains, clusters, spatial trees — is built from fixed-size 8KB file pages. Something has to answer "which pages are free?", hand out new ones without scanning the whole file, and grow the file transparently as data accumulates, all without ever risking two structures claiming the same page. Page Allocation & Occupancy Tracking is that foundation layer: every other storage feature (segments, chunk allocators) sits on top of it and never has to reason about raw file-page bookkeeping itself.

## ⚙️ How it works (in brief)

A bitmap with one bit per file page tracks allocation, organized in three levels. The bottom level (L0) is the durable ground truth — it lives on disk, in the occupancy segment's own pages. Two in-memory summary levels (L1/L2) let the allocator skip straight to a free run instead of scanning bit-by-bit, giving O(1) amortized allocate/free via `BitOperations.TrailingZeroCount`. When the bitmap itself runs out of room to track more pages, the occupancy segment grows by one page — using pages set aside in advance specifically to avoid a "need a free page to record more free pages" deadlock. A compact key/value bootstrap dictionary on the file's root page persists the occupancy segment's location and its growth-reserve page indices, so adding a new piece of root-level metadata is a new dictionary key, never a file-format revision.

## 💻 Usage

Allocation itself is an internal primitive — application code never calls `AllocatePage`/`FreePages` directly; every component table, index, and segment acquires pages through it transparently as you spawn entities and write components. The surface application code actually touches is read-only introspection over what the allocator has done:

```csharp
// Audit storage-level invariants — safe to call at any time, no data-page I/O.
var report = db.RunStorageIntegrityCheck();
if (!report.IsHealthy)
{
    foreach (var issue in report.Issues)
    {
        Console.WriteLine($"{issue.Kind} @ segment {issue.SegmentRootPageIndex}: {issue.Detail}");
    }
}

// Per-segment footprint: which pages each structure owns, and (for chunk-based
// segments) live allocated/free chunk counts.
foreach (var seg in db.EnumerateStorageSegments())
{
    Console.WriteLine($"{seg.Kind} root={seg.RootPageIndex} pages={seg.Pages.Length} " +
                       $"allocChunks={seg.AllocatedChunkCount} freeChunks={seg.FreeChunkCount}");
}
```

## ⚠️ Guarantees & limits

- One bit per page; O(1) amortized allocate/free via the L1/L2 in-memory skip levels — no full-bitmap scans on the happy path.
- The occupancy bitmap is treated as a *derived* structure on crash recovery: it is never trusted as-is, but rebuilt wholesale from actual segment ownership (rule CK-09) — a torn or stale bitmap page can't cause a double-allocation after restart.
- Growth is self-financing: the pages needed to extend the bitmap are reserved ahead of time, so growing the map never itself requires an allocation (no chicken-and-egg deadlock).
- Each additional occupancy page extends tracking capacity by 64,000 file pages (~500 MiB of file growth) — very large databases pay periodic, not continuous, growth pauses.
- Adding new root-level metadata (an SPI, a counter) is a new `BootstrapDictionary` key, not a file-format bump.
- Not directly callable by application code — `AllocatePage`/`FreePages` are internal allocator primitives consumed by segments; the only supported app-facing surface is read-only introspection (`RunStorageIntegrityCheck`, `EnumerateStorageSegments`).

## 🧪 Tests

- [BitmapL3FreeRangeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/BitmapL3FreeRangeTests.cs) — `ManagedPagedMMF.FreePages` contiguous-range bit-flip and subsequent reallocation
- [ManagedPagedMMFTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/ManagedPagedMMFTests.cs) — L0/L1 bitmap find/set primitives, occupancy map save/reload, map growth
- [BootstrapDictionaryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/BootstrapDictionaryTests.cs) — root-page key/value round-trip backing the occupancy segment's persisted bootstrap entries

## 🔗 Related

- Sibling: [Segment & Chunk-Based Allocation Engine](segment-chunk-allocation.md) — the multi-page/chunk allocator layered directly on top of this page-level bookkeeping

<!-- Deep dive: claude/design/Storage/database-file-format.md §3–4 (root header, bootstrap dictionary, occupancy map structure) -->
<!-- Overview: claude/overview/03-storage.md §3.2 (ManagedPagedMMF) -->
<!-- Rules: claude/rules/durability.md — CK-09 (occupancy bitmap is derived, re-derived on crash) -->
