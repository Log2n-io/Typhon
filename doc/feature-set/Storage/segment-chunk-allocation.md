# Segment & Chunk-Based Allocation Engine
> Multi-page directories and fixed-size slot allocation — the substrate every component, index, and revision chain is built from.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Storage](./README.md)

## 🎯 What it solves

A single 8 KiB file page is too small to hold a component table, a B+Tree index, or a revision chain — these structures span thousands of pages and grow as data accumulates. Something has to stitch many pages into one addressable, growable structure, and — for structures that store many same-size records (components, index entries, revision slots) — hand out and reclaim fixed-size slots within that structure without a linear scan or false sharing under concurrent access. The segment/chunk allocation engine is that substrate: every higher-level storage feature (component tables, B+Tree indexes, VSBS, archetype clusters) is built on it rather than reinventing multi-page addressing and slot bookkeeping itself.

## ⚙️ How it works (in brief)

A **logical segment** is a directory of file pages: the root page holds up to 2,000 page-index entries (the database's directory-only v4 root format — the root carries no data of its own, only the directory), and a chain of extension pages picks up beyond that. Two independent linked lists run through the page headers — one through directory pages, one through data pages — so the structure can grow incrementally without ever moving existing pages. A **chunk-based segment** layers fixed-stride slot allocation on top: each page keeps a persisted occupancy bitmap (1 bit per slot, the durable ground truth), while an in-memory forward linked list tracks which pages still have free slots, so an allocator can skip straight to a page with room instead of scanning every page. A magic-multiplier division replaces the page-locating arithmetic's integer divide with a multiply+shift. Both segment types grow automatically (capped-doubling) when capacity runs out. A **chunk accessor** is the per-call helper that turns a chunk ID into a live pointer — it keeps a small SIMD-searchable cache of recently touched pages so repeated access to the same handful of pages (typical of B+Tree descents) avoids re-resolving the page on every call.

## 💻 Usage

This is internal allocator infrastructure — application code never constructs a segment or calls `AllocateChunk` directly. Every `ComponentTable`, B+Tree index, VSBS, and archetype cluster acquires its pages and chunks transparently as you spawn entities and write components. The supported application-facing surface is read-only introspection over what the allocator has built:

```csharp
// Per-segment footprint and live chunk-allocator stats for every chunk-based segment.
foreach (var seg in db.EnumerateStorageSegments().Where(s => s.IsChunkBased))
{
    Console.WriteLine(
        $"{seg.Kind} root={seg.RootPageIndex} stride={seg.Stride}B pages={seg.Pages.Length} " +
        $"capacity={seg.ChunkCapacity} used={seg.AllocatedChunkCount} free={seg.FreeChunkCount}");
}
```

## ⚠️ Guarantees & limits

- Chunk addressing is O(1): a magic-multiplier multiply+shift replaces the ~20–80 cycle integer divide that would otherwise be needed to locate a chunk's page.
- Allocation is lock-free and near-constant time: ~2–3 `Interlocked` operations per successful `AllocateChunk`, walking only pages known to have free slots.
- The per-page occupancy bitmap is the durable, persisted ground truth; the in-memory free-page list is rebuilt from it in O(pages) on database open — a crash can never leave allocator state that disagrees with what's on disk.
- Growth is capped-doubling (geometric while small, additive beyond 1,024 pages) so a single grow request never demands an unreasonably large page-allocation burst.
- Every segment spans at least 2 pages: the directory-only root carries no chunk data, so chunk 0 always lives on the first data page, not the root.
- Minimum chunk stride is 8 bytes; chunk 0 of every chunk-based segment is reserved as a sentinel and is never handed out by `AllocateChunk`.
- The chunk accessor's per-call page cache holds a bounded number of recently touched pages (SIMD-searched, clock-hand evicted) — it accelerates repeated access to the same pages within one call chain but is not a substitute for the page cache itself.
- Not directly callable by application code — segment/chunk allocation is an internal primitive consumed by component tables, indexes, VSBS, and clusters; the only supported app-facing surface is read-only introspection (`EnumerateStorageSegments`).

## 🧪 Tests

- [ChunkAccessorTests](../../../test/Typhon.Engine.Tests/Storage/ChunkAccessorTests.cs) — SIMD-searchable MRU page cache, clock-hand eviction, exclusive latch acquire/release
- [ChunkBasedSegmentBitmapL3Tests](../../../test/Typhon.Engine.Tests/Storage/ChunkBasedSegmentBitmapL3Tests.cs) — chunk allocate/free, L0/L1/L2 bitmap invariants, capacity growth
- [ManagedPagedMMFTests](../../../test/Typhon.Engine.Tests/Storage/ManagedPagedMMFTests.cs) — `LogicalSegmentGrowTest`: directory-only v4 root growing into a map-extension chain

## 🔗 Related

- Related feature: [Page Allocation & Occupancy Tracking](page-allocation-occupancy.md) (the file-page-level allocator segments are built on top of)

<!-- Deep dive: claude/design/Storage/PageCache/04-segments.md (LogicalSegment directory structure, growth, directory-only v4 root) -->
<!-- Deep dive: claude/design/Storage/PageCache/05-chunk-allocator.md (forward linked-list allocator, L0 bitmap, AllocateChunk/FreeChunk algorithms) -->
<!-- Reference: claude/design/Storage/database-file-format.md §5–6 (on-disk page layout for segments and chunks) -->
<!-- Reference: claude/design/Storage/StackChunkAccessor.md (notes the superseded design; points to the live ChunkAccessor<TStore>) -->
<!-- ADRs: claude/adr/008-chunk-based-segments.md (superseded), claude/adr/041-treiber-stack-chunk-allocator.md (superseded — documents the move away from the three-level bitmap, itself later replaced by the forward linked list), claude/adr/010-soa-simd-chunk-accessor.md (partially superseded — SOA+SIMD design rationale for the chunk accessor) -->
