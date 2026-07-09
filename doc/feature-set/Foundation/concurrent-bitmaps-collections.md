---
uid: feature-foundation-concurrent-bitmaps-collections
title: 'Concurrent Bitmaps & Collections'
description: 'Lock-free/CAS-guarded occupancy bitmaps (flat and 3-level) plus a pick/putback slot array for high-contention tracking.'
---

# Concurrent Bitmaps & Collections
> Lock-free/CAS-guarded occupancy bitmaps (flat and 3-level) plus a pick/putback slot array for high-contention tracking.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](./README.md)

## 🎯 What it solves

Several engine subsystems need to know, under concurrent access, "which slot is taken" or "which slot is free" without paying a lock per slot: which blocks inside a memory-allocator page are occupied, which pages a backup snapshot has already copy-on-write captured, which array entry a worker is currently processing. A general-purpose `bool[]` behind a lock would serialize every subsystem that touches it at high frequency. These primitives give each use case the cheapest synchronization shape that still holds up under concurrent readers and writers.

## ⚙️ How it works (in brief)

`ConcurrentBitmap` is a flat array of 64-bit words — `Set`/`Clear`/`IsSet` go straight through `Interlocked.Or`/`And` on one word, no hierarchy. `ConcurrentBitmapL3Any` (and its unsynchronized sibling `BitmapL3Any`) layer two small summary levels (L1, L2) on top of the ground-truth L0 bitmap so enumerating *set* bits can skip whole empty 64- or 4096-bit regions instead of testing every bit one by one; `ConcurrentBitmapL3Any.Set` holds a short spin-lock across all three levels to keep the summary consistent, `Clear`/`IsSet` don't. `ConcurrentBitmapL3All` inverts the idea — it tracks when a word becomes *completely full* so `FindNextUnsetL0`/`FindNextUnsetL1` can hierarchically skip full regions while hunting for a free slot; every level update is a plain Interlocked CAS with a self-correcting double-check for races, so it never blocks, and capacity grows by atomically appending a whole new bank rather than resizing in place. `ConcurrentArray<T>` is a fixed-size slot array with pick/putback semantics: `Pick` atomically claims and nulls a slot (`Interlocked.Exchange`), `PutBack` restores it, `Remove` spin-waits for a slot currently held by another thread.

## 💻 Usage

```csharp
// Page-occupancy: BlockAllocatorBase claims/releases a free block within an allocator page
var blockMap = new ConcurrentBitmapL3All("MyAllocatorBlockMap", parentResource, memoryAllocator, entryCountPerPage);

int blockId = -1;
while (blockMap.FindNextUnsetL0(ref blockId) && !blockMap.SetL0(blockId)) { } // claim the next free slot
blockMap.ClearL0(blockId);                                                    // release it later

// Concurrent slot pool: workers exclusively own an item while processing it
var pool = new ConcurrentArray<MyWorkItem>(capacity: 256);
int index = pool.Add(item);
if (pool.Pick(index, out var owned))      // atomic claim; false = already taken or empty
{
    Process(owned);
    pool.PutBack(index, owned);           // release back for the next picker
}
```

| Type | Capacity | Concurrency |
|---|---|---|
| `ConcurrentBitmap` | caller-defined, fixed | Lock-free `Set`/`Clear`/`IsSet` |
| `ConcurrentBitmapL3Any` / `BitmapL3Any` | up to 262,144 bits (64×64×64), fixed | `Set` spin-locked across levels (`Any`); `BitmapL3Any` is single-threaded only |
| `ConcurrentBitmapL3All` | 262,144 bits per bank, multi-bank growable | Fully lock-free CAS, self-correcting hints |
| `ConcurrentArray<T>` | fixed at construction | `Pick`/`PutBack`/`Remove` atomic per slot |

## ⚠️ Guarantees & limits

- All four types are `internal` (`Typhon.Engine.Internals`) — engine plumbing, no public API surface.
- `ConcurrentBitmapL3Any.Set` serializes through one spin-lock scoped to that bitmap instance — concurrent `Set` calls on the *same* instance don't run in parallel; `Clear` and `IsSet` never take it.
- `ConcurrentBitmapL3All`'s L0 bit is always the ground truth; L1/L2 summary bits are best-effort hints that self-correct after a race, so a summary can be transiently stale but `IsSet`/`ClearL0`/`SetL0` results are always exact.
- The 3-level scheme is sized for ≤64×64×64 = 262,144 bits per instance/bank; beyond that, `ConcurrentBitmapL3All` grows by appending a new bank (`Grow()`) rather than enlarging the existing one — `ConcurrentBitmapL3Any`/`BitmapL3Any` have no growth path at all, capacity is fixed at construction.
- `ConcurrentBitmapL3All` is wired into the engine's resource tree and metrics pipeline (`IResource`, `IMetricSource`, `IDebugPropertiesProvider`) — capacity, utilization, and per-bank stats are reportable.
- `ConcurrentArray<T>.Remove` spin-waits indefinitely on a slot currently picked by another thread, and will wait forever if called on a slot that holds no item — callers must pair every `Pick` with `PutBack` or `Release`.
- `ConcurrentArray<T>.Add` throws once `Capacity` is reached; there is no automatic growth.
- `BitmapL3Any` carries no synchronization at all — single-threaded use only; it exists primarily as the no-overhead baseline against which `ConcurrentBitmapL3Any`'s locking cost is benchmarked.

## 🧪 Tests
- [ConcurrentBitmapL3AllTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Collections/ConcurrentBitmapL3AllTests.cs) — `ConcurrentBitmapL3All` lock-free CAS set/clear, hierarchical `FindNextUnsetL0`/L1 skip-full-regions, multi-bank `Grow()`.
- [ConcurrentBitmapL3Test](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Collections/ConcurrentBitmapL3Test.cs) — `ConcurrentBitmapL3Any` set-bit enumeration across the L1/L2 summary hierarchy.

## 🔗 Related

- Sibling: [Page Allocation & Occupancy Tracking](../Storage/page-allocation-occupancy.md) — uses this bitmap family (`BitmapL3`) to track free/occupied pages in the database file.

<!-- Deep dive: claude/overview/11-utilities.md §D.1-D.2 -->
<!-- ADR: claude/adr/041-treiber-stack-chunk-allocator.md — chunk-level slot allocation moved off this bitmap family to a free list; these bitmaps remain the page-occupancy and backup-tracking primitive -->
<!-- Design: claude/design/Durability/PitBackup/02-backup-creation.md — ConcurrentBitmapL3All as the CoW page-tracking bitmap -->
