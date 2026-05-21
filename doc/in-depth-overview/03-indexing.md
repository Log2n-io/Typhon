# 03 — Indexing

**Code:** [`src/Typhon.Engine/Indexing/`](../../src/Typhon.Engine/Indexing/)

Indexing in Typhon is one mechanism: a **B+Tree** specialised at compile time for the key width. There is no separate "primary key index", "secondary index" or "uniqueness constraint" implementation — the same `BTree<TKey, TStore>` powers all three. The variants (`L16BTree`, `L32BTree`, `L64BTree`, `String64BTree`) exist purely to size the node layout to the key width so that every node is exactly **256 bytes** (or 64 B for `String64BTree`), keying capacity off the key size rather than off some configured fan-out.

The tree is **concurrent** by design: readers descend lock-free via Optimistic Lock Coupling ([`OlcLatch`](../../src/Typhon.Engine/Indexing/internals/OlcLatch.cs)), writers use a two-phase spin-then-yield lock that never pays the Windows 15 ms timer-tick penalty, and obsolete nodes are reclaimed via epoch deferral ([01-foundation §4](01-foundation.md)). It's also a **B-link tree** — every node carries a `HighKey` upper bound and a `NextChunk` pointer so a writer can split a node without coordinating with traversing readers; the readers follow the right-link to find the key that's now on the new sibling.

If you've used the engine before, you've used this code path. Every PK lookup, every secondary-index probe, every range scan in the query planner goes through it.

---

## 1. Overview — one tree, many uses

`BTree<TKey, TStore>` is the universal index. The same class instance backs:

- **Primary key indexes** on `ComponentTable` (one per component type).
- **Secondary indexes** declared by `[Indexed]` on schema fields.
- **Uniqueness constraints** (`[Unique]` is a unique secondary index — `AllowMultiple = false`).
- **Multi-value indexes** (`AllowMultiple = true`) — values per key are stored in a `VariableSizedBufferSegment` whose buffer head ID lives in the BTree's value slot.

The `TStore` generic threads through to [`IPageStore`](../../src/Typhon.Engine/Storage/internals/IPageStore.cs) — concretely `PersistentStore` (WAL-backed, durable) or `TransientStore` (in-memory only, no WAL). The BTree code is identical for both; the store dictates whether mutations get journalled.

The user-facing handle is [`IndexRef`](../../src/Typhon.Engine/Indexing/public/IndexRef.cs) — opaque, reusable, with a captured `IndexLayoutVersion` for O(1) staleness detection after schema evolution. Resolve once via `DatabaseEngine.GetPKIndexRef<T>()` / `GetIndexRef<T, TKey>()`, then reuse on the hot path.

---

## 2. Node layout — 256 B = 4 cache lines

Every node is one segment chunk, and the segment's stride is exactly the node struct size. Nodes are 256 bytes for all numeric-keyed variants (filling four cache lines) and 64 bytes for the String64 variant.

The 256 B size isn't arbitrary: modern CPUs (Zen 4+, recent Intel) have an **Adjacent Line Prefetcher** that pulls the paired 64 B cache line within a 128 B region. Two ALP triggers therefore cover the full node — a node descent fetches one entry's data with at most two cache-line latencies, not four.

Every node carries the same header fields at the same offsets (so a generic `BaseNodeStorage` can address them uniformly):

| Field | Type | Purpose |
|---|---|---|
| `Control` | int (4 B) | Packed: `StateFlags` (lo 16 b) ∣ `ContentionHint` (b16-23) ∣ `Start` (b24-31, ring-buffer head) ∣ `Count` (b32-39) — wait, `Count` is byte 3 of the int. The packing is byte-3=Count, byte-2=Start, byte-1=ContentionHint, low 16 b = StateFlags. |
| `OlcVersion` | int (4 B) | OLC latch state — bit 0 locked, bit 1 obsolete, bits 2-31 monotonic version counter |
| `PrevChunk` | int (4 B) | Left sibling at leaf level (doubly-linked leaf chain) |
| `NextChunk` | int (4 B) | Right sibling — the B-link |
| `LeftValue` | int (4 B) | Leftmost child pointer (internal nodes); reused as buffer head field at leaves of multi-value trees |
| `HighKey` | TKey-shaped | B-link upper bound — exclusive separator. `node.HighKey == node.GetNext().GetFirst().Key` is the invariant. |
| `Keys[Capacity]` | TKey × N | Key array, contiguous |
| `Values[Capacity]` | int × N | Value array — child chunk IDs (internal) or record IDs / buffer heads (leaves) |

`Start` and `Count` together describe a **rotated ring buffer** within the key/value arrays: PushFirst/PushLast/Spill-Left/Spill-Right amortize key shuffling by rotating the head rather than memmoving the whole array. `IsRotated` is true iff `Start + Count > Capacity`. The `Adjust(i)` helper folds an index back into `[0, Capacity)`.

### `BTree<TKey, TStore> : BTreeBase<TStore>`

[`BTree.cs`](../../src/Typhon.Engine/Indexing/internals/BTree.cs), [`BTreeBase.cs`](../../src/Typhon.Engine/Indexing/internals/BTreeBase.cs)

`BTreeBase<TStore>` is the non-generic surface used by `ComponentTable`, the query planner, and `IndexedFieldInfo` — it doesn't know `TKey`. `BTree<TKey, TStore>` is the typed implementation; concrete instantiations (e.g. `IntSingleBTree<TStore>`, `LongMultipleBTree<TStore>`) bind both type parameters and pick a node-storage strategy.

Key invariants and constants:

| Constant | Value | Meaning |
|---|---|---|
| `MaxTreeDepth` | 32 | Maximum descent depth — bounds stack-allocated path buffers |
| `MaxOptimisticRestarts` | 3 | OLC reader restart budget before falling back to pessimistic |
| `ContentionSplitThreshold` | 3 | `ContentionHint` value at which a hot leaf gets proactively split |
| `DirectoryChunkCount` | 4 | Reserved chunks at the start of the segment for the BTree directory |
| `MaxDirectoryEntries` | 20 | Hard cap on indexes per shared segment (PK + 19 secondary, or 20 standalone) |

---

## 3. Variants & per-node capacities

Each numeric variant is a 256 B struct sized so the key/value arrays plus the fixed 28 B of metadata + HighKey fit exactly. Capacities follow directly from key width:

| Variant | Key types | Key size | Capacity | Node size | Source |
|---|---|---|---|---|---|
| `L16BTree` | `sbyte`, `byte`, `short`, `ushort`, `char` | 2 B (slot-wise) | **38** | 256 B | [`L16BTree.cs`](../../src/Typhon.Engine/Indexing/internals/L16BTree.cs) — `Index16Chunk.Capacity = 38` |
| `L32BTree` | `int`, `uint`, `float` | 4 B | **29** | 256 B | [`L32BTree.cs`](../../src/Typhon.Engine/Indexing/internals/L32BTree.cs) — `Index32Chunk.Capacity = 29` |
| `L64BTree` | `long`, `ulong`, `double` | 8 B | **19** | 256 B | [`L64BTree.cs`](../../src/Typhon.Engine/Indexing/internals/L64BTree.cs) — `Index64Chunk.Capacity = 19` |
| `String64BTree` | `String64` | 64 B | **4** | 64 B × 5 segments | [`String64BTree.cs`](../../src/Typhon.Engine/Indexing/internals/String64BTree.cs) — `IndexString64Chunk.Capacity = 4` |

The L16 storage is slightly subtle: keys are stored as 2 B regardless of whether `TKey` is 1 B (sbyte/byte) or 2 B (short/ushort/char). The variant exists per key type to give the JIT a monomorphised search routine.

### Why these capacities and not "the obvious" ones

L16 could fit a few more 2 B keys than 38 if you packed harder, and L64 only fits 19 because the 8 B `HighKey` plus the 8 B keys (×19 = 152) plus the 4 B values (×19 = 76) plus 24 B of header = 260 B — the storage authors keep `HighKey` co-located with `OlcVersion` in the first 128 B region (so OLC readers and B-link gap checks touch one ALP-paired cache line) and let that constrain capacity. The trade is honest: lower fan-out per node but every concurrent-read fast path takes one cache miss, not two.

### Per-key concrete classes

Each variant gives two final classes per key type: `XSingleBTree` (`AllowMultiple = false`) and `XMultipleBTree` (`AllowMultiple = true`). The multi-value classes store values in a side `VariableSizedBufferSegment<int, TStore>` and put the buffer-head ID in the BTree's value slot.

### SIMD search

All variants implement `BinarySearch` with a SIMD-accelerated `CountLessThan` over the key array — `Vector256` (8 × int / 16 × short / 4 × long), falling back to `Vector128` and then scalar. At leaf capacities of 19–38, the SIMD path resolves a search in 1–3 vector ops. The B-link `HighKey` lookups use the same path.

---

## 4. Concurrency

Two primitives, two regimes.

### Readers — `OlcLatch` (optimistic, lock-free)

[`OlcLatch.cs`](../../src/Typhon.Engine/Indexing/internals/OlcLatch.cs)

`OlcLatch` is a 32-bit field embedded in every node's `OlcVersion` slot:

| Bits | Field |
|---|---|
| 0 | Locked (writer holds the latch) |
| 1 | Obsolete (node was merged away — readers must restart) |
| 2–31 | Version counter (30 bits, ~1.07 B versions before wrap) |

The reader API performs zero writes to shared state. `ReadVersion()` returns the version or `0` if the node is locked or obsolete (signalling restart). The reader threads the version through its work and calls `ValidateVersion(expected)` afterwards — if the version changed, the read might have been torn and the reader restarts. On x64 (TSO), no memory barriers are needed — loads aren't reordered with other loads, and the write-lock CAS provides the acquire/release on the writer side.

This is plain **Optimistic Lock Coupling** (Leis et al., 2016) — see [01-foundation](01-foundation.md) for how it composes with the broader synchronization story.

A failed validation emits a `Concurrency:OlcLatch:ValidationFail` event ([12-observability](12-observability.md)). Lookup retries up to `MaxOptimisticRestarts = 3` times before falling back to a pessimistic loop that retries indefinitely.

### Writers — `SpinWriteLock` (two-phase, yield-capped)

[`BTree.cs SpinWriteLock`](../../src/Typhon.Engine/Indexing/internals/BTree.cs)

Acquiring a writer latch is via `OlcLatch.TryWriteLock()` which `CAS`-sets bit 0. If that fails, `SpinWriteLock` walks two phases — **deliberately avoiding `Thread.Sleep(1)`**, which on Windows stalls for ~15 ms (one timer-tick), an eternity at the latch hold times this code targets.

```csharp
// Phase 1: tight PAUSE loop — 64 iterations of Thread.SpinWait(1).
// On Zen 4 a PAUSE is ~50 cycles → ~100 ns total. On Skylake+ closer to ~2 µs.
// Covers the common case of a leaf insert/delete on another core completing.
for (int i = 0; i < 64; i++) {
    Interlocked.Increment(ref _writeLockFailures);
    Thread.SpinWait(1);
    if (latch.TryWriteLock()) return false; // got it, with contention
}

// Phase 2: SpinWait with sleep1Threshold = -1 (Sleep(1) DISABLED).
// Escalates to Thread.Yield / Thread.Sleep(0) only — never Sleep(1).
// Holder is likely doing a split/merge or sharing our SMT core.
SpinWait spin = default;
do {
    Interlocked.Increment(ref _writeLockFailures);
    spin.SpinOnce(-1);
} while (!latch.TryWriteLock());
```

Phase 1 is calibrated for typical OLC latch hold times (~100–500 ns — a single key compare + array shift). Phase 2 is for the rare cases where the holder is doing a node split/merge (which takes microseconds, not nanoseconds), or where threads share an SMT core. The `-1` threshold on `SpinWait.SpinOnce` is the magic that disables `Sleep(1)` while still permitting `Yield()` / `Sleep(0)`.

`_writeLockFailures` is a diagnostic counter, surfaced via the `WriteLockFailures` property.

### Obsolete-node reclamation

When a merge removes a node, its chunk can't be freed immediately — concurrent readers may still hold the chunk ID. The BTree maintains a per-instance `DeferredNodeList` (inline buffer of 8 + overflow `List`); the merged-out chunk is recorded with `RetireEpoch = EpochManager.GlobalEpoch`, and reclamation happens when `EpochManager.MinActiveEpoch > RetireEpoch`. Reclamation runs every 64 mutations (the `_deferredReclaimSkip` counter) and on UoW dispose.

This is the same epoch model as the page cache ([01-foundation §4](01-foundation.md)) — reused, not reinvented.

---

## 5. Operations

All operations live on `BTree<TKey, TStore>`. Public entry points work in two passes: a fast OLC path that does no writes to shared state, then a pessimistic fallback that takes write locks.

### `Add(key, value, ref accessor, out bufferRootId)` — Insert

[`BTree.Insert.cs`](../../src/Typhon.Engine/Indexing/internals/BTree.Insert.cs)

OLC fast path: descend optimistically, validate the leaf version, `TryWriteLock` the leaf, retry-on-CAS-failure or fall through. The slow path `InsertIterative` descends with full path recording, handles overflow by spill-left → spill-right → split, and propagates separator-key updates back up the path.

Key behaviours:

- **Spill before split**: when a leaf is full, it tries to push items to a neighbour first (`spillLeft` / `spillRight`) — a half-full target threshold reduces follow-up `LeafFull` frequency for sequential-append workloads (else every subsequent insert would trigger another full→spill cycle).
- **Contention split**: a leaf with `ContentionHint >= ContentionSplitThreshold = 3` and `>= Capacity/2` items is *proactively* split even when not full — breaks up hotspots before they cause OLC restart storms. Counted via `ContentionSplitCount`.
- **Multi-value indexes**: `AllowMultiple = true` allocates a `VariableSizedBufferSegment` buffer per key on first insert, returns the buffer's HEAD chunk ID via `out bufferRootId`; subsequent inserts append to the buffer (`_storage.Append`).
- **Cached last-key fast path**: `_cachedLastKey` is checked for sequential-append workloads (PK indexes during bulk load); a hit skips the BTree descent.

Returns the `ElementId` (slot index within the buffer for multi-value; record ID for single).

### `Remove(key, out value, ref accessor)` — Delete

[`BTree.Remove.cs`](../../src/Typhon.Engine/Indexing/internals/BTree.Remove.cs)

OLC fast path: descend optimistically, find the leaf, `TryWriteLock` and remove if no SMO is required. Pessimistic fallback `RemoveCorePessimistic` is full path recording, with `borrow-left` → `borrow-right` → `merge-left` → `merge-right` for underflow.

For **multi-value indexes**, `Remove(key, ...)` removes the *whole key* (and its buffer). To remove a single value from a multi-value key, use `RemoveValue` below.

### `TryGet(key, ref accessor)` — Lookup

[`BTree.cs TryGet`](../../src/Typhon.Engine/Indexing/internals/BTree.cs)

Pure OLC: descent reads versions only, never writes. Returns `Result<int, BTreeLookupStatus>` (`Found`/`NotFound`). After `MaxOptimisticRestarts = 3` failed restarts, falls through to `TryGetPessimistic` which loops on OLC indefinitely (still no writes — pessimistic here means "no retry budget", not "take a lock").

For multi-value indexes, use `TryGetMultiple` which returns a `VariableSizedBufferAccessor<int, TStore>` over the value list.

### `EnumerateRange(min, max)` / `EnumerateRangeDescending` / `EnumerateRangeMultiple` — RangeScan

[`BTree.RangeEnumerator.cs`](../../src/Typhon.Engine/Indexing/internals/BTree.RangeEnumerator.cs)

A `ref struct` enumerator that seeks the start leaf and walks the leaf-chain forward (via `NextChunk`) or backward (via `PrevChunk`). Per-leaf OLC validation: read the leaf version before reading the items, validate after; on validation failure, re-read just that leaf from the beginning/end.

Variants:

- `EnumerateRange(min, max)` — bounded forward, unique-only (throws on `AllowMultiple`)
- `EnumerateRangeDescending(min, max)` — bounded reverse
- `EnumerateRangeMultiple(min, max)` — bounded forward, multi-value only, expands buffers
- `EnumerateRangeMultipleDescending(min, max)` — bounded reverse, multi-value
- `EnumerateLeaves()` — unbounded forward, walks the entire leaf chain

The caller **must be inside an epoch scope** ([01-foundation §4](01-foundation.md)) — without that, pages backing leaves could be evicted mid-scan.

### `RemoveValue(key, elementId, value, ref accessor, preserveEmptyBuffer)` — multi-value removal

[`BTree.cs RemoveValue`](../../src/Typhon.Engine/Indexing/internals/BTree.cs)

This is **not** "remove by key" — `RemoveValue` removes *one value from a multi-value key's buffer*. Important: a multi-value index entry holds many values per key (e.g., "all entities with `Status = Active`"), and the engine often needs to remove one element while leaving the rest of the buffer intact.

Flow:

1. `FindLeaf(key)` — under OLC, then `WriteLock` the leaf for an authoritative read.
2. Re-find the key under lock (OLC concurrent removes may have shifted indices).
3. Read the buffer ID from the leaf value slot.
4. `_storage.RemoveFromBuffer(bufferId, elementId, value)` — variable-sized-buffer-segment delete.
5. WriteUnlock the leaf (version bump now visible to OLC readers).
6. If the buffer is now empty *and* `preserveEmptyBuffer == false`, remove the BTree key entry too (via `RemoveCorePessimistic`) and delete the buffer.

The `preserveEmptyBuffer = true` mode keeps the key alive even when the buffer is empty — needed by temporal indexes ([`TemporalIndexQuery.cs`](../../src/Typhon.Engine/Indexing/internals/TemporalIndexQuery.cs), [`VersionedIndexEntry.cs`](../../src/Typhon.Engine/Indexing/internals/VersionedIndexEntry.cs)) where the HEAD buffer chains to a TAIL of older versions; dropping the HEAD would unlink the entire history.

### `Move(oldKey, newKey, value)` / `MoveValue(...)` — compound move

[`BTree.Move.cs`](../../src/Typhon.Engine/Indexing/internals/BTree.Move.cs)

Atomically removes from one key and inserts under another — used when an indexed field changes value on update. Same-leaf fast path takes one write lock; different-leaf path locks the two leaves in `ChunkId` order to avoid deadlock. Falls back to a fully pessimistic remove+insert pair under contention.

---

## 6. Structural mutations

Two SMOs (Structure Modification Operations) extend or shrink the tree's shape.

### NodeSplit

[`BTree.NodeWrapper.cs SplitRight`](../../src/Typhon.Engine/Indexing/internals/BTree.NodeWrapper.cs), per-variant `SplitRight` in [`L16BTree.cs`](../../src/Typhon.Engine/Indexing/internals/L16BTree.cs) et al.

Triggered when a leaf is full and neither neighbour can absorb a spill (or under contention with a hot leaf at ≥ half capacity). Allocates a new right sibling, moves the upper half of the items to it, and:

- Updates the leaf chain: `left.Next → right`, `right.Next → oldNext`, mirror for `Prev`.
- **B-link HighKey update**: `right.HighKey = left.oldHighKey` (right inherits the upper bound); `left.HighKey = right.GetFirst().Key` (left's new upper bound is the new separator).
- Returns the new right node + a separator key to be promoted to the parent.

The parent-side promotion happens iteratively in `InsertIterative` — if the parent overflows, it splits too; if the root splits, a new root is allocated (under the root's write lock, to serialize concurrent root creators).

Counted via `SplitCount` and (for contention-triggered splits) `ContentionSplitCount`.

### NodeMerge

[`BTree.NodeWrapper.cs MergeLeft`](../../src/Typhon.Engine/Indexing/internals/BTree.NodeWrapper.cs), per-variant `MergeLeft` in `L16BTree.cs` et al.

Triggered when a node falls below half capacity and neighbours can't borrow more (`borrow-left`/`borrow-right` are tried first). Merges the right node into the left, marks the right as obsolete (via `OlcLatch.MarkObsolete`), and adds it to the `DeferredNodeList` for epoch-deferred reclamation. The left's `HighKey` inherits the right's, preserving the B-link invariant.

Cascading merges propagate upward iteratively. If the root collapses to one child, the child becomes the new root and the old root is deferred-freed.

Counted via `MergeCount`.

---

## 7. Multi-tree segments — the BTree directory

A single `ChunkBasedSegment<TStore>` can host **multiple B+Trees** — most commonly the per-component PK index plus all of that component's secondary indexes share one segment, which keeps page-cache locality high. The directory mechanism makes this work.

### Layout

The first **4 chunks** of every BTree-bearing segment (`DirectoryChunkCount = 4`) are reserved for the directory. A maximum of **20 entries** fits across those four chunks (`MaxDirectoryEntries = 20`).

```
Chunk 0:  [ BTreeDirectoryHeader (2 B EntryCount) ] [ entry₀ ][ entry₁ ]...
Chunk 1:  [ entry_n ][ entry_n+1 ] ...
Chunk 2:  [ ... ]
Chunk 3:  [ ... ]
```

### [`BTreeDirectoryHeader`](../../src/Typhon.Engine/Indexing/internals/BTree.cs)

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct BTreeDirectoryHeader {
    public ushort EntryCount;   // how many BTrees registered in this segment
}
```

### [`BTreeDirectoryEntry`](../../src/Typhon.Engine/Indexing/internals/BTree.cs) — 12 bytes per BTree

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct BTreeDirectoryEntry {
    public short StableId;      // -1 = PK, FieldId = secondary, 0 = standalone/test
    public short Reserved;
    public int   RootChunkId;
    public int   Count;
}
```

`StableId` is the lookup key: `-1` is the convention for the PK index, positive values are `FieldInfo.FieldId` for secondary indexes, `0` is for standalone/test trees. Each `BTree` instance caches its directory location in `_dirChunkId` + `_dirEntryOffset` at construction (`RegisterInDirectory` for create, `FindInDirectory` for load) so `SyncHeader` is an O(1) write — no scan.

Importantly: each BTree on a shared segment has its **own** root and count in the directory entry. Without this, multiple BTrees sharing a segment would corrupt each other's bookkeeping.

---

## 8. Telemetry spans

B+Tree mutation paths emit typed events ([12-observability](12-observability.md)) — the events are JIT-eliminated when their gates are off. Definitions live in [`BTreeEvents.cs`](../../src/Typhon.Engine/Profiler/internals/BTreeEvents.cs):

| Span event | Where emitted | Payload |
|---|---|---|
| `BTreeInsert` | `BTree.Add` entry | none — operation identity carried by the kind |
| `BTreeDelete` | `BTree.Remove`, `BTree.RemoveValue` entries | none |
| `BTreeNodeSplit` | `NodeWrapper.SplitRight` (wraps the storage call) | none |
| `BTreeNodeMerge` | `NodeWrapper.MergeLeft` (wraps the storage call) | none |

These are **payload-less spans** — 37 B header, 53 B with trace context. The event kind alone identifies the operation; finer-grained labelling would balloon ring-buffer cost on inserts that can fire at ~1 M/s in bulk-load workloads.

### What's not instrumented (and why)

- **`TryGet` / `TryGetMultiple` (lookup)** — deliberately uninstrumented. A primary-key lookup is the engine's tightest hot path (multiple millions per second under load); the cost of a span begin/end pair is observable in microbenchmarks. Lookups are inferred from caller-level spans (`Entity.Read`, query planner) instead.
- **OLC restarts** — counted via `OptimisticRestarts` / `PessimisticFallbacks` properties on the BTree, not per-event. Validation failures *do* emit a `Concurrency:OlcLatch:ValidationFail` event from `OlcLatch.ValidateVersion`.

### What's instrumented but gated

- **`EnumerateRange*` (range scans)** — `Data:Index:BTree:RangeScan` is a Tier-2-gated span. Off by default (`TelemetryConfig.DataIndexBTreeRangeScanActive`); when enabled, it records `ResultCount` and `RestartCount` per scan. Costs ~10 ns per scan when off (a single static bool check).
- **Latch operations** — `OlcLatch` emits `Concurrency:OlcLatch:WriteLockAttempt`, `WriteUnlock`, `MarkObsolete`, `ValidationFail` — all Tier-2-gated, all off in normal operation. Useful for diagnosing OLC contention storms.

---

## See also

- [01-foundation](01-foundation.md) — `OlcLatch`/`SpinWait`/`EpochManager` are described in their primitive form; this doc shows how the BTree composes them.
- [02-storage](02-storage.md) — `IPageStore`, `ChunkBasedSegment`, `ChunkAccessor`. BTree nodes are chunks; everything here is built on top of those.
- [05-revision](05-revision.md) — B+Trees host the revision-chain indexes used by MVCC (`TemporalIndexQuery`, `VersionedIndexEntry` — the multi-value preservation in `RemoveValue` is for these).
- [12-observability](12-observability.md) — typed event kinds, gating, span shapes (`BTreeInsertEvent` et al.).
