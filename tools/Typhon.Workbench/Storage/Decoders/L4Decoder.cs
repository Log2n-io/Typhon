using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Storage;

namespace Typhon.Workbench.Storage.Decoders;

/// <summary>
/// Server-side L4 content decoders for the Database File Map (Module 15, A2, design §10). Each decoder turns
/// raw bytes into neutral <see cref="StorageContentCellDto"/>s the client lays out and colors — the byte layout
/// is C# engine knowledge, so decoding here keeps it DRY and drift-free. Decoders are additive: A2 ships the
/// chunk-based component decoder (field-level), the page-directory decoder, and a generic byte-class fallback;
/// anything else renders as the unknown tile.
/// </summary>
internal static class L4Decoder
{
    /// <summary>Decoder name reported when no typed decoder applies — the client draws the unknown tile.</summary>
    public const string UnknownDecoder = "unknown";

    /// <summary>
    /// Decodes a component-instance chunk into one field cell per declared field, colored by field id. A chunk
    /// in a component segment holds exactly one instance; SingleVersion / Transient components carry an 8-byte
    /// entity-PK header (<see cref="DBComponentDefinition.EntityPKOverheadSize"/>) decoded as a leading cell.
    /// </summary>
    public static StorageContentCellDto[] DecodeComponent(DBComponentDefinition def, ReadOnlySpan<byte> chunkBytes)
    {
        var cells = new List<StorageContentCellDto>();

        var overhead = def.EntityPKOverheadSize;
        if (overhead >= 8 && chunkBytes.Length >= 8)
        {
            var pk = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(chunkBytes);
            cells.Add(new StorageContentCellDto("entity", pk.ToString(CultureInfo.InvariantCulture), "entityPk", 0, 8, -1));
        }

        foreach (var field in def.FieldsByName.Values)
        {
            var offset = overhead + field.OffsetInComponentStorage;
            var size = field.SizeInComponentStorage;
            var value = offset >= 0 && offset + size <= chunkBytes.Length
                ? StorageFieldFormatter.Format(field.Type, chunkBytes.Slice(offset, size))
                : "—";
            cells.Add(new StorageContentCellDto(field.Name, value, "field", offset, size, field.FieldId));
        }

        // Stable layout order — the client renders cells left-to-right by byte offset.
        cells.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        return cells.ToArray();
    }

    /// <summary>
    /// Decodes one cluster chunk into an N-slot entity sub-grid (Module 15, A6, design §10.1). A cluster packs N
    /// entities SoA-style: an <c>OccupancyBits</c> u64 at offset 0 (bit <c>s</c> set ⇒ slot <c>s</c> holds a live
    /// entity), one <c>EnabledBits</c> u64 per component slot at <c>8 + c*8</c> (bit <c>s</c> ⇒ component <c>c</c>
    /// is enabled for the entity in slot <c>s</c>), then the packed entity-id array at
    /// <paramref name="entityIdsOffset"/> (8 bytes per slot). Each emitted cell is one slot: <c>ColorKey</c> 1 =
    /// occupied / 0 = free, and <c>EnabledMask</c> carries the per-slot component bitmask, so the client renders
    /// the occupancy sub-grid and the component overlay from a single decode. Layout constants come from
    /// <see cref="DatabaseEngine.TryGetClusterLayout"/>.
    /// </summary>
    public static StorageContentCellDto[] DecodeCluster(ReadOnlySpan<byte> chunkBytes, int clusterSize, int componentCount, int entityIdsOffset)
    {
        if (clusterSize <= 0 || chunkBytes.Length < 8)
        {
            return [];
        }

        var occupancy = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(chunkBytes);

        // Read each component's EnabledBits word (at 8 + c*8); transposed to a per-slot mask in the slot loop below.
        Span<ulong> enabled = componentCount <= 16 ? stackalloc ulong[16] : new ulong[componentCount];
        for (var c = 0; c < componentCount; c++)
        {
            var off = 8 + c * 8;
            enabled[c] = off + 8 <= chunkBytes.Length
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(chunkBytes.Slice(off))
                : 0UL;
        }

        var cells = new StorageContentCellDto[clusterSize];
        for (var s = 0; s < clusterSize; s++)
        {
            var occupied = (occupancy & (1UL << s)) != 0;

            long enabledMask = 0;
            for (var c = 0; c < componentCount; c++)
            {
                if ((enabled[c] & (1UL << s)) != 0)
                {
                    enabledMask |= 1L << c;
                }
            }

            var idOffset = entityIdsOffset + s * 8;
            var value = "—";
            if (occupied && idOffset >= 0 && idOffset + 8 <= chunkBytes.Length)
            {
                var id = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(chunkBytes.Slice(idOffset));
                value = id.ToString(CultureInfo.InvariantCulture);
            }

            cells[s] = new StorageContentCellDto($"slot {s}", value, "entitySlot", idOffset, 8, occupied ? 1 : 0, enabledMask);
        }
        return cells;
    }

    /// <summary>
    /// Decodes the full content of a single cluster entity — the L5 level beneath the L4 slot sub-grid (file-map §10 Q4 override). Where
    /// <see cref="DecodeCluster"/> emits one occupancy cell per slot, this emits the entity at <paramref name="slotIndex"/> the same way
    /// <see cref="DecodeComponent"/> shows a legacy component instance: a leading <c>entityPk</c> cell, then per component slot a <c>componentHeader</c> cell
    /// (name + enabled / disabled for this entity) followed by one <c>field</c> cell per declared field, decoded from the component's inline SoA array at
    /// <c>Offset + slotIndex * Size</c>. SingleVersion / Versioned components are decoded inline (the Versioned inline copy is the current committed value);
    /// <c>Transient</c> slots carry no data in the persisted chunk (their SoA lives in the in-memory transient store) so they emit a single header note instead.
    /// Returns an empty array when the slot is free (no live entity). Layout comes from <see cref="DatabaseEngine.TryGetClusterEntityLayout"/>.
    /// </summary>
    public static StorageContentCellDto[] DecodeClusterEntity(ReadOnlySpan<byte> chunkBytes, int slotIndex, int clusterSize, int entityIdsOffset,
        (string Name, int Offset, int Size, bool Transient, DBComponentDefinition Definition)[] components)
    {
        if (clusterSize <= 0 || slotIndex < 0 || slotIndex >= clusterSize || chunkBytes.Length < 8 || components == null)
        {
            return [];
        }

        // Free slot → no entity to decode (mirrors a free legacy chunk decoding to nothing).
        var occupancy = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(chunkBytes);
        if ((occupancy & (1UL << slotIndex)) == 0)
        {
            return [];
        }

        var cells = new List<StorageContentCellDto>();

        var idOffset = entityIdsOffset + slotIndex * 8;
        if (idOffset >= 0 && idOffset + 8 <= chunkBytes.Length)
        {
            var id = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(chunkBytes.Slice(idOffset));
            cells.Add(new StorageContentCellDto("entity", id.ToString(CultureInfo.InvariantCulture), "entityPk", idOffset, 8, -1));
        }

        for (var c = 0; c < components.Length; c++)
        {
            var comp = components[c];

            // Per-slot enabled bit (EnabledBits[c] @ 8 + c*8). A disabled component still occupies its SoA slot; the flag rides the header cell.
            var enabledWordOffset = 8 + c * 8;
            var enabled = enabledWordOffset + 8 <= chunkBytes.Length
                && (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(chunkBytes.Slice(enabledWordOffset)) & (1UL << slotIndex)) != 0;

            if (comp.Transient)
            {
                // Transient component data is in the in-memory transient store, never this persisted chunk — surface that honestly rather than decode garbage.
                cells.Add(new StorageContentCellDto(comp.Name, "(transient — not persisted)", "componentHeader", 0, 0, c));
                continue;
            }

            cells.Add(new StorageContentCellDto(comp.Name, enabled ? "enabled" : "disabled", "componentHeader", comp.Offset, comp.Size, c));

            if (comp.Definition == null)
            {
                continue;
            }

            var compBase = comp.Offset + slotIndex * comp.Size;
            foreach (var field in comp.Definition.FieldsByName.Values)
            {
                // Cluster SoA stores the pure component struct (no per-element entity-PK overhead — the PK lives in the EntityKeys array), so the field offset is
                // relative to the component base directly (unlike DecodeComponent's legacy chunk, which prepends an EntityPKOverheadSize header).
                var fieldOffset = compBase + field.OffsetInComponentStorage;
                var fieldSize = field.SizeInComponentStorage;
                var value = fieldOffset >= 0 && fieldOffset + fieldSize <= chunkBytes.Length
                    ? StorageFieldFormatter.Format(field.Type, chunkBytes.Slice(fieldOffset, fieldSize))
                    : "—";
                cells.Add(new StorageContentCellDto($"{comp.Name}.{field.Name}", value, "field", fieldOffset, fieldSize, field.FieldId));
            }
        }

        return cells.ToArray();
    }

    /// <summary>
    /// Decodes one VSBS / component-collection chunk (Module 15, A6, design §10.1). A buffer spans a chain of fixed-stride
    /// chunks linked by <c>NextChunkId</c> (header @chunk+0); <c>ElementCount</c> (@chunk+4) is the elements stored in this
    /// chunk. Reports element count, the per-chunk capacity (<c>(stride − 8) / elementSize</c>) and the chain link. Single
    /// chunk only — root vs continuation can't be told from one chunk's bytes (the root's larger header overlaps element
    /// data), so no head/length is claimed.
    /// </summary>
    public static StorageContentCellDto[] DecodeVsbs(ReadOnlySpan<byte> chunkBytes, int elementSize, int stride)
    {
        if (chunkBytes.Length < 8 || elementSize <= 0)
        {
            return [];
        }

        var nextChunkId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(chunkBytes);
        var elementCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(chunkBytes.Slice(4));
        var capacity = (stride - 8) / elementSize;

        return
        [
            new StorageContentCellDto("Elements", elementCount.ToString(CultureInfo.InvariantCulture), "vsbsMeta", 4, 4, -1),
            new StorageContentCellDto("Capacity / chunk", capacity.ToString(CultureInfo.InvariantCulture), "vsbsMeta", 0, 0, -1),
            new StorageContentCellDto("Element size", $"{elementSize} B", "vsbsMeta", 0, 0, -1),
            new StorageContentCellDto("Next chunk", nextChunkId == 0 ? "— (chain end)" : $"#{nextChunkId}", "chainLink", 0, 4, nextChunkId),
        ];
    }

    /// <summary>
    /// Decodes one string-table chunk (Module 15, A6, design §10.1) into a UTF-8 preview. A string spans a chain of chunks
    /// linked by <c>NextChunkId</c> (@chunk+4); <c>SizeLeft</c> (@chunk+0) is the bytes remaining from this chunk on, so this
    /// chunk holds <c>min(SizeLeft, blockSize)</c> payload bytes (<c>blockSize = stride − 8</c>). Single chunk: the preview is
    /// this chunk's slice (so a mid-chain chunk shows a fragment of the whole string), and the chain link points onward.
    /// </summary>
    public static StorageContentCellDto[] DecodeString(ReadOnlySpan<byte> chunkBytes, int stride)
    {
        if (chunkBytes.Length < 8)
        {
            return [];
        }

        var sizeLeft = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(chunkBytes);
        var nextChunkId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(chunkBytes.Slice(4));
        var blockSize = stride - 8;
        var payloadLen = Math.Clamp(sizeLeft, 0, Math.Min(blockSize, chunkBytes.Length - 8));
        var preview = payloadLen > 0 ? Utf8Preview(chunkBytes.Slice(8, payloadLen), 96) : "";

        return
        [
            new StorageContentCellDto("Bytes from here", $"{sizeLeft} B", "stringMeta", 0, 4, -1),
            new StorageContentCellDto("This chunk", $"{Math.Min(Math.Max(sizeLeft, 0), blockSize)} / {blockSize} B", "stringMeta", 0, 0, -1),
            new StorageContentCellDto("Next chunk", nextChunkId == 0 ? "— (chain end)" : $"#{nextChunkId}", "chainLink", 4, 4, nextChunkId),
            new StorageContentCellDto("Preview", preview, "stringPreview", 8, payloadLen, -1),
        ];
    }

    /// <summary>UTF-8 decode of a payload slice for display: control chars shown as · and the result capped at <paramref name="maxChars"/>.</summary>
    private static string Utf8Preview(ReadOnlySpan<byte> bytes, int maxChars)
    {
        var decoded = Encoding.UTF8.GetString(bytes);
        var sb = new StringBuilder(Math.Min(decoded.Length, maxChars + 1));
        foreach (var ch in decoded)
        {
            if (sb.Length >= maxChars)
            {
                sb.Append('…');
                break;
            }
            sb.Append(char.IsControl(ch) ? '·' : ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decodes one linear-hash (entity-map) chunk (Module 15, A6, design §10.1). The chunk's role is supplied by the caller — the meta chunk is always chunk 0;
    /// directory / overflow-dir-index chunks come from the engine's non-data set (<see cref="DatabaseEngine.TryGetHashMapLayout"/>) — because a headerless
    /// directory chunk can't be told from a bucket by its bytes alone:
    /// <list type="bullet">
    /// <item><b>meta</b> — unpacks <c>PackedMeta</c> (@+8): the linear-hash <c>Level</c> / split pointer / bucket count, plus total entries (@+16) and the
    /// directory-chunk count (@+24);</item>
    /// <item><b>directory</b> — a flat table of 64 bucket-chunk pointers (no header);</item>
    /// <item><b>bucket / overflow</b> — the bucket header (@+0): <c>EntryCount</c> (@+4) over capacity, the overflow chain link (@+8), and the
    /// primary-vs-overflow role (a primary bucket carries a non-zero <c>OlcVersion</c>; an overflow chunk carries <c>OlcVersion == 0</c>).</item>
    /// </list>
    /// </summary>
    public static StorageContentCellDto[] DecodeHashMap(ReadOnlySpan<byte> chunkBytes, bool isMeta, bool isDirectory, int bucketCapacity)
    {
        if (chunkBytes.Length < 12)
        {
            return [];
        }

        if (isMeta)
        {
            // PackedMeta layout (PagedHashMapBase.UnpackMeta): Level(bits 56-63) | Next(bits 32-55) | BucketCount(bits 0-31).
            var packed = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(chunkBytes.Slice(8));
            var level = (int)((packed >> 56) & 0xFF);
            var next = (int)((packed >> 32) & 0x00FFFFFF);
            var bucketCount = (int)(packed & 0xFFFFFFFF);
            var entryCount = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(chunkBytes.Slice(16));
            var dirChunks = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(chunkBytes.Slice(24));
            return
            [
                new StorageContentCellDto("Role", "Meta", "hashMeta", 0, 0, -1),
                new StorageContentCellDto("Buckets", bucketCount.ToString(CultureInfo.InvariantCulture), "hashMeta", 8, 8, -1),
                new StorageContentCellDto("Total entries", entryCount.ToString(CultureInfo.InvariantCulture), "hashMeta", 16, 8, -1),
                new StorageContentCellDto("Level", level.ToString(CultureInfo.InvariantCulture), "hashMeta", 8, 8, -1),
                new StorageContentCellDto("Split pointer", next.ToString(CultureInfo.InvariantCulture), "hashMeta", 8, 8, -1),
                new StorageContentCellDto("Directory chunks", dirChunks.ToString(CultureInfo.InvariantCulture), "hashMeta", 24, 2, -1),
            ];
        }

        if (isDirectory)
        {
            return
            [
                new StorageContentCellDto("Role", "Directory", "hashMeta", 0, 0, -1),
                new StorageContentCellDto("Bucket pointers", "64 / chunk", "hashMeta", 0, 0, -1),
            ];
        }

        // Bucket or overflow chunk — PagedHashMapBucketHeader { OlcVersion @0, EntryCount @4, _ @5-7, OverflowChunkId @8 }.
        var olcVersion = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(chunkBytes);
        var bucketEntries = chunkBytes[4];
        var overflowChunkId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(chunkBytes.Slice(8));
        var role = olcVersion == 0 ? "Overflow" : "Bucket";
        var entriesLabel = bucketCapacity > 0 ? $"{bucketEntries} / {bucketCapacity}" : bucketEntries.ToString(CultureInfo.InvariantCulture);
        return
        [
            new StorageContentCellDto("Role", role, "hashBucket", 0, 4, -1),
            new StorageContentCellDto("Entries", entriesLabel, "hashBucket", 4, 1, -1),
            new StorageContentCellDto("Overflow", overflowChunkId == -1 ? "— (none)" : $"#{overflowChunkId}", "chainLink", 8, 4, overflowChunkId),
        ];
    }

    /// <summary>
    /// Decodes one B-tree index chunk (Module 15, A6, design §10.1). The chunk's role comes from its id: chunks <c>[0, directoryChunkCount)</c> are the segment's
    /// shared directory (chunk 0 + overflow), every other chunk is a node. A node's header is variant-independent — leaf vs internal and the entry count read at
    /// fixed offsets regardless of key width — so this needs no per-node capacity (deliberately omitted; see §13 A6):
    /// <list type="bullet">
    /// <item><b>directory</b> — lists every B-tree registered in the segment (primary key + one per secondary-indexed field): stable id, root chunk, entry count.</item>
    /// <item><b>node</b> — the control word (@+0): leaf (bit 1) vs internal, the entry <c>Count</c> (byte 3); the prev / next sibling links (@+8 / @+12); and the
    /// leftmost-child link (@+16) for an internal node. The keys / HighKey are not decoded — their width is the (unknown) key width.</item>
    /// </list>
    /// </summary>
    public static StorageContentCellDto[] DecodeIndex(ReadOnlySpan<byte> chunkBytes, int chunkId, int directoryChunkCount,
        (short StableId, int RootChunkId, int EntryCount)[] trees)
    {
        if (chunkId < directoryChunkCount)
        {
            var treeCount = trees?.Length ?? 0;
            var dirCells = new List<StorageContentCellDto>(1 + treeCount)
            {
                new("B-trees", treeCount.ToString(CultureInfo.InvariantCulture), "indexMeta", 0, 2, -1),
            };
            if (trees != null)
            {
                foreach (var t in trees)
                {
                    var label = t.StableId == -1 ? "Primary key" : t.StableId == 0 ? "Standalone" : $"Field #{t.StableId}";
                    dirCells.Add(new(label, $"root #{t.RootChunkId} · {t.EntryCount} entries", "indexTree", 0, 12, t.RootChunkId));
                }
            }
            return dirCells.ToArray();
        }

        if (chunkBytes.Length < 20)
        {
            return [];
        }

        var control = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(chunkBytes);
        var isLeaf = (control & 0x02) != 0; // NodeStates.IsLeaf
        var count = (control >> 24) & 0xFF; // Count = byte 3 of the control word
        var prev = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(chunkBytes.Slice(8));
        var next = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(chunkBytes.Slice(12));
        var leftChild = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(chunkBytes.Slice(16));

        var cells = new List<StorageContentCellDto>(5)
        {
            new("Role", isLeaf ? "Leaf" : "Internal", "indexNode", 0, 4, -1),
            new("Entries", count.ToString(CultureInfo.InvariantCulture), "indexNode", 3, 1, -1),
            new("Prev sibling", prev == 0 ? "—" : $"#{prev}", "chainLink", 8, 4, prev),
            new("Next sibling", next == 0 ? "—" : $"#{next}", "chainLink", 12, 4, next),
        };
        if (!isLeaf)
        {
            cells.Add(new("Leftmost child", leftChild == 0 ? "—" : $"#{leftChild}", "chainLink", 16, 4, leftChild));
        }
        return cells.ToArray();
    }

    /// <summary>
    /// Generic byte-class fallback for a chunk of a non-component chunk-based segment (index / VSBS / string
    /// table). The chunk is split into fixed runs, each cell colored by its dominant byte class — structured vs
    /// zeroed vs string-heavy stays legible without a typed decoder (design §10 decode-free fallback).
    /// </summary>
    public static StorageContentCellDto[] DecodeGeneric(ReadOnlySpan<byte> chunkBytes)
    {
        if (chunkBytes.IsEmpty)
        {
            return [];
        }

        const int maxRuns = 64;
        var runLength = Math.Max(8, (chunkBytes.Length + maxRuns - 1) / maxRuns);
        var cells = new List<StorageContentCellDto>();
        for (var offset = 0; offset < chunkBytes.Length; offset += runLength)
        {
            var len = Math.Min(runLength, chunkBytes.Length - offset);
            var run = chunkBytes.Slice(offset, len);
            var cls = DominantByteClass(run);
            cells.Add(new StorageContentCellDto($"@{offset}", ByteClassName(cls), "byteRun", offset, len, cls));
        }
        return cells.ToArray();
    }

    /// <summary>
    /// Decodes a logical segment's page directory — the engine already resolved it into the ordered page list,
    /// so each entry maps the logical page index to its physical file page.
    /// </summary>
    public static StorageContentCellDto[] DecodeDirectory(ReadOnlySpan<int> pages)
    {
        var cells = new StorageContentCellDto[pages.Length];
        for (var i = 0; i < pages.Length; i++)
        {
            cells[i] = new StorageContentCellDto(
                $"logical {i}", $"page {pages[i]}", "dirEntry", i * 4, 4, pages[i]);
        }
        return cells;
    }

    /// <summary>
    /// The dominant byte class (0 zero · 1 0xFF · 2 ASCII · 3 binary) of a byte span — the decode-free
    /// characterization the generic decoder uses and the detail tier's per-page byte-class encoding reuses.
    /// </summary>
    internal static int DominantByteClass(ReadOnlySpan<byte> run)
    {
        Span<int> counts = stackalloc int[4];
        foreach (var b in run)
        {
            counts[ClassOf(b)]++;
        }
        var best = 0;
        for (var c = 1; c < 4; c++)
        {
            if (counts[c] > counts[best])
            {
                best = c;
            }
        }
        return best;
    }

    private static int ClassOf(byte b) => b switch
    {
        0x00 => 0,
        0xFF => 1,
        >= 0x20 and <= 0x7E => 2,
        _ => 3,
    };

    private static string ByteClassName(int cls) => cls switch
    {
        0 => "zero",
        1 => "0xFF",
        2 => "ascii",
        _ => "binary",
    };
}
