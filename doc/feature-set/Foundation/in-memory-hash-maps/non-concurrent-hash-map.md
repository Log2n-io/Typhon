# Non-Concurrent HashMap\<TKey[, TValue]\>
> Single-threaded open-addressing hash set/map — the default replacement for `HashSet<T>`/`Dictionary<TKey,TValue>` on a hot path.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](../README.md)

## 🎯 What it solves
Most hot-path sets and maps in the engine are touched by exactly one thread at a time — a query's result set, a view's tracked entity-ID set, a per-system dirty-entity map rebuilt every tick. `Dictionary`/`HashSet` work, but each entry is a separate heap-tracked node and every lookup pays a `GetHashCode()`/modulo cost that's overkill for blittable keys like `long` entity IDs. `HashMap<TKey>` and `HashMap<TKey, TValue>` give the same semantics over a single flat, contiguous array with no per-entry allocation.

## ⚙️ How it works (in brief)
Entries are packed `[hash | key]` (set) or `[hash | key | value]` (map) in one array on the Pinned Object Heap, sized to a power of two and grown by doubling once the 0.75 load factor is crossed. Lookups probe linearly from the key's hash bucket; deletes use backward-shift to avoid leaving tombstones behind. If `TValue` contains managed references, values are stored in a parallel array instead of inline — transparent to the caller, just slightly slower than the fully-inline unmanaged path.

## 💻 Usage
```csharp
// Set: tracking a result set of entity IDs (the actual pattern used by EcsQuery's full-scan path)
var pkResult = new HashMap<long>();          // initialCapacity defaults to 64
reader.ExecuteFullScan(plan, evaluators, ct, tx, pkResult);

foreach (long pk in pkResult)
{
    var entityId = EntityId.FromRaw(pk);
    // ...
}
pkResult.Dispose();

// Map: caching a derived value per key
var cache = new HashMap<int, float>(256);
if (!cache.TryGetValue(key, out float cached))
{
    cached = cache.GetOrAdd(key, ComputeExpensiveValue(key));
}
cache.TryUpdate(key, newValue);              // no-op if key absent
cache[key] = newValue;                       // indexer adds-or-overwrites
cache.Dispose();
```

| Member | Effect |
|---|---|
| `TryAdd(key[, value])` | Insert if absent; `false` if the key already exists (value unchanged) |
| `Contains(key)` / `TryGetValue(key, out value)` | O(1) average lookup |
| `TryRemove(key[, out value])` | Backward-shift delete, no tombstone |
| `GetOrAdd(key, value)` | Atomic-within-thread get-or-insert (map only) |
| `EnsureCapacity(n)` | Pre-grow to avoid resizes during a known-size bulk build |
| `Clone()` | Independent snapshot copy — used for old-set/new-set delta comparisons (set only) |
| `GetPartitionEnumerator(i, n)` | Contiguous index-range slice for manual parallel iteration (set only) |

## ⚠️ Guarantees & limits
- Single-threaded only — no internal synchronization; concurrent access from multiple threads is undefined behavior. Use [ConcurrentHashMap](./concurrent-hash-map.md) instead.
- `TKey` must be `unmanaged, IEquatable<TKey>`; `TValue` is unconstrained on the map variant.
- O(1) average for add/lookup/remove; worst case O(n) under pathological hash collisions (not expected with the built-in hash strategy).
- Lookup is ~1.5-3x faster than `Dictionary<TKey,TValue>` in measured benchmarks; insert is roughly on par (resize cost dominates at small sizes).
- Entries live in a GC-pinned array, not unmanaged memory — `Dispose()` drops the reference but the GC still reclaims it; disposal isn't strictly required for correctness, only for prompt release.
- `internal` (`Typhon.Engine.Internals`) — engine plumbing only.

## 🧪 Tests
- [HashMapTests](../../../../test/Typhon.Engine.Tests/Collections/InMemoryHashMapTests.cs) — `TryAdd`/`Contains`/`TryGetValue`/`TryRemove`/`GetOrAdd`/`Clone()`, backward-shift delete, resize at 0.75 load factor.
- [HashMapPartitionTests](../../../../test/Typhon.Engine.Tests/Collections/HashMapPartitionTests.cs) — `GetPartitionEnumerator` contiguous-range slicing for manual parallel iteration.

## 🔗 Related
- Parent feature: [In-Memory Hash Maps](./README.md)
- Sibling: [ConcurrentHashMap](./concurrent-hash-map.md) — same design, striped for safe concurrent access when a hot path is shared across threads.

<!-- Deep dive: claude/design/Foundation/Collections/in-memory-hash-map.md §8.1-8.2, claude/overview/11-utilities.md §H.1 -->
