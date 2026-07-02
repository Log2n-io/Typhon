# Page-Backed Linear Hash Map
> O(1) exact-match key/value index, persisted in fixed-size chunks, with crash-safe rebuild instead of WAL logging.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](./README.md)

## 🎯 What it solves

Some indexes only ever need exact-match lookup — no range scans, no ordering — and for those, walking a B+Tree's `O(log n)` node chain on every lookup spends cycles a flat hash table wouldn't. `PagedHashMap<TKey, TValue, TStore>` is that flat alternative: an `O(1)` average-case lookup/insert/delete structure that lives in the same page-backed storage as everything else in the engine, grows incrementally (no stop-the-world resize), and survives a crash without ever touching the WAL. Its first consumer is spatial Layer-1 occupancy: "is this grid cell occupied, and by what" — a pure exact-match question at high update rate.

## ⚙️ How it works (in brief)

Entries are grouped into fixed-size bucket chunks addressed by `hash(key) mod bucketCount`; each bucket grows an overflow chain only when its chunk fills up. The map grows one bucket at a time (linear hashing) instead of doubling the whole table at once, so no single insert pays for a full rehash. Each bucket chunk carries its own optimistic-concurrency (OLC) version field, so reads are lock-free and writers only ever serialize with other writers on the *same* bucket — contention on unrelated keys never blocks. The map keeps no write-ahead log entries of its own: because it is fully derived from other durable state (e.g., the entity data it indexes), a crash is handled by rebuilding it from that source rather than replaying a log.

## 💻 Usage

`PagedHashMap<TKey, TValue, TStore>` is `internal` — engine plumbing consumed by other subsystems (currently `SpatialIndexState.OccupancyMap`), not called from application code. A consumer creates one over a dedicated `ChunkBasedSegment` and drives it through a `ChunkAccessor`:

```csharp
// One hash map owns its segment exclusively (chunk 0 is reserved for its meta).
using var guard = EpochGuard.Enter(epochManager);
var segment = new ChunkBasedSegment<PersistentStore>(epochManager, new PersistentStore(mmf), stride: 256);
var occupancyMap = PagedHashMap<long, int, PersistentStore>.Create(
    segment, initialBuckets: 64, allowMultiple: false, changeSet);

// Insert / lookup / remove, all under a ChunkAccessor scope.
var accessor = segment.CreateChunkAccessor(changeSet);
try
{
    long cellKey = PackCellKey2D(cellX, cellY);
    occupancyMap.Insert(cellKey, entityCount, ref accessor, changeSet);

    if (occupancyMap.TryGet(cellKey, out int count, ref accessor))
    {
        // cell is occupied
    }

    occupancyMap.Remove(cellKey, out _, ref accessor, changeSet);
}
finally
{
    accessor.Dispose();
}

// Reconnect to a persisted map after restart.
var reopened = PagedHashMap<long, int, PersistentStore>.Open(segment);
```

| Option | Default | Effect |
|---|---|---|
| `initialBuckets` (`Create`) | 64 | Initial bucket count; must be a power of 2. Higher avoids early splits for known-large maps. |
| `allowMultiple` | `false` | `true` stores multiple values per key via an internal variable-sized buffer; changes `Insert`/`Remove` semantics and disables `Upsert`. |

## ⚠️ Guarantees & limits

- **O(1) average lookup/insert/delete** — typically 2-3 chunk reads per operation, versus a B+Tree's `O(log n)` node chain.
- **No range or prefix queries** — exact-match only; reach for the B+Tree index variants when ordering matters.
- **Lock-free reads, per-bucket write serialization** — writers on different buckets never block each other; only same-bucket writers contend.
- **Gradual growth** — splits one bucket at a time as load factor crosses 0.75, never a stop-the-world full rehash.
- **No WAL participation** — the map is a derived structure; crash recovery rebuilds it from its authoritative source rather than replaying log entries. It must never be the only copy of data it indexes.
- **`internal` type** — not part of the public surface; not yet wired into the `[Index]` attribute / `ComponentTable` index system (planned future integration).
- One hash map owns its `ChunkBasedSegment` exclusively — chunk 0 is hardcoded as its meta chunk, so multiple maps cannot share a segment.

## 🧪 Tests
- [PagedHashMapTests](../../../test/Typhon.Engine.Tests/Data/HashMapTests.cs) — meta/directory struct layout, linear-hash bucket resolution across splits, `Create`/`Open` round-trip, insert/lookup/remove over a `ChunkAccessor`.

## 🔗 Related

- Compare: [Specialized B+Tree Key-Size Variants](../Indexing/btree-key-variants.md) — the ordered/range-query alternative
- Sibling: [In-Memory Hash Maps](./in-memory-hash-maps/README.md) — same open-addressing/backward-shift hashing family, kept in pinned memory instead of page storage.

<!-- Deep dive: claude/design/Foundation/Collections/linear-hash-map.md -->
