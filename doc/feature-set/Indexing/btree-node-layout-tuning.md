# B+Tree Node Layout and Capacity Tuning
> Cache-line-aware 256-byte node layout, the product of a multi-phase profiling effort — invisible to, and not configured by, application code.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Every B+Tree operation walks from root to leaf, and each node visited is a potential cache miss or DRAM round-trip. Two things determine how expensive that walk is: how many cache lines a node spans, and how many keys fit in a node (which sets the tree's height for a given entry count). Naively, those two goals fight each other — bigger nodes mean fewer levels but more bytes touched per node. Typhon's B+Tree node layout was tuned through a multi-phase profiling effort (issues #98, #163) to resolve this trade-off, so application developers get shallow, cache-efficient trees without sizing anything themselves.

## ⚙️ How it works (in brief)

Every node — `Index16Chunk`, `Index32Chunk`, `Index64Chunk`, `IndexString64Chunk` — is fixed at **256 bytes, four 64-byte cache lines**, laid out as a 16-byte-plus-key header (`Control`, `OlcVersion` latch, `PrevChunk`/`NextChunk` sibling links, `LeftValue`, `HighKey`) followed by parallel `Keys[N]`/`Values[N]` arrays. The size was reached in stages: profiling first showed tree *height* dominated cost more than per-node cache-line count, so nodes doubled from 64→128 bytes (issue #98), then evolved again to the current 256 bytes. Capacity `N` is computed per key-width tier so each tier fills the 256 bytes as densely as possible — narrower keys pack more entries per node, shallower trees, fewer levels walked per operation. The same effort also reworked the surrounding algorithm: lazy sibling loading (`NodeRelatives` only loads left/right siblings on the ~5% of operations that actually spill or split), iterative (not recursive) root-to-leaf descent, and SIMD-accelerated key search within a node. None of this is a knob — node size, capacity, and which concrete tree type backs a field are all resolved automatically from the field's declared type at schema-build time.

## 💻 Usage

```csharp
[Component("Game.Player", 1)]
public struct Player
{
    [Index]                       // -> L32BTree, 256-byte node, 29 keys/node
    public int PlayerId;

    [Index(AllowMultiple = true)] // -> L64BTree, 256-byte node, 19 keys/node
    public long GuildId;
}

// Application code never names a node type or a node size — it only ever
// resolves an IndexRef and queries through Transaction.
var idIndex = engine.GetIndexRef<Player, int>(p => p.PlayerId);
using var tx = engine.CreateQuickTransaction();
using var found = tx.EnumerateIndex<Player, int>(idIndex, minKey: 100, maxKey: 200);
```

| Header field | Size | Purpose |
|---|---|---|
| `Control` | 4 B | `StateFlags` (incl. `IsLeaf`) + `Count` |
| `OlcVersion` | 4 B | per-node optimistic-lock-coupling latch |
| `PrevChunk` / `NextChunk` | 4+4 B | doubly-linked sibling pointers (B-link right-link) |
| `LeftValue` | 4 B | leftmost child pointer (internal nodes) |
| `HighKey` | `sizeof(TKey)` | B-link upper bound, `== Next.firstKey` |
| `Keys[N]` / `Values[N]` | remainder | sorted key array + parallel value array |

## ⚠️ Guarantees & limits

- Node size is fixed at compile time (`Debug.Assert(sizeof(Index{16,32,64}Chunk) == 256)`); there is no per-table or per-index size override.
- Capacity scales inversely with key width: `L16BTree`=38, `L32BTree`=29, `L64BTree`=19 keys/node; `String64BTree` stays at 4 because a 64-byte key already consumes most of the budget. See [B+Tree Key-Size Variants](./btree-key-variants.md) for the full type-to-variant mapping.
- The layout benefits Insert, Remove, and Lookup roughly equally (all are root-to-leaf descents); RangeScan benefits only on the initial descent to the starting leaf, not on the subsequent leaf-linked-list walk.
- Gains are structural (fewer tree levels, fewer pointer-chases, fewer DRAM round-trips), not raw memory-bandwidth gains — they hold even on CPUs without an adjacent-line prefetcher, though the within-node binary/SIMD search assumes a stride-prefetcher-friendly access pattern.
- Going materially past 256 bytes is not free: wider nodes mean more bytes touched (and more cache lines beyond what the prefetcher reliably covers) per node visited, so this is a tuned ceiling, not a "bigger is always better" knob — see ADR-022 for the alternatives considered.
- Schema authors interact with this only indirectly, through `[Index]`/`[Index(AllowMultiple = true)]` on a field — there is no API surface to request a non-default node size or capacity.

## 🧪 Tests

- [OlcLatchTests](../../../test/Typhon.Engine.Tests/Data/OlcLatchTests.cs) — `Index16Chunk_Size_Is256Bytes`/`Index32Chunk_Size_Is256Bytes`/`Index64Chunk_Size_Is256Bytes`/`Index32Chunk_Capacity_Is29`: pins the fixed 256-byte node size and per-tier capacity this feature describes
- [BTreeTests](../../../test/Typhon.Engine.Tests/Data/BTreeTests.cs) — exercises trees built at these exact node capacities across all four key-width tiers, indirectly validating the layout under real insert/remove/range-scan traffic

## 🔗 Related

- Sibling feature: [B+Tree Key-Size Variants](./btree-key-variants.md) — how the key type selects the concrete tree implementation

<!-- Deep dive: claude/design/Indexing/btree-insert-optimization.md — the 64→128-byte profiling study, lazy NodeRelatives, iterative descent, SIMD search -->
<!-- Deep dive: claude/design/Indexing/btree-phase5-analysis.md — deep cost-breakdown analysis and further quick-win optimizations -->
<!-- Deep dive: claude/adr/022-64byte-cache-aligned-nodes.md — sizing rationale and the 64B→128B→256B evolution -->
<!-- Deep dive: claude/overview/04-data.md#node-structure-256-bytes-four-cache-lines — current node structure diagram -->
