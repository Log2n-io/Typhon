using System;
using System.Collections.Generic;
using System.Globalization;
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

    private static int DominantByteClass(ReadOnlySpan<byte> run)
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
