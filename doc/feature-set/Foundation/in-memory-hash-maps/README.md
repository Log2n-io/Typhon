---
uid: feature-foundation-in-memory-hash-maps-index
title: 'In-Memory Hash Maps'
description: 'Open-addressing hash set/map types that replace HashSet/Dictionary/ConcurrentDictionary on hot paths, with near-zero GC pressure.'
---

# In-Memory Hash Maps
> Open-addressing hash set/map types that replace `HashSet`/`Dictionary`/`ConcurrentDictionary` on hot paths, with near-zero GC pressure.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](../README.md)

## 🎯 What it solves
Engine-internal hot paths build and probe sets/maps constantly — entity-ID sets for a query result, a view's tracked entities, destroyed-chunk tracking during a commit — and .NET's general-purpose collections aren't built for this: `ConcurrentDictionary` allocates a heap node per entry, modulo-based bucketing wastes cycles, and a full resize re-acquires every lock. Typhon needs a key/value container that stores entries flat and contiguous, hashes a 4/8/16-byte key in a couple of cycles, and never asks the GC to track thousands of small objects per collection.

## ⚙️ How it works (in brief)
Both variants use open addressing with linear probing over a single flat entry array, so a lookup scans contiguous memory instead of chasing pointers through scattered nodes. Deletion uses backward-shift instead of tombstones, so probe chains never degrade from accumulated deletes. The hash function is JIT-specialized on `sizeof(TKey)` — a one-multiply Fibonacci hash for 4/8/16-byte keys, a generic byte-scan fallback otherwise — so there's no runtime dispatch. `TKey` must be `unmanaged, IEquatable<TKey>`; managed key types (`string`, `Type`) aren't supported, fall back to `Dictionary`/`ConcurrentDictionary` for those.

## Sub-features

| Sub-feature | Concurrent | Use it when... |
|---|---|---|
| [Non-Concurrent HashMap\<TKey[, TValue]\>](./non-concurrent-hash-map.md) | No | Single-threaded hot path — a query result set, a per-system tracked-entity set |
| [ConcurrentHashMap\<TKey[, TValue]\>](./concurrent-hash-map.md) | Yes (striped OLC) | Same shape, but accessed from multiple threads concurrently |

## ⚠️ Guarantees & limits
- `internal` engine types (`Typhon.Engine.Internals`) — engine plumbing, not callable from application code; none of the four types are `[PublicAPI]`.
- `TKey` must be `unmanaged` and `IEquatable<TKey>`; `TValue` may be any type (managed values land in a parallel array instead of inline storage).
- Lookup is typically 1.5-3x faster than `Dictionary<TKey,TValue>`; insert is roughly on par at small sizes (resize cost dominates).
- All four implement `IDisposable` — entries live in GC-pinned arrays (Pinned Object Heap), not unmanaged memory, but still require explicit disposal to drop the reference promptly.
- Not a drop-in replacement for every use of `Dictionary`: no ordered enumeration/range queries (use a B+Tree for that), and short-lived collections under ~100 entries don't justify the `Dispose()` discipline.

## 🧪 Tests
- [HashMapTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Collections/InMemoryHashMapTests.cs) — non-concurrent set/map: open addressing, backward-shift delete, resize at load factor.
- [ConcurrentHashMapTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Collections/ConcurrentInMemoryHashMapTests.cs) — striped concurrent set/map: lock-free reads, per-stripe CAS writes.

## 🔗 Related
- Sub-features: [Non-Concurrent HashMap](./non-concurrent-hash-map.md), [ConcurrentHashMap](./concurrent-hash-map.md)
- Sibling: [Page-Backed Linear Hash Map](../paged-linear-hash-map.md) — same open-addressing hashing family, persisted to page storage instead of pinned memory.

<!-- Deep dive: claude/design/Foundation/Collections/in-memory-hash-map.md, claude/overview/11-utilities.md §H.1 -->
