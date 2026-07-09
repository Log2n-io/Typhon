---
uid: feature-storage-string-table
title: 'String Table Storage'
description: 'UTF-8 string storage spread across linked fixed-size chunks, for strings too long to hold inline.'
---

# String Table Storage
> UTF-8 string storage spread across linked fixed-size chunks, for strings too long to hold inline.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Storage](./README.md)

## 🎯 What it solves

Inline fixed-width string fields (`String64`, 64 bytes) cover short identifiers and key-like strings, but plenty of string data — names, descriptions, paths, log messages — has no fixed upper bound and would either truncate or waste space if forced into a fixed-width slot. `StringTableSegment<TStore>` is the building block for storing strings of arbitrary length in the same chunk-based storage every other structure uses: a string is split across as many fixed-size chunks as it needs and reassembled on load, so storage cost scales with actual content size rather than a worst-case bound.

## ⚙️ How it works (in brief)

A string is UTF-8 encoded, then sliced into chunk-sized blocks; each chunk holds a small header (bytes remaining, next chunk id) plus its slice of the payload, with chunks linked forward into a chain. `StoreString` allocates the whole chain up front, writes the slices, and returns the id of the root chunk — that id is the string's handle. `LoadString` walks the chain from the root chunk, copying each slice into a buffer and decoding back to UTF-8 once the chain is exhausted. `DeleteString` walks the same chain, freeing each chunk back to the owning `ChunkBasedSegment`. The type is generic over `TStore : IPageStore`, so it works unmodified over both the persistent (MMF-backed) and transient (heap-backed) storage backends — see [Pluggable Storage Backend](pluggable-storage-backend/README.md).

## 💻 Usage

`StringTableSegment<TStore>` is `internal` engine plumbing — it sits directly on a `ChunkBasedSegment<TStore>` and is driven within an epoch scope, the same pattern used by every chunk-based structure:

```csharp
var segment = pagedMmf.AllocateChunkBasedSegment(PageBlockType.None, length: 10, stride: 64);

var depth = epochManager.EnterScope();
try
{
    var stringTable = new StringTableSegment<PersistentStore>(segment, epochManager);

    int id = stringTable.StoreString("a string longer than one 64-byte chunk...");

    string roundTripped = stringTable.LoadString(id);

    stringTable.DeleteString(id);
}
finally
{
    epochManager.ExitScope(depth);
}
```

The returned `int` (root chunk id) is the only handle needed to reload or delete a stored string later — callers persist that id wherever they'd otherwise store the string itself.

## ⚠️ Guarantees & limits

- Storage cost is proportional to actual UTF-8 byte length (rounded up to the chunk stride), not a fixed worst-case width — unlike `String64`.
- No length cap other than the owning segment's available chunk capacity (which auto-grows).
- Each `StoreString`/`LoadString`/`DeleteString` call must run inside an `EpochManager` scope, like any other chunk-accessor-driven operation.
- Not indexable: there is no B+Tree variant over string-table content — use `String64` (see [B+Tree Key-Size Variants](../Indexing/btree-key-variants.md)) when a string needs to be a lookup key.
- `internal` type — not exposed on the public API surface and not yet wired into the component schema's field type system (no variable-length string `FieldType` exists today); currently engine-internal infrastructure, exercised directly rather than through a component field.
- Storing a string allocates its full chunk chain up front (no partial/streaming writes); deleting frees the whole chain in one call.

## 🧪 Tests

- [ManagedPagedMMFTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/ManagedPagedMMFTests.cs) — `StringTableTest`: store/load/delete round-trip of a string spanning multiple chained chunks

## 🔗 Related

- Related feature: [Segment & Chunk-Based Allocation Engine](segment-chunk-allocation.md) — the `ChunkBasedSegment<TStore>` this type is built on
- Related feature: [Pluggable Storage Backend](pluggable-storage-backend/README.md) — the `TStore` generic backend it specializes over
- Compare: [B+Tree Key-Size Variants](../Indexing/btree-key-variants.md) — `String64`, the fixed-width indexable alternative

<!-- Deep dive: claude/overview/03-storage.md §3.8 -->
