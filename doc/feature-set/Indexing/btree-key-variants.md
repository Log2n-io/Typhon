---
uid: feature-indexing-btree-key-variants
title: 'Specialized B+Tree Key-Size Variants'
description: 'One cache-aligned B+Tree node layout per key width, picked automatically from the field''s type — invisible to application code.'
---

# Specialized B+Tree Key-Size Variants
> One cache-aligned B+Tree node layout per key width, picked automatically from the field's type — invisible to application code.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Every indexed field — primary key or secondary index — needs an ordered, concurrent lookup structure, but indexed key types span an 8×-wide range (1-byte `byte` up to 64-byte `String64`). A single one-size-fits-all B+Tree would either waste node space on narrow keys (fewer keys per cache-line-sized node, deeper trees, more cache misses) or be unable to address wide keys at all. Typhon avoids this by giving each key-width tier its own specialized tree implementation, so a `byte`-keyed index and a `long`-keyed index both get a node layout packed for their key size, without the application ever picking one.

## ⚙️ How it works (in brief)

Four concrete tree types — `L16BTree` (keys ≤ 16-bit), `L32BTree` (32-bit), `L64BTree` (64-bit), `String64BTree` (fixed 64-byte string) — share one generic algorithm (`BTree<TKey, TStore>`) but each defines its own fixed-size node struct, sized so more keys fit in a narrow-key node than a wide-key one. Which variant backs a given index is decided automatically when the schema is built, from the indexed field's declared type — application code never names or constructs a tree class directly. Every variant is further specialized over `TStore`: `PersistentStore` (durable, WAL-protected, backs `Versioned`/`SingleVersion` tables) or `TransientStore` (in-memory, backs `Transient` tables) — again chosen by the table's storage discipline, not by the caller.

## 💻 Usage

```csharp
[Component("Game.Player", 1)]
public struct Player
{
    [Index]                       // unique secondary index -> L32BTree<int, ...> (32-bit key)
    public int PlayerId;

    [Index(AllowMultiple = true)] // non-unique secondary index -> String64BTree<...>
    public String64 Guild;

    public float PositionX;       // not indexed
}

// Cold path: resolve once, reuse the IndexRef on the hot path.
var idIndex = engine.GetIndexRef<Player, int>(p => p.PlayerId);

// Hot path: range-scan through Transaction — never through the tree directly.
using var tx = engine.CreateQuickTransaction();
using var found = tx.EnumerateIndex<Player, int>(idIndex, minKey: 100, maxKey: 200);
foreach (var entry in found)
{
    // entry.EntityPK, entry.Key, entry.Component
}
```

| Key type(s) | Variant | Capacity (keys/node) |
|---|---|---|
| `byte`, `sbyte`, `short`, `ushort`, `char` | `L16BTree` | 38 |
| `int`, `uint`, `float` | `L32BTree` | 29 |
| `long`, `ulong`, `double` | `L64BTree` | 19 |
| `String64` | `String64BTree` | 4 |

## ⚠️ Guarantees & limits

- Variant selection is fully automatic and type-driven — there is no API to request a specific tree implementation; it follows from the field's declared type and `[Index(AllowMultiple = ...)]`.
- All four variants expose the identical operation surface and concurrency model (OLC optimistic reads, B-link splits) through `IndexRef`/`Transaction` — no behavioral difference between tiers at the application level.
- Node capacity scales inversely with key width (38 → 29 → 19 → 4), so narrower-keyed indexes pack more entries per node and produce shallower trees for the same entry count.
- `String64` keys are fixed at 64 bytes; there is no variable-length string index.
- Persistent vs. transient backing follows the owning table's storage discipline (`Versioned`/`SingleVersion` = durable, `Transient` = in-memory) — not independently selectable per index.
- Schema evolution that changes an indexed field's type invalidates previously-resolved `IndexRef`s (`CapturedLayoutVersion` check); re-resolve via `GetIndexRef`/`GetPKIndexRef`.

## 🧪 Tests

- [BtreeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/BTreeTests.cs) — `ForwardInsertionTest`/`ForwardFloatInsertionTest`/`ReverseInsertionTest`/`ReverseString64InsertionTest` exercise all four key-width tiers (`int`/`L32`, `float`/`L32`, `byte`/`L16`, `String64`) through the shared `BTree<TKey, TStore>` algorithm
- [OlcLatchTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/OlcLatchTests.cs) — chunk-size verification (`Index16Chunk_Size_Is256Bytes`, `Index32Chunk_Size_Is256Bytes`, `Index64Chunk_Size_Is256Bytes`, `Index32Chunk_Capacity_Is29`) confirms the per-variant node layout the table above documents

## 🔗 Related

- Sibling: [B+Tree Node Layout and Capacity Tuning](./btree-node-layout-tuning.md) — per-key-width node capacities this feature's variants use

<!-- Deep dive: claude/design/Indexing/public-api.md — public surface, internal type hierarchy, per-variant capacities -->
<!-- Deep dive: claude/adr/021-specialized-btree-variants.md — why per-key-size specialization over one generic tree -->
<!-- Deep dive: claude/adr/022-64byte-cache-aligned-nodes.md — node-size sizing rationale (64B → 128B → 256B evolution) -->
