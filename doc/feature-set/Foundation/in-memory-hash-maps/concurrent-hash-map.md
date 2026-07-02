# ConcurrentHashMap\<TKey[, TValue]\>
> Striped, lock-free-read hash set/map — the replacement for `ConcurrentDictionary<TKey,TValue>` on a shared hot path.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](../README.md)

## 🎯 What it solves
Some hot-path collections are genuinely shared across threads — tracking destroyed chunk IDs during concurrent commits, for instance. `ConcurrentDictionary` handles this but allocates a node per entry and serializes every write through internal segment locks; under heavy contention that shows up directly in commit latency. `ConcurrentHashMap<TKey[, TValue]>` gives the same open-addressing/backward-shift design as the non-concurrent variant, partitioned into independent stripes so unrelated keys never contend, with reads that touch no shared, mutable state at all.

## ⚙️ How it works (in brief)
The table is split into independent stripes (at least 64, scaled with core count), each a self-contained open-addressing table selected by the top bits of the key's hash. Reads are lock-free: they snapshot a per-stripe version, probe, then re-check the version — if a writer raced in, the read retries. Writes take a per-stripe CAS lock (spin-then-yield, never an OS wait), so two writers only contend if they land on the same stripe. Resize happens per-stripe under that stripe's own lock; every other stripe stays fully accessible.

## 💻 Usage
```csharp
// Set: lock-free tracking shared across concurrent commit threads
// (the actual pattern used by ComponentTable for destroyed-chunk tracking)
private readonly ConcurrentHashMap<int> _destroyedChunkIds = new(64);

void TrackDestroyedChunkId(int chunkId) => _destroyedChunkIds.TryAdd(chunkId);
bool wasDestroyed = _destroyedChunkIds.Contains(chunkId);   // lock-free read

// Map: shared cache keyed by entity ID, read-heavy / write-light
var cache = new ConcurrentHashMap<long, float>(1024);
if (!cache.TryGetValue(entityId, out float cached))         // lock-free
{
    cached = cache.GetOrAdd(entityId, ComputeExpensiveValue(entityId));
}
cache.TryUpdate(entityId, newValue, comparisonValue);        // CAS-style conditional update
cache.Dispose();
```

| Member | Concurrency |
|---|---|
| `Contains(key)` / `TryGetValue(key, out value)` / indexer getter | Lock-free (OLC) |
| `TryAdd` / `TryRemove` / `GetOrAdd` / `TryUpdate` / indexer setter | Per-stripe CAS lock |
| `Clear()` | Acquires **all** stripe locks, in order |
| `Count` | Approximate — sums per-stripe counts without locking |
| `EnsureCapacity(n)` | Locks and grows individual stripes as needed |

## ⚠️ Guarantees & limits
- `TKey` must be `unmanaged, IEquatable<TKey>`; `TValue` is unconstrained on the map variant.
- Lock-free reads never block and never write shared state; they retry (cheaply) only if a write raced on the same stripe during the probe.
- Writes only contend when two threads hash to the *same* stripe — with 64+ stripes, collision probability per individual write stays in the low single digits even at 16 concurrent writers.
- `Count`/`StripeCount` are diagnostic, not transactional — `Count` can be stale by the time it returns under concurrent mutation.
- Measured 3-4x faster than `ConcurrentDictionary` on disjoint-key inserts across thread counts, ~25-35% faster on a 90/10 read/write mix.
- Enumeration (`foreach`) is best-effort and unlocked — it may observe a partial state under concurrent writes; don't use it where a consistent snapshot is required.
- Entries live in GC-pinned arrays (Pinned Object Heap); `Dispose()` releases all stripes' references.
- `internal` (`Typhon.Engine.Internals`) — engine plumbing only.

## 🧪 Tests
- [ConcurrentHashMapTests](../../../../test/Typhon.Engine.Tests/Collections/ConcurrentInMemoryHashMapTests.cs) — striped lock-free reads (OLC retry on a raced write), per-stripe CAS writes, `Clear()` all-stripe locking, concurrent disjoint-key inserts.

## 🔗 Related
- Parent feature: [In-Memory Hash Maps](./README.md)
- Sibling: [Non-Concurrent HashMap](./non-concurrent-hash-map.md) — same open-addressing/backward-shift design for single-threaded hot paths.

<!-- Deep dive: claude/design/Foundation/Collections/in-memory-hash-map.md §7-8.3-8.4, claude/overview/11-utilities.md §H.1 -->
