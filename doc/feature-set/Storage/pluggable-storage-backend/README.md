# Pluggable Storage Backend (Persistent vs Transient)
> One set of segment/index code, JIT-specialized per backend, so `Transient` components get heap speed for free.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Storage](../README.md)

## 🎯 What it solves

Every Typhon data structure — segments, B+Trees, hash maps, chunk accessors — needs to read and write 8 KiB
pages. For durable components those pages live in the memory-mapped file and carry dirty tracking, eviction,
and CRC protection; for `StorageMode.Transient` components (see [Storage Modes](../../Ecs/storage-modes/README.md))
none of that applies — the data is heap-only and gone on restart by design. Forking the segment/B+Tree/hash-map
implementations to get a fast heap-only path would double the maintenance surface and risk the two copies
drifting. The pluggable storage backend lets one implementation serve both cases at full speed for each.

## ⚙️ How it works (in brief)

`IPageStore` is the abstraction every generic storage type is built against (`where TStore : struct, IPageStore`)
— never as a runtime-polymorphic field. Because the constraint is a struct, not a class, the JIT compiles a
fully specialized copy of `LogicalSegment<TStore>`, `ChunkBasedSegment<TStore>`, `ChunkAccessor<TStore>`,
`BTree<TKey, TStore>`, etc. per concrete backend, with zero virtual dispatch. Two backends ship in-tree:
`PersistentStore`, a one-line forwarding wrapper around the memory-mapped page cache, and `TransientStore`,
a heap-only store backed by pinned memory blocks. `typeof(TStore)` checks in hot paths (e.g. dirty tracking)
are constant-folded per specialization, so a `TransientStore` build of `ChunkAccessor` literally has no dirty-
tracking instructions left in it — not a cheap no-op call, an absent one.

## Sub-features

| Sub-feature | Backs | Use it for |
|-------------|-------|-----------|
| [Persistent Store (MMF-backed)](./persistent-store.md) | `StorageMode.Versioned` / `SingleVersion` | Any durable component — the default path, selected automatically |
| [Transient Store (heap-backed)](./transient-store.md) | `StorageMode.Transient` | Scratch data with `StorageMode.Transient` — tune via `TransientOptions` |

## ⚠️ Guarantees & limits

- `IPageStore` is `[PublicAPI]` for type-visibility reasons only — it is not meant for external implementation;
  the two stores ship in-tree and selection is automatic from `StorageMode`, not something application code
  chooses directly.
- Backend selection is per-component, decided at `[Component(StorageMode = ...)]` registration — never mixed
  within a single segment.
- `PersistentStore` is `readonly struct` (8 bytes, one pointer) with every method `AggressiveInlining`-forwarded
  to the page cache — the abstraction costs nothing over calling the page cache directly.
- `TransientStore` trades the persistence guarantees away entirely in exchange for the no-op dirty-tracking path
  — see the [Transient Store](./transient-store.md) page for what that costs you.

## 🧪 Tests

- [StorageModeInfrastructureTests](../../../../test/Typhon.Engine.Tests/Data/StorageModeInfrastructureTests.cs) — per-component backend selection via `[Component(StorageMode = ...)]`; confirms Versioned/SV/Transient never share a segment

## 🔗 Related

- Related feature: [Storage Modes](../../Ecs/storage-modes/README.md) — the component-level decision this backend split exists to serve
- Sub-features: [Persistent Store (MMF-backed)](./persistent-store.md), [Transient Store (heap-backed)](./transient-store.md)

<!-- Deep dive: claude/design/Storage/PageCache/08-page-stores.md, claude/design/Storage/StorageModeGuide.md -->
