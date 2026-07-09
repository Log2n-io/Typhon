---
uid: feature-storage-pluggable-storage-backend-transient-store
title: 'Transient Store (heap-backed)'
description: 'Pinned heap blocks standing in for the page cache, so Transient components get raw-memory speed through the same segment code.'
---

# Transient Store (heap-backed)
> Pinned heap blocks standing in for the page cache, so `Transient` components get raw-memory speed through the same segment code.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Storage](../README.md)
**Assumes:** [Transient (Storage Mode)](../../Ecs/storage-modes/storage-mode-transient.md)

## 🎯 What it solves

`StorageMode.Transient` components (see [Transient](../../Ecs/storage-modes/storage-mode-transient.md))
promise no durability cost at all — no WAL, no checkpoint, no dirty tracking. Routing them through the
memory-mapped page cache would mean paying for (and then discarding) eviction bookkeeping and dirty-counter
updates the data will never need. The transient store gives those components their own backend — plain pinned
heap memory — so the cost genuinely disappears instead of just being hidden.

## ⚙️ How it works (in brief)

`TransientStore` allocates 8 KiB pages from pinned heap blocks (`TransientOptions.PagesPerBlock` pages per
block, one `IMemoryAllocator.AllocatePinned` call per block) and maps file-page index directly to memory-page
index — there is no cache layer to translate, and pages are never evicted once allocated. All dirty-tracking
and slot-ref-counting members of `IPageStore` are empty method bodies; combined with `AggressiveInlining` and
the generic specialization, the JIT removes those call sites entirely from `ChunkAccessor<TransientStore>` and
friends rather than executing a cheap no-op. Page latching still uses a real per-page spinlock (concurrent
B+Tree growth can race even on transient pages), just a lighter one than the persistent path — no seqlock, no
torn-page repair, no state machine. Growth is capped: `AllocatePages` throws `InsufficientMemoryException` once
the store would exceed its configured memory budget.

## 💻 Usage

You select the transient backend indirectly, by declaring a component `Transient`:

```csharp
[Component("Game.AnimState", 1, StorageMode = StorageMode.Transient)]
struct AnimState
{
    public int ClipId;
    public float Time;
}
```

The one thing you do configure directly is the memory budget and block granularity, at engine startup:

```csharp
services.AddDatabaseEngine(options =>
{
    options.Transient.MaxMemoryBytes = 512 * 1024 * 1024; // default: 256 MB
    options.Transient.PagesPerBlock  = 64;                // default: 32 (8 KiB pages -> 512 KiB/block)
});
```

| Option | Default | Effect |
|--------|---------|--------|
| `TransientOptions.MaxMemoryBytes` | 256 MB | Hard cap; `AllocatePages` throws `InsufficientMemoryException` once exceeded |
| `TransientOptions.PagesPerBlock` | 32 (256 KiB/block) | Pages per pinned allocation call — larger reduces allocator overhead, wastes more for small stores |

## ⚠️ Guarantees & limits

- No durability whatsoever, by design: data lives only in pinned heap memory and is gone on process exit or
  crash — there is no recovery path, not even a degraded one.
- Dirty tracking, slot-ref counting, and CRC verification are compiled away, not skipped at runtime — the
  `ChunkAccessor<TransientStore>` specialization has no instructions for them at all.
- Pages are never evicted once allocated — the whole store must fit in `MaxMemoryBytes`; there is no spill-to-disk
  fallback. Hitting the cap throws `InsufficientMemoryException` rather than degrading.
- `MemPagesBaseAddress` is `null` — transient pages live in separate, non-contiguous heap blocks, so the
  pointer-arithmetic reverse-mapping `PersistentStore` supports isn't available here (a `typeof(TStore)` branch
  routes around it).
- `GetOrAllocateDirectoryTwin` always returns "no twin" — transient segments are never persisted, so the
  torn-write protection that exists for persistent segment-directory pages doesn't apply.
- `TransientStore` itself is an internal type — application code configures it through `TransientOptions` and
  `StorageMode.Transient`, never by constructing or implementing it directly.

## 🧪 Tests

- [TransientSegmentGrowthRegressionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/TransientSegmentGrowthRegressionTests.cs) — page-address stability across `TransientStore`'s internal array growth (a bug `PersistentStore`'s stable base pointer doesn't share)
- [StorageModeInfrastructureTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/StorageModeInfrastructureTests.cs) — `Transient`-declared components allocate transient segments/chunks and carry no WAL type id

## 🔗 Related

- Related feature: [Transient (Storage Mode)](../../Ecs/storage-modes/storage-mode-transient.md) — the component-level feature this backend powers
- Parent feature: [Pluggable Storage Backend](./README.md)

<!-- Deep dive: claude/design/Storage/PageCache/08-page-stores.md — TransientStore, TransientOptions -->
