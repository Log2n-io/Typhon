---
uid: feature-storage-pluggable-storage-backend-persistent-store
title: 'Persistent Store (MMF-backed)'
description: 'The default backend — every durable component''s segments run through the memory-mapped page cache at zero abstraction cost.'
---

# Persistent Store (MMF-backed)
> The default backend — every durable component's segments run through the memory-mapped page cache at zero abstraction cost.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Storage](../README.md)

## 🎯 What it solves

`StorageMode.Versioned` and `StorageMode.SingleVersion` components need their pages backed by the durable,
crash-recoverable memory-mapped file — dirty tracking, eviction protection, CRC verification, WAL/checkpoint
integration, all of it. The persistent store is the backend that wires segments, B+Trees, and chunk accessors
to that machinery, and it has to do so without adding a cent of overhead over calling the page cache directly,
since this is the path nearly every component in a Typhon database takes.

## ⚙️ How it works (in brief)

`PersistentStore` is a `readonly struct` wrapping a single `ManagedPagedMMF` reference (8 bytes total — one
pointer). Every `IPageStore` member is a one-line, `AggressiveInlining` forward to the underlying page cache
(`RequestPageEpoch`, `IncrementDirty`, `TryLatchPageExclusive`, `AllocatePages`, …). Because the struct is
`readonly` and every method inlines, the JIT produces the same assembly as if the segment code called the page
cache directly — the generic `where TStore : struct, IPageStore` constraint costs nothing here. This is the
backend selected for every component that isn't `StorageMode.Transient`.

## 💻 Usage

You never construct a `PersistentStore` yourself — it's selected automatically the moment a component uses the
default storage mode (or explicitly `Versioned`/`SingleVersion`):

```csharp
[Component("Game.Inventory", 1, StorageMode = StorageMode.Versioned)]
struct Inventory { public int Gold; }

[Component("Game.Position", 1, StorageMode = StorageMode.SingleVersion)]
struct Position { public float X, Y, Z; }

// No StorageMode = Versioned by default — also routes through PersistentStore.
[Component("Game.Tag", 1)]
struct Tag { public int Value; }
```

What you actually configure is the page cache the store wraps — see
[Memory-Mapped Page Cache & Clock-Sweep Eviction](../page-cache.md) for sizing and timeout options.

## ⚠️ Guarantees & limits

- Zero-cost abstraction: every method is an inlined forward, `readonly struct` avoids defensive copies — the
  generated code for `LogicalSegment<PersistentStore>` matches hand-written non-generic page-cache calls.
- Full durability surface: dirty tracking (`DirtyCounter`/`ActiveChunkWriters`), slot ref-counting, CRC
  verification, and checkpoint coordination are all live — this is the only backend that participates in
  WAL/checkpoint.
- `MemPagesBaseAddress` is non-null and contiguous, letting `ChunkAccessor` reverse-map a raw pointer to a
  memory-page index via pointer arithmetic — a path `TransientStore` cannot offer.
- An escape hatch (`PersistentStore.Mmf`) exposes the wrapped `ManagedPagedMMF` for code that needs MMF-specific
  APIs (`AllocateSegment`, `CreateChangeSet`, …) — internal engine plumbing, not an application-facing surface.

## 🧪 Tests

- [ManagedPagedMMFTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/ManagedPagedMMFTests.cs) — segment/chunk allocation exercised directly through `PersistentStore` (every `AllocateSegment`/`AllocateChunkBasedSegment` call wraps one)

## 🔗 Related

- Related feature: [Memory-Mapped Page Cache & Clock-Sweep Eviction](../page-cache.md), [Epoch-Based Page Protection & Dirty-Page Tracking](../epoch-dirty-tracking.md)
- Parent feature: [Pluggable Storage Backend](./README.md)

<!-- Deep dive: claude/design/Storage/PageCache/08-page-stores.md — PersistentStore -->
