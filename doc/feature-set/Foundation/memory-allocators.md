# Memory Allocators
> Pinned/unmanaged memory primitives every page cache, segment, and hash-map layers on for stable, GC-immune addresses.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](./README.md)

## 🎯 What it solves

Typhon's hot paths hold raw pointers into engine-owned memory for microseconds at a time — page cache buffers, B+Tree node arenas, hash-map backing storage. A garbage collector that's free to move or compact objects mid-access would invalidate those pointers and corrupt unsafe code. Every such allocation also needs to show up somewhere when something leaks: a forgotten unmanaged block doesn't throw, it just silently grows the process. Memory Allocators give engine subsystems addresses that never move and a parent-owned accounting trail for every byte taken.

## ⚙️ How it works (in brief)

A `MemoryAllocator` hands out two flavors of block: unmanaged ones backed by `NativeMemory.AlignedAlloc` (no GC interaction at all, optional power-of-2 alignment) and managed ones backed by a POH byte array (GC-allocated but pinnable on demand via `GCHandle`). Every block is created with an explicit owning resource and is registered as that owner's child in the engine's resource tree, not the allocator's — so a leaked block shows up under the subsystem that requested it, not lost in a flat pool. Higher-level allocators (`BlockAllocator` for fixed-stride slots, `ChainedBlockAllocator` for linked chains, `StructAllocator<T>` for typed slots) all draw their backing storage from a `MemoryAllocator` and add their own allocation/free semantics on top.

## 💻 Usage

Application code never calls the allocator directly — it's wired once at host startup and consumed internally by every engine subsystem that needs raw memory (page cache, B+Tree arenas, hash maps):

```csharp
services.AddResourceRegistry();
services.AddMemoryAllocator(opts => opts.Name = "MyApp Memory Allocator");
// ... AddPagedMemoryMappedFile / AddDatabaseEngine etc. resolve IMemoryAllocator from DI
```

The only surface application code can touch directly is `IMemoryResource` — implement it on a custom resource type if you want it to report its size into the resource tree:

```csharp
internal sealed class MyCache : IMemoryResource
{
    public int EstimatedMemorySize => _bufferBytes; // excludes child resources
    // ... rest of IResource members
}
```

| Option | Default | Effect |
|--------|---------|--------|
| `MemoryAllocatorOptions.Name` | `"Default Memory Allocator"` | Display name of the allocator's resource-tree node. |

## ⚠️ Guarantees & limits

- **Stable addresses** — unmanaged blocks never move for their lifetime; managed blocks only move while unpinned, and pinning is ref-counted so concurrent pinners are safe.
- **Leak visibility, not leak prevention** — every block is a child resource of its caller, so an undisposed block is visible in the resource tree (and via `MemoryAllocator`'s `PinnedBytes` / `PinnedLiveBlocks` metrics) rather than silently invisible; nothing reclaims it automatically.
- **Resource tree is organized by owner, not by allocator** — a block's parent is the subsystem that requested it (e.g. a hash map or chained allocator), so diagnostics group memory by what's using it.
- **Alignment is caller's choice, not free** — unmanaged allocation accepts a power-of-2 alignment; requesting one larger than the natural size wastes the padding (tracked via the `Memory:AlignmentWaste` profiler event when alignment-waste tracing is on).
- **No automatic compaction or defragmentation** — block allocators (`BlockAllocator`, `ChainedBlockAllocator`) hand out fixed-stride slots from a freelist; freeing a slot makes it reusable but never shrinks the backing block.
- **Profiler-attributed** — every alloc/free carries a 16-bit source tag so allocation volume can be attributed back to the originating subsystem when memory tracing is enabled; zero cost when it's off.

## 🧪 Tests
- [MemoryAllocatorInstrumentationTests](../../../test/Typhon.Engine.Tests/Memory/MemoryAllocatorInstrumentationTests.cs) — `PinnedBytes`/`PinnedLiveBlocks` counters, peak-never-regresses, source-tag attribution and default "unattributed" tag.
- [BlockAllocatorTests](../../../test/Typhon.Engine.Tests/Memory/BlockAllocatorTests.cs) — fixed-stride slot alloc/free/freelist reuse via `BlockAllocator`.
- [StructAllocatorTests](../../../test/Typhon.Engine.Tests/Memory/BlockAllocatorTests.cs) — typed slots with `ICleanable` reset-on-free via `StructAllocator<T>` (same file as `BlockAllocatorTests`).
- [ChainedBlockAllocatorTests](../../../test/Typhon.Engine.Tests/Memory/ChainedBlockAllocatorTests.cs) — linked-chain block growth via `ChainedBlockAllocator`.

## 🔗 Related

- Related feature: [Resource Tree & Registry](../Resources/resource-tree-registry.md) — how `IMemoryResource` plugs into the broader resource tree
- Sibling: [Page Allocation & Occupancy Tracking](../Storage/page-allocation-occupancy.md) — the page cache draws its backing storage from this allocator.

<!-- Deep dive: claude/design/Foundation/Memory/memory-allocators.md -->
<!-- Overview: claude/overview/11-utilities.md §Category F -->
<!-- ADR: claude/adr/009-pinned-memory-unsafe-code.md -->
