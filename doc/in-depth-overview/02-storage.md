---
uid: overview-storage
title: '02 — Storage'
description: 'Storage is the physical layer: a memory-mapped file managed by a page cache, segments built on top of pages, and accessors that hold short-lived,…'
---

# 02 — Storage

**Code:** [`src/Typhon.Engine/Storage/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Storage)

Storage is the physical layer: a memory-mapped file managed by a page cache, segments built on top of pages, and accessors that hold short-lived, JIT-inlined views into chunk memory. Everything above this layer — MVCC revision chains, B+Tree indexes, the ECS component tables, even the page CRC + seqlock snapshots the checkpoint relies on — bottoms out here. The shape of this layer (8 KB pages, two-pass clock-sweep eviction, generic `<TStore>` segments) is what makes the higher layers cheap.

Because the cache is a fixed pool with on-demand load + clock-sweep eviction, **the database file can be arbitrarily larger than the cache** — resident memory is bounded by `DatabaseCacheSize`, not by database size. Persistent component data, B+Tree indexes, the per-archetype `EntityMap`, and the free-space bitmap all live in this paged store, so data volume and entity count scale with *disk*, not memory. The lone exception is **Transient** components (`TransientComponentSegment`), which are in-memory only by design. This larger-than-RAM property is routine for SQL and embedded engines (SQLite, LMDB) but sets Typhon apart from in-memory ECS frameworks, where the entire world must fit in process memory.

You read this doc when you're: tuning page-cache pressure, understanding why a benchmark stalls, designing a new on-disk structure, debugging dirty-counter inflation, or reading the engine's most allocation-sensitive code (`ChunkAccessor`, `ChunkBasedSegment`). The narrative is bottom-up — file → pages → segments → accessors → cross-cutting concerns (dirty tracking, backpressure, page CRC + seqlock).

<a href="assets/typhon-storage-overview.svg">
  <img src="assets/typhon-storage-overview.svg" width="1200" alt="Storage layer overview">
</a>
<br>
<sub>The layered storage model: two <code>IPageStore</code> backends (<code>PersistentStore</code> over the page cache, heap-backed <code>TransientStore</code>), the <code>PagedMMF</code> / <code>ManagedPagedMMF</code> cache core, the generic <code>&lt;TStore&gt;</code> segment hierarchy (<code>ChunkBasedSegment</code> extends <code>LogicalSegment</code>; VSBS / StringTable compose a chunk segment), the hot-path accessors, and the cross-cutting dirty-tracking / backpressure / page-CRC machinery.</sub>

---

## 1. Overview — the layered model

Three concentric layers, each with one obvious type:

| Layer | Type | What it owns |
|---|---|---|
| **Page cache** | [`PagedMMF`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/PagedMMF.cs) | A pinned slab of `N × 8 KB` memory backing the data file. Allocates / evicts pages by clock-sweep. |
| **Database manager** | [`ManagedPagedMMF`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ManagedPagedMMF.cs) | Page 0 root header + bootstrap dictionary + occupancy bitmap. Allocates / frees *file* pages. |
| **Segments + accessors** | [`LogicalSegment<TStore>`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/LogicalSegment.cs), [`ChunkBasedSegment<TStore>`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ChunkBasedSegment.cs), [`ChunkAccessor<TStore>`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ChunkAccessor.cs) | Logical pages grouped into segments; fixed-size chunks allocated within segments; SOA-cached accessors for hot-path chunk reads/writes. |

Two backends share the segment / accessor code via the [`IPageStore`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/IPageStore.cs) struct-generic interface:

- [`PersistentStore`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/PersistentStore.cs) — wraps `ManagedPagedMMF`. The JIT inlines every delegation; assembly is identical to non-generic code.
- [`TransientStore`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/TransientStore.cs) — heap-backed, no cache, no dirty tracking. The dirty-tracking methods are no-ops; `typeof(TStore)` branches in `ChunkAccessor` dead-code-eliminate the persistence machinery for transient components.

Cross-cutting concerns sit alongside this layered core:

- **Dirty tracking** — [`ChangeSet`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ChangeSet.cs) — coordinates `DirtyCounter` / `ActiveChunkWriters` lifecycle for a UoW.
- **Backpressure** — [`IPageCacheBackpressureStrategy`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/IPageCacheBackpressureStrategy.cs) / [`WaitForIOStrategy`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/WaitForIOStrategy.cs) — what happens when the clock-sweep finds nothing evictable.
- **Page CRC & seqlock** — CRC32C torn-write *detection* + consistent checkpoint snapshots (no FPI; recovery rebuilds — see §7).
- **Storage introspection** — [`StorageMapTypes`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/StorageMapTypes.cs) — the read-only surface Workbench's Database File Map uses.

---

## 2. PagedMMF — the page cache

[`PagedMMF`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/PagedMMF.cs) is the foundation. It owns a single backing file (the database) and a fixed-size pool of in-memory pages. Pages are loaded on demand and evicted when the pool is full.

### Page layout — 64 + 128 + 8000 = 8192

```
┌──────────────────┬──────────────────┬──────────────────────────────────────────┐
│ PageBaseHeader   │ Metadata         │ Raw Data                                 │
│ 64 bytes         │ 128 bytes        │ 8000 bytes                               │
└──────────────────┴──────────────────┴──────────────────────────────────────────┘
0                  64                 192                                       8192
```

Constants defined on `PagedMMF`:

| Constant | Value | Meaning |
|---|---|---|
| `PageSize` | 8192 | One page |
| `PageBaseHeaderSize` | 64 | Bytes reserved for [`PageBaseHeader`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/PageBaseHeader.cs) |
| `PageMetadataSize` | 128 | Per-page metadata (e.g. chunk occupancy bitmap) |
| `PageHeaderSize` | 192 | `PageBaseHeaderSize + PageMetadataSize` |
| `PageRawDataSize` | 8000 | What user code actually writes to |
| `PageSizePow2` | 13 | `2^13 = 8192` (used for shift-instead-of-divide) |

The base header is a small struct ([`PageBaseHeader`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/PageBaseHeader.cs)) with `Flags`, `Type`, `FormatRevision`, `ChangeRevision` (incremented every disk write), `PageChecksum` (CRC32C over the page, skipping the checksum field itself), and `ModificationCounter` (the **seqlock counter** used for torn-page detection — see §7). The header struct itself only occupies the first 24 bytes; the rest of the 64-byte zone is reserved for forward compatibility.

### `PageInfo` and `PageState`

Each in-memory page has a sidecar [`PageInfo`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/PagedMMF.PageInfo.cs) tracking:

- `MemPageIndex` / `FilePageIndex` — slot ↔ file mapping
- `PageState` — current state machine value (see below)
- `ClockSweepCounter` — eviction heuristic (range 0..5, see §2.4)
- `DirtyCounter` (`DC`) — > 0 means the page has unsaved writes; prevents eviction
- `ActiveChunkWriters` (`ACW`) — > 0 means writers are mid-flight; prevents *checkpoint snapshot* (but not eviction)
- `SlotRefCount` — number of `ChunkAccessor` slots holding raw pointers into this page
- `AccessEpoch` — epoch tag (see [01-foundation §4](01-foundation.md))
- `CrcVerified` — CRC checked since this load? (reset on allocate)
- `StateSyncRoot` — `AccessControlSmall` protecting state transitions
- `PageExclusiveLatch` — `AccessControlSmall` for exclusive writer ownership
- `ExclusiveLatchDepth` — re-entrance counter (multiple chunks on the same page)

`PageState` is a 4-value enum with a **deliberate gap** (no value 3) — bits are reserved for future state extension:

```csharp
internal enum PageState : ushort
{
    Free       = 0,   // Unallocated slot
    Allocating = 1,   // Mid-load, single-owner
    Idle       = 2,   // Loaded, evictable when DC == 0 and AccessEpoch < MinActiveEpoch
    Exclusive  = 4,   // Held by one thread under PageExclusiveLatch
}
```

State transitions are protected by `StateSyncRoot`. The Idle → Exclusive transition is what `TryLatchPageExclusive` does; it also bumps the seqlock counter from even → odd.

### Default cache size — 256 MiB

`DatabaseCacheSize` on [`PagedMMFOptions`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/PagedMMFOptions.cs) defaults to **256 MiB** (`DefaultDatabaseCacheSize`) — a production-sane size for the one primary engine a process normally runs. The hard minimum is **8 MiB** (`MinimumCacheSize` = `MinimumMemPageCount × 8 KiB`); a configured size below the **64 MiB** recommended floor (`RecommendedMinimumCacheSize`) logs a startup warning. Public byte constants — `PagedMMFOptions.DefaultCacheSizeBytes` / `MinimumCacheSizeBytes` / `PageSizeBytes` — expose these in-code.

**The 8 MiB minimum is deliberately small — but it's the hard floor; production-sane sizing starts far higher.** The internal `TestMode` flag suppresses the small-cache warning and the min-size floor so a fixture *can* run below 8 MiB; fixtures that deliberately exercise eviction opt in via `[Property("CacheSize", ...)]` to a sub-floor cache under `TestMode`, while the general test default is a representative 8 MiB (right at the floor). That puts the eviction, backpressure, and dirty-counter paths under real pressure exactly where a test wants it, while the rest of the suite runs representatively. Production leaves `TestMode` off and gets the 256 MiB default; size `DatabaseCacheSize` — or the fluent `TyphonOptions.PageCacheSize(...)` — for your workload's largest single-transaction working set (real servers go much higher).

The validator enforces: the size must be a multiple of the page size, at least 8 MiB (unless `TestMode`), and ≤ 4 GiB. The **4 GiB ceiling is not a hard architectural limit** — it exists purely because the cache is currently a *single* contiguous allocation for all pages. It will be raised substantially soon (by splitting into multiple allocations, or moving to a 64-bit allocation); nothing in the page-cache design depends on staying under 4 GiB.

### Two-pass clock-sweep eviction

When a new page is requested and no `Free` slot exists, `AllocateMemoryPageCore` (in [`PagedMMF.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/PagedMMF.cs)) runs **two passes** over the page array:

```
Pass 1 (counter-respecting):  scan up to 2 × N slots
   if slot.ClockSweepCounter == 0 and TryAcquire succeeds → evict it
   else slot.DecrementClockSweepCounter and continue
Pass 2 (counter-ignoring):    scan up to N slots
   take the first slot TryAcquire succeeds on, regardless of counter
```

The counter is incremented on every access via `PageInfo.IncrementClockSweepCounter`, capped at `ClockSweepMaxValue = 5`. Hot pages climb to 5 and survive several full sweeps; cold pages decrement to 0 and get reclaimed. The second pass exists for the case where every page has DC > 0 or is epoch-protected at the moment we sweep — we still need a slot, so we make one more circle ignoring the heuristic but respecting the *real* eviction blockers (DC, ACW, SlotRefCount, AccessEpoch).

When both passes fail, the **backpressure path** kicks in (see §6). The clock hand `_clockSweepCurrentIndex` is a `CacheLinePaddedInt` to avoid false sharing with adjacent state.

### Two micro-optimizations worth mentioning

`AllocateMemoryPageCore` has a fast prefix path: if `filePageIndex - 1` is already cached in `memPageIndex N`, the allocator tries `N + 1` first. This lets sequential page reads coalesce into a single disk write later when both pages flush.

CRC verification is **lazy**. `EnsurePageVerified` (line ~1436) runs only the first time a page is touched after load. If `PageChecksumVerification.RecoveryOnly` is set (the default until recovery completes), it's skipped entirely. After recovery, `DatabaseEngine` flips the mode to `OnLoad` — every fresh load verifies, then `CrcVerified` is cached until the slot is reused. On mismatch *during recovery* the page is recorded **suspect** — rebuilt if it backs a derived structure, or a loud failure if it still backs a live primary chunk (§7, and [11-durability §6](11-durability.md)); during normal operation a `PageCorruptionException` propagates.

---

## 3. ManagedPagedMMF — the database file manager

[`ManagedPagedMMF`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ManagedPagedMMF.cs) extends `PagedMMF` with what makes the file actually a *database*: a known root layout, an occupancy bitmap, and a bootstrap key/value store.

### The first eight pages of an empty file (format v4)

```
Page 0: Meta slot A          — magic + format revision + bootstrap dictionary (CK-05 A/B meta pair)
Page 1: Meta slot B          — the alternate meta slot
Page 2: Occupancy segment root — directory-only LogicalSegment (lists [2, 4]); CK-05 directory page → twinned
Page 3: Occupancy root TWIN   — the root's second physical slot (CK-05)
Page 4: Occupancy first data page — the BitmapL3 L0 words live here (the directory-only root carries no data)
Page 5: Reserved for occupancy growth (next data page)
Page 6: Reserved for the occupancy bitmap's next map-extension directory page
Page 7: Reserved for that map-extension page's TWIN (it is itself a directory page → needs a twin)
```

`InitialReservedPageCount = 8`. The reserves are pre-allocated so the *first* occupancy grow doesn't need to chain through the allocator that's itself trying to grow. The **directory-only root (v4)** is why the occupancy bitmap needs a *separate* first data page (page 4): the root page now holds only the segment's page directory, no bitmap words.

### `RootFileHeader` — about 108 bytes

On disk at page 0, offset `PageBaseHeaderSize` (64), there's a small identity header — *not* the 192 B figure that appeared in some older docs:

```csharp
[StructLayout(LayoutKind.Sequential)]
unsafe internal struct RootFileHeader
{
    public fixed byte HeaderSignature[32];    // "TyphonDatabase"
    public int        DatabaseFormatRevision; // currently 4 (CK-05 twins + directory-only root)
    public ulong      DatabaseFilesChunkSize;
    public fixed byte DatabaseName[64];       // verified on load
}
```

Total ~108 bytes. Everything *dynamic* — SPIs for segments, the checkpoint LSN, occupancy reserved page indices — moved out of the struct and into the bootstrap dictionary that follows immediately after, at `BootstrapStreamOffset = PageBaseHeaderSize + sizeof(RootFileHeader)`.

### `BootstrapDictionary` — typed key/value on page 0

[`BootstrapDictionary`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/BootstrapDictionary.cs) is a compact tagged-value stream. On disk:

```
[StreamLength:2B] [TypeTag:1B][Key:UTF8+NUL][Value:N bytes] ... [0xFF]
```

Supported value types: `Bool` (1 B), `Int1..Int6` (4..24 B), `Long` (8 B), `DateTime` (8 B, ticks), `String` (UTF-8 NUL-terminated). The `End` sentinel is `0xFF`. In-memory it's a `Dictionary<string, Value>` for O(1) lookup; the stream is rewritten in full on shutdown (and on bootstrap mutations).

Keys are conventional engine prefixes — `OccupancyMapSPI`, `OccupancyReserved`, `CheckpointLSN` are the storage-layer ones. The Ecs / Transactions / Durability / Schema layers each contribute their own (e.g. `BK_NextFreeTSN`, the schema SPIs).

### Occupancy bitmap — `BitmapL3` on a `LogicalSegment`

The free-page tracking lives in [`BitmapL3`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ManagedPagedMMF.BitmapL3.cs) — a three-level summarized bitmap stored *in the file itself* as the raw data of a `LogicalSegment<PersistentStore>`. Each L0 long covers 64 pages of the file; one *data* page worth of L0 longs (`PageRawDataSize / 8 = 1000`) covers 64 000 file pages, which is roughly **500 MiB of file data per occupancy data page**. With the directory-only root (v4) the occupancy segment's root holds only the page directory — no bitmap words — so the L0 words start on the segment's first data page (genesis page 4). Bit mutations keep their data page dirty-until-checkpoint (even on allocation paths that pass no `ChangeSet`), so a torn/evicted occupancy page can never silently drop a freshly-set bit.

When occupancy gets full, `GrowOccupancySegment` consumes the pre-allocated `_occupancyNextReservedPageIndex`, allocates a fresh reserve, and the cycle continues. The L1/L2 summary levels live in heap memory (`Memory<long>` arrays) — they're rebuilt on load from the L0 bitmap (source of truth).

`AllocatePages` / `AllocatePage` enter under `_occupancyMapAccess` exclusive (see [01-foundation §1](01-foundation.md) — `AccessControl`) and walk the bitmap level-down to find a free run.

---

## 4. Segments & accessors

A *segment* is a typed view of a sequence of file pages. The base class [`LogicalSegment<TStore>`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/LogicalSegment.cs) handles the page-list bookkeeping; [`ChunkBasedSegment<TStore>`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ChunkBasedSegment.cs) adds fixed-size chunk allocation on top.

### `LogicalSegment<TStore>` — page-list segment

**Directory-only root (v4).** The root page is a *pure directory page*: its entire 8000-byte raw-data area lists the first 2000 pages of the segment — it carries **no** segment data. If the segment grows beyond 2000 pages, additional **map-extension pages** are chained, each holding another 2000 (= `PageRawDataSize / sizeof(int)`) page indices. Because the root holds no data, the CK-05 twin that shadows every directory page (§7) protects only the immutable directory — never live data — and directory addressing is uniform (root and every extension page hold the same number of entries). One consequence: a segment always spans **at least 2 pages** (the directory root + at least one data page); the allocators clamp to this minimum.

Two relevant constants:

| Constant | Value | Meaning |
|---|---|---|
| `RootHeaderIndexSectionCount` | 2000 | Page indices on the directory-only root page (= `NextHeadersIndexSectionCount`) |
| `NextHeadersIndexSectionCount` | 2000 | Page indices stored on each map-extension page |

Forward traversal goes through the linked list in [`LogicalSegmentHeader`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/LogicalSegment.cs) (lives at offset `PageBaseHeader.Size = 64` inside each segment page, in the metadata zone):

- `LogicalSegmentNextMapPBID` — next map-extension directory page (`0` = end)
- `LogicalSegmentNextRawDataPBID` — next data page (`0` = end)

The root page holds **no** usable data (the directory fills the whole `PageRawDataSize`); every data page (segment page 1+) has the full 8000 bytes.

`Grow(newLength, ...)` is `lock`-protected and `volatile`-publishes the new `_pages` array — concurrent reads always see a consistent index view. `GetPage(i, epoch, ...)` resolves the i-th segment page index through `_store.RequestPageEpoch`, returning a [`PageAccessor`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/PageAccessor.cs) (a thin wrapper over the page address with typed `Metadata<T>` / `RawData<T>` / `StructAt<T>` slicing).

### `ChunkBasedSegment<TStore>` — fixed-stride allocator

Stores fixed-size chunks (minimum stride 8 B). Each page reserves part of its 128-byte metadata zone for a per-page occupancy bitmap; chunks are allocated by setting bits in that bitmap via `Interlocked.Or`. Three precomputed values keep `GetChunkLocation` branch-free:

- `ChunkCountRootPage` — chunks per root page. With the directory-only root (v4) the directory fills the whole root, so this is **always 0** — chunk 0 lives on segment page 1, not the root.
- `ChunkCountPerPage` — chunks per data (non-root) page
- `_divMagic` — magic multiplier for `chunkIdx / ChunkCountPerPage` (multiply + shift, ~3 cycles, vs 20–80 for division)

Alignment padding ensures chunks start at stride-aligned absolute page offsets — for `stride = 128`, each data page wastes 64 B (because `PageHeaderSize = 192` isn't a multiple of 128). For `stride = 64`, padding is zero. (The root carries no chunks, so its alignment is moot.)

#### The lock-free forward singly-linked list

Free-page tracking is a lock-free **forward SLL** over the pages that have at least one free chunk:

- `_freeHead` — head index, or `EMPTY_PAGE = -1` when no page has free space
- `_nextPage[i]` — successor index, `EMPTY_PAGE` for tail, `NOT_IN_LIST = -2` when removed
- `HEAD_SENTINEL = -3` — used internally by allocators to indicate "predecessor is the head pointer itself"

`AllocateChunk` walks the chain, scans a page's bitmap (`Interlocked.Or` with a bit mask), and on success calls `_store.EnsureDirtyAtLeast(memPageIdx, 1)` + `Interlocked.Increment(_allocatedCount)`. When a page's bitmap is fully set, it's removed from the chain via **two-phase mark + unlink**:

```
Phase A (linearization point):  CAS _nextPage[cur] from capturedNext → NOT_IN_LIST
Phase B (best-effort unlink):   CAS predecessor's pointer from cur → capturedNext
```

Phase A is the linearization point — once it succeeds, the page is "removed" as far as any concurrent traverser is concerned. Phase B failure is harmless: a later walk hits `NOT_IN_LIST` and restarts from `_freeHead` with a bounded restart counter (`restarts > length` triggers `RebuildFreeList` under `_growLock`). Freed pages are appended at the tail. The minimum chunk size is 8 bytes.

#### Growth — uses your `ChangeSet`

`Grow(minNewPageCount, changeSet)` doubles the segment (or grows to the minimum requested), then for every newly allocated page calls `_store.EnsureDirtyAtLeast(memPageIdx, 2)`. That `2`, not `1`, is the **growth-race fix**: see §5 below.

`EnsureCapacity(minChunkCount, changeSet)` is the pre-sizing entry point used by schema migration; `GrowIfNeeded` is the lazy variant used inside `AllocateChunk` when `_allocatedCount == _capacity`.

### `ChunkAccessor<TStore>` — the hot-path accessor

[`ChunkAccessor<TStore>`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ChunkAccessor.cs) is the type the ECS / Indexing / Revision layers actually hold across operations. It's a `struct` (no heap allocation), tagged `[NoCopy]`, with pure **Structure-of-Arrays** layout for SIMD search:

```csharp
private fixed int  _pageIndices[16];    // 64 bytes — SIMD-searchable
private fixed long _baseAddresses[16];  // 128 bytes — direct pointer per slot

private ushort _dirtyFlags;             // 16-bit dirty bitmask (one bit per slot)
private byte   _clockHand;              // O(1) eviction cursor
private byte   _mruSlot;                // MRU shortcut
private byte   _usedSlots;              // high water mark

private int    _stride;
private int    _rootHeaderOffset;
private int    _otherHeaderOffset;
```

Three-tier hot path inside `GetChunkAddress`:

1. **MRU check** — single-page workloads (B+Tree probes inside a node, ECS field reads on the same chunk) hit this without touching SIMD.
2. **SIMD `Vector256` search** — two 256-bit loads + equality compare across 16 cached page indices, scanned with `ExtractMostSignificantBits` + `TrailingZeroCount`.
3. **Clock-hand eviction** — when no slot matches, the clock hand advances and the slot at the cursor is evicted. Eviction *cannot fail*: the cache has no pinned slots, the underlying page is protected by the active `EpochGuard` regardless of slot eviction.

`memPageIndex` for a slot is computed *on demand* from `_baseAddresses[slot]` minus `_memPagesBaseAddr`, shifted by `PageSizePow2`. This saves 64 B of state per accessor (no `_memPageIndices[16]`) at the cost of ~3 cycles in slow paths only.

Dirty marking: `MarkSlotDirty(slot)` sets the dirty bit, calls `_store.IncrementActiveChunkWriters(memPageIdx)`, and registers the page with the `ChangeSet`. See §5 — this is the choreography that makes B+Tree splits torn-page-safe.

---

## 5. ChangeSet & dirty tracking

The page cache has two counters per page that work together:

| Counter | Set by | Cleared by | Blocks |
|---|---|---|---|
| `DirtyCounter` (`DC`) | `IncrementDirty` (on first `ChangeSet` registration; on re-dirty after commit) | `DecrementDirty` (only checkpoint Step 5) | **Eviction** while > 0 |
| `ActiveChunkWriters` (`ACW`) | `IncrementActiveChunkWriters` (in `MarkSlotDirty`) | `DecrementActiveChunkWriters` (in `CommitChanges`, eviction queue) | **Checkpoint snapshot** while > 0 |

DC and ACW are independent dimensions:

- **DC > 0** means *the page has pending writes that must reach disk*. Eviction is forbidden. Checkpoint *can* snapshot the page if ACW == 0.
- **ACW > 0** means *a writer is mid-flight*. Snapshotting now would capture partially-written data (e.g. a B+Tree node with odd OLC version, or a struct mid-update). Checkpoint **skips** this page on the current cycle; it'll be picked up next time.

### `ChangeSet` — the per-UoW dirty page set

[`ChangeSet`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ChangeSet.cs) wraps a `HashSet<int>` of memory-page indices touched during a UoW. Public surface:

| Method | What it does |
|---|---|
| `AddByMemPageIndex(int)` | Register a dirty page. Idempotent — only the *first* call per page calls `IncrementDirty`. Returns `true` if this was the first registration. |
| `SaveChangesAsync()` / `SaveChanges()` | (Non-WAL paths) write all tracked pages to disk. After write, `DecrementDirty` brings `DC` back down. |
| `ReleaseExcessDirtyMarks()` | **WAL-path equivalent**: caps `DC` of every tracked page at 1 (via `DecrementDirtyToMin(memIdx, 1)`), then clears the set. Without this, hot pages would accumulate one `DC` increment per UoW and become permanently unevictable. |
| `Reset()` | Rollback path — decrements `DC` once per tracked page. |
| `DeferEviction(entry)` / `FlushDeferredEvictions()` | Per-eviction `SlotRefCount` / `ACW` decrements that the persistent-store `ChunkAccessor` queues when a slot gets evicted mid-UoW, drained at commit. |

`ChangeSet` is pooled via [`PagedMMF.RentChangeSet`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/PagedMMF.cs) + `ReturnChangeSet` — a `ConcurrentBag<ChangeSet>` keeps reusable instances. Pool rental is the normal pattern; the `new ChangeSet(this)` path is only the cold start.

### `ReleaseExcessDirtyMarks` and the 128-op refresh

Called by the ECS path every 128 entity operations (`EpochRefreshInterval = 128` — see [06-ecs §9](06-ecs.md)) inside `FlushAndRefreshEpoch`, and unconditionally at UoW dispose. This is what keeps hot pages evictable: without it, a 10 000-op transaction over `ComponentA`'s root page would leave `DC = 10 000`, and the only thing that could decrement it is 10 000 checkpoint cycles.

### `EnsureDirtyAtLeast(2)` — the Grow → first-access race

`ChunkBasedSegment.Grow` calls `_store.EnsureDirtyAtLeast(memPageIdx, 2)` for each newly allocated page (see `ChunkBasedSegment.cs:274`). Why 2 and not 1?

The race: `base.Grow` registers the new page with a `ChangeSet`, which sets `DC = 1`. Between the moment `base.Grow` releases the page latch and the moment `ChunkBasedSegment.Grow` re-latches it to clear the bitmap, a checkpoint cycle can run: it snapshots zeros, writes them, calls `DecrementDirty`, and brings `DC` to 0 — making the page evictable before the caller (`AllocateChunk` → `GetChunkAddress`) has had a chance to establish `ACW > 0` protection.

`EnsureDirtyAtLeast(memPageIdx, 2)` raises `DC` to at least 2 atomically. One checkpoint cycle decrements to 1; the page survives. The next caller's `MarkSlotDirty` arrives, raises `ACW > 0`, and from then on the page is in the normal write protection regime.

**All `AllocateChunk` callers must pass a `ChangeSet`** to thread through `Grow`. Without that, `Grow` creates a local `ChangeSet` whose `DC` increments are "orphaned" (no UoW lifecycle to release them); a checkpoint cycle decrements them to 0 and the protection collapses.

### `MarkSlotDirty` re-registration — the re-dirty guard (per design rule)

`MarkSlotDirty` (in `ChunkAccessor`) has subtle behaviour for *re-dirty*. When `_changeSet.AddByMemPageIndex(memIdx)` returns `false` (page already tracked by this `ChangeSet`), it calls `_store.IncrementDirty(memIdx)` unconditionally — not `EnsureDirtyAtLeast(1)`. The reason: between two accessor rentals within the same UoW, a checkpoint can have snapshotted the page (with ACW=0 mid-rent), making the snapshot stale. `IncrementDirty` pushes `DC` to ≥ 2, so the pending `DecrementDirty` from that checkpoint leaves `DC ≥ 1` — the page stays dirty for the *next* checkpoint cycle which will capture the new modifications. `ReleaseExcessDirtyMarks` ultimately caps the inflation at 1 on UoW dispose.

---

## 6. Backpressure

When the clock-sweep returns nothing — every page is dirty, epoch-protected, or slot-pinned — the allocator enters the backpressure path. The strategy is pluggable:

```csharp
internal interface IPageCacheBackpressureStrategy : IDisposable
{
    bool OnPressure(ref BackpressureContext ctx, int dirtyPageCount, int epochProtectedCount);
    void SignalPageAvailable();
}
```

[`WaitForIOStrategy`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/WaitForIOStrategy.cs) is the default — a `ManualResetEventSlim` waited up to 50 ms per iteration, signalled by `PagedMMF.DecrementDirty` when a page's `DC` reaches 0. Each retry re-checks `BackpressureContext.ShouldGiveUp` (see [01-foundation §2](01-foundation.md)) against `TimeoutOptions.Current.PageCacheBackpressureTimeout`; if it expires, a `PageCacheBackpressureTimeoutException` propagates.

The factory hook on `PagedMMFOptions`:

```csharp
internal Func<IPageCacheBackpressureStrategy> BackpressureStrategyFactory { get; set; }
    = () => new WaitForIOStrategy();
```

It's `internal` — meant to be set by the engine's test harness, not by application code.

### `OnBackpressure → ForceCheckpoint`

Independent of the strategy, the moment the allocator decides backpressure is needed, it invokes `OnBackpressure?.Invoke()` — set by `DatabaseEngine` to `CheckpointManager.ForceCheckpoint`. This wakes the checkpoint thread immediately instead of waiting for the timer (default `CheckpointIntervalMs = 30 000`). The pipeline writes dirty pages, calls `DecrementDirty`, which calls `SignalPageAvailable`, which wakes the strategy's `ManualResetEventSlim`. End-to-end the worst case is one checkpoint cycle (typically tens of ms) + the strategy's wait granularity.

### `PageCacheGaugeSnapshot` — what the profiler sees

[`PageCacheGaugeSnapshot`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/PageCacheGaugeSnapshot.cs) is the per-tick sampled view exposed to the profiler. Mutually-exclusive buckets (`FreePages` / `CleanUsedPages` / `DirtyUsedPages` / `ExclusivePages`) sum to `TotalPages`; overlay counters (`EpochProtectedPages`, `PendingIoReads`) are independent and may include pages from any bucket. Workbench renders the buckets as a stacked area chart — the mutually-exclusive invariant is what keeps that visualization honest.

---

## 7. Page CRC & seqlock writes

Two mechanisms let the checkpoint snapshot a live page **consistently and verifiably** without blocking writers: a **seqlock counter** on every page detects in-flight writes, and a **CRC32C** on every page detects a torn write after a crash. There is **no FPI** — the Minimal-WAL redesign retired full-page images entirely; torn pages are healed by re-derivation or fail the open loudly (see [11-durability §6](11-durability.md)).

### The seqlock — `ModificationCounter`

Every page header has a `ModificationCounter : int`. Convention: **even = quiescent, odd = mid-modification**. `TryLatchPageExclusive` (≈ `PagedMMF.cs:1262`) bumps it from even → odd before any writes touch the payload; `UnlatchPageExclusive` (≈ `:1303`) bumps it odd → even after. Readers can detect a torn read by sampling the counter before *and* after their copy and rejecting the result on mismatch.

`CopyPageWithSeqlock` (≈ `:1697`) is the consumer used by checkpoint: it spins while the counter is odd, copies the page into staging, and re-checks the counter — if it changed, retry. There are **two skip conditions**: an odd counter on a page whose `PageState` is *not* `Exclusive` is a **stale** counter — no writer to wait for — and is skipped **immediately** (logged via `LogStaleSeqlockCounterSkip`); an odd counter held by a *real* exclusive writer for longer than the **100 ms** threshold is also skipped (the writer is hung or in backpressure). A skipped page holds the checkpoint's coverage gate back (CK-03) but keeps its dirty bit / DC so the next cycle re-captures it.

Critically — `InitHeader` in `LogicalSegment.cs` (≈ `:498`) **preserves `ModificationCounter` across header clears**. Zeroing it while a page is latched would leave the counter odd after unlatch — a quiescent page falsely advertising a write. The stale-counter guard above now skips such a page immediately rather than spinning, but preserving the counter (and the slot-reuse reset in `TryAcquire`) is still the correct invariant: a quiescent page must read even.

### The CRC — `PageChecksum`

Every page header also carries a `PageChecksum : uint`. It is a CRC32C ([`Crc32CUtil.ComputeSkipping`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Foundation) in `Foundation/`, hardware-accelerated, ~0.4 µs/8 KB) over the whole page *except* the 4-byte checksum field. The checkpoint stamps it **on the staging-buffer copy** at write time (so it reflects exactly the bytes that hit disk), and `EnsurePageVerified` checks it on load (`OnLoad` mode) and during recovery. The CRC helper lives in `Foundation/`, not `Storage/` — by design, the storage layer holds **zero** `Wal` / `Lsn` / `Fpi` identifiers (grep-enforced).

### Torn-page safety — detect, then rebuild or loud-fail (no FPI)

When `EnsurePageVerified` finds a stored CRC that doesn't match the recomputed one, the response depends on the page's role (full table in [11-durability §6](11-durability.md)):

- **Derived pages** (Index, Spatial, Occupancy) — discarded and **rebuilt** from primary data during recovery (RB-01 / CK-09). Always healable.
- **Primary pages** (component/revision content, EntityMap, cluster, collections, string table, system) — recorded **suspect**; after rebuild, a suspect page that no longer backs a live chunk is healed, but one that still backs a live primary chunk **fails the open loudly** (RB-04). A torn primary is never silently served as intact.
- **Structural meta / segment-directory pages** — protected by checkpoint v2's **A/B slot-pairing**: the current-valid slot is never overwritten, so a torn write can't destroy the only good copy.

<a href="assets/typhon-checkpoint-pipeline.svg">
  <img src="assets/typhon-checkpoint-pipeline.svg" width="812" alt="Checkpoint v2 pipeline">
</a>

---

## 8. Storage introspection

The Workbench's Database File Map (Module 15) reads the engine's storage state without touching data pages — everything comes from in-memory engine structures. The surface lives in [`StorageMapTypes`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/StorageMapTypes.cs) and on `ManagedPagedMMF` itself:

| Type | Purpose |
|---|---|
| `StoragePageType` enum | Semantic classification of one file page (`Unknown` / `Free` / `Root` / `Occupancy` / `Component` / `Revision` / `Index` / `Cluster` / `Vsbs` / `StringTable`) |
| `StorageSegmentKind` enum | Runtime role of a logical segment (`Component` / `Revision` / `Index` / `Cluster` / `Vsbs` / `StringTable` / `Occupancy` / `Other`) |
| `StorageSegmentDescriptor` | Read-only snapshot of one segment: root page, kind, owned pages, chunk-layout constants (stride, root/per-page chunk counts, data offsets) |

`ManagedPagedMMF` ([`ManagedPagedMMF.StorageMap.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/internals/ManagedPagedMMF.StorageMap.cs)) exposes:

- `StorageFilePageCount` — total file pages
- `StoragePageSize` — `8192`
- `ReadOccupancyBits(Span<long>)` — bulk-read the occupancy L0 bitmap into a caller-owned span

`DatabaseEngine.EnumerateStorageSegments` returns the list of `StorageSegmentDescriptor`s for Workbench rendering. Everything here is **read-only**, **no data-page I/O**, and **no allocation** beyond what the caller's `Span<long>` provides.

---

## 9. Configuration

[`PagedMMFOptions`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/PagedMMFOptions.cs) is the only configuration surface in this layer:

| Property | Default | Meaning |
|---|---|---|
| `DatabaseName` | `"TyphonDB"` | Logical name. Validated against `^[A-Za-z0-9_-]+$` and ≤ 63 UTF-8 bytes. |
| `DatabaseDirectory` | `Environment.CurrentDirectory` | Filesystem directory. Must exist. `DatabaseAbsoluteDirectory` returns the absolutized form. |
| `DatabaseFileName` | `DatabaseName` (if unset) | Logical file prefix; backing file becomes `<DatabaseFileName>.bin`. Same validation rules. |
| `DatabaseCacheSize` | `256 MiB` (`DefaultDatabaseCacheSize`) | Total page cache bytes. Must be a multiple of `PageSize`, between `MinimumCacheSize` (8 MiB) and 4 GiB. |
| `PagesDebugPattern` | `false` | Fill newly-allocated pages with a debug pattern (development/testing). |
| `BackpressureStrategyFactory` (internal) | `() => new WaitForIOStrategy()` | Test hook to substitute the backpressure strategy. |

Also exposed: `EnsureFileDeleted()` (best-effort delete of the backing file + lock file; for tests), `IsValid` / `Validate(silent, out validation)` (returns the multi-line error string or throws).

`ManagedPagedMMFOptions` is currently a marker subclass — same shape, separate type for DI binding.

The advisory lock file (`<DatabaseName>.lock` in `DatabaseDirectory`) is created on open and carries `{ pid, machineName, startedAt }` JSON. A stale lock (process gone) is silently removed; a live lock (or a foreign-machine lock that can't be verified) raises `DatabaseLockedException`.

---

## See also

- [01-foundation](01-foundation.md) — `EpochManager` / `EpochGuard` (page protection), `AccessControlSmall` (page state locks), `CacheLinePaddedInt` (clock hand), memory allocators (page cache pinning)
- [05-revision](05-revision.md) — MVCC revision storage uses `ChunkBasedSegment` + `ChunkAccessor` and threads its `ChangeSet` through writes
- [06-ecs](06-ecs.md) — `ComponentTable` is the ECS-side consumer of segments; `EpochRefreshInterval = 128` drives `ReleaseExcessDirtyMarks`
- [11-durability](11-durability.md) — the checkpoint v2 cycle; page CRC + seqlock snapshots; torn-page rebuild/loud-fail
- [13-resources](13-resources.md) — `PagedMMF` registers itself as a `ResourceNode` (memory + I/O metrics on the `Storage` subtree)
