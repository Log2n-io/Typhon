using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Engine.Internals;

/// <summary>Outcome of reading one slot of the page-0 A/B meta pair.</summary>
internal sealed class MetaSlotView
{
    /// <summary>Physical page index of the slot (0 or 1).</summary>
    public int SlotIndex { get; init; }

    /// <summary>Whether the slot exists within the file at all.</summary>
    public bool Present { get; init; }

    /// <summary>Whether the slot's whole-page checksum matched.</summary>
    public bool ChecksumValid { get; init; }

    /// <summary>Stored checksum value.</summary>
    public uint StoredChecksum { get; init; }

    /// <summary>Recomputed checksum value.</summary>
    public uint ComputedChecksum { get; init; }

    /// <summary>The slot's pair generation. <c>0</c> means it was never written through the alternation path.</summary>
    public ulong Generation { get; init; }

    /// <summary>A slot is selectable when it is present, checksum-valid and carries a non-zero generation.</summary>
    public bool IsValid => Present && ChecksumValid && Generation > 0;
}

/// <summary>
/// Reads the database's identity and bootstrap dictionary from page 0 without booting the engine.
/// </summary>
/// <remarks>
/// <para>
/// The bootstrap dictionary is the single point of failure for offline reading, and it lives on the page-0 A/B meta pair.
/// This reader implements the same pair-selection rule the engine uses — newest valid generation wins — because reading a
/// stale bootstrap would produce a flood of phantom findings. It is the first thing to get right and the first thing to
/// test.
/// </para>
/// <para>
/// Unlike <see cref="BootstrapDictionary.ReadFrom"/>, the stream parse here is <b>tolerant</b>: a truncated or malformed
/// stream yields the entries recovered so far plus a diagnostic, never an exception. A checker must survive the input it
/// exists to diagnose.
/// </para>
/// </remarks>
internal sealed class BootstrapView
{
    /// <summary>Expected page-0 signature.</summary>
    public const string ExpectedSignature = ManagedPagedMMF.HeaderSignature;

    /// <summary>Bootstrap key holding the packed checkpoint-LSN + clean-shutdown pair.</summary>
    public const string DurabilityWatermarksKey = "DurabilityWatermarks";

    /// <summary>Bootstrap key holding the occupancy segment's root page index.</summary>
    public const string OccupancyMapSpiKey = ManagedPagedMMF.BK_OccupancyMapSPI;

    /// <summary>State of meta slot A (physical page 0).</summary>
    public MetaSlotView SlotA { get; init; }

    /// <summary>State of meta slot B (physical page 1).</summary>
    public MetaSlotView SlotB { get; init; }

    /// <summary>The selected slot, or <c>-1</c> when neither was valid.</summary>
    public int SelectedSlot { get; init; } = -1;

    /// <summary>Generation of the selected slot.</summary>
    public ulong SelectedGeneration { get; init; }

    /// <summary>Whether the 32-byte identity signature matched.</summary>
    public bool SignatureValid { get; init; }

    /// <summary>The raw signature string as decoded, for reporting a mismatch.</summary>
    public string Signature { get; init; }

    /// <summary>The on-disk format revision recorded on page 0.</summary>
    public int FormatRevision { get; init; }

    /// <summary>The database name recorded on page 0.</summary>
    public string DatabaseName { get; init; }

    /// <summary>Chunk size used when growing the data file.</summary>
    public ulong FilesChunkSize { get; init; }

    /// <summary>Parsed bootstrap entries, in stream order.</summary>
    public IReadOnlyList<KeyValuePair<string, BootstrapDictionary.Value>> Entries { get; init; } = [];

    /// <summary>Diagnostics produced while parsing the stream. Empty on a well-formed stream.</summary>
    public IReadOnlyList<string> ParseDiagnostics { get; init; } = [];

    /// <summary>Whether the stream terminated on its <c>0xFF</c> sentinel.</summary>
    public bool SentinelReached { get; init; }

    /// <summary>Declared stream length in bytes, as read from the 2-byte header.</summary>
    public int DeclaredStreamLength { get; init; }

    /// <summary>Whether a usable bootstrap was recovered.</summary>
    public bool IsUsable => SelectedSlot >= 0 && SignatureValid;

    /// <summary>Looks up an entry by key.</summary>
    /// <param name="key">Bootstrap key.</param>
    /// <param name="value">Receives the value when present.</param>
    public bool TryGet(string key, out BootstrapDictionary.Value value)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (string.Equals(Entries[i].Key, key, StringComparison.Ordinal))
            {
                value = Entries[i].Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Reads the packed checkpoint-LSN + clean-shutdown pair. A fresh database reads as <c>(0, false)</c>.</summary>
    public (long CheckpointLsn, bool CleanShutdown) ReadWatermarks()
    {
        if (!TryGet(DurabilityWatermarksKey, out var v) || v.IntCount < 3)
        {
            return (0, false);
        }

        var lsn = (uint)v.GetInt(0) | ((long)v.GetInt(1) << 32);
        return (lsn, v.GetInt(2) != 0);
    }
}

/// <summary>
/// Parses page 0 (the A/B meta pair) into a <see cref="BootstrapView"/> from raw bytes.
/// </summary>
internal static class BootstrapReader
{
    // RootFileHeader sits at PageBaseHeaderSize (64). Its fields, at their sequential-layout offsets within the struct:
    //   [0,32)   HeaderSignature : fixed byte[32]
    //   [32,36)  DatabaseFormatRevision : int
    //   [40,48)  DatabaseFilesChunkSize : ulong   (8-aligned, so 4 bytes of padding precede it)
    //   [48,112) DatabaseName : fixed byte[64]
    private const int RootHeaderOffset = PagedMMF.PageBaseHeaderSize;
    private const int SignatureLength = 32;
    private const int FormatRevisionFieldOffset = 32;
    private const int ChunkSizeFieldOffset = 40;
    private const int NameFieldOffset = 48;
    private const int NameLength = 64;

    /// <summary>Reads and validates one meta slot without selecting it.</summary>
    /// <param name="source">The page source.</param>
    /// <param name="slotIndex">Physical page index of the slot (0 or 1).</param>
    /// <param name="buffer">Scratch buffer of at least one page; receives the slot image on success.</param>
    public static MetaSlotView ReadSlot(IPageSource source, int slotIndex, Span<byte> buffer)
    {
        if (!source.TryReadPage(slotIndex, buffer))
        {
            return new MetaSlotView { SlotIndex = slotIndex, Present = false };
        }

        var ok = PageImage.VerifyWholePageChecksum(buffer, out var computed);
        return new MetaSlotView
        {
            SlotIndex = slotIndex,
            Present = true,
            ChecksumValid = ok,
            StoredChecksum = PageImage.StoredChecksum(buffer),
            ComputedChecksum = computed,
            Generation = PageImage.PairGeneration(buffer)
        };
    }

    /// <summary>
    /// Reads both meta slots, selects the newest valid one, and parses its identity header and bootstrap stream.
    /// </summary>
    /// <param name="source">The page source.</param>
    public static BootstrapView Read(IPageSource source)
    {
        Span<byte> slotABuf = new byte[IntegrityConstants.PageSize];
        Span<byte> slotBBuf = new byte[IntegrityConstants.PageSize];

        var a = ReadSlot(source, 0, slotABuf);
        var b = ReadSlot(source, 1, slotBBuf);

        int selected;
        ReadOnlySpan<byte> image;
        if (b.IsValid && (!a.IsValid || b.Generation > a.Generation))
        {
            selected = 1;
            image = slotBBuf;
        }
        else if (a.IsValid)
        {
            selected = 0;
            image = slotABuf;
        }
        else
        {
            return new BootstrapView { SlotA = a, SlotB = b, SelectedSlot = -1 };
        }

        var selectedGen = selected == 0 ? a.Generation : b.Generation;
        var signature = ReadFixedString(image.Slice(RootHeaderOffset, SignatureLength));
        var formatRevision = MemoryMarshal.Read<int>(image[(RootHeaderOffset + FormatRevisionFieldOffset)..]);
        var chunkSize = MemoryMarshal.Read<ulong>(image[(RootHeaderOffset + ChunkSizeFieldOffset)..]);
        var name = ReadFixedString(image.Slice(RootHeaderOffset + NameFieldOffset, NameLength));

        var entries = ParseStream(image[ManagedPagedMMF.BootstrapStreamOffset..], out var diagnostics, out var sentinel, out var declaredLength);

        return new BootstrapView
        {
            SlotA = a,
            SlotB = b,
            SelectedSlot = selected,
            SelectedGeneration = selectedGen,
            SignatureValid = string.Equals(signature, BootstrapView.ExpectedSignature, StringComparison.Ordinal),
            Signature = signature,
            FormatRevision = formatRevision,
            DatabaseName = name,
            FilesChunkSize = chunkSize,
            Entries = entries,
            ParseDiagnostics = diagnostics,
            SentinelReached = sentinel,
            DeclaredStreamLength = declaredLength
        };
    }

    /// <summary>
    /// Tolerant parse of the bootstrap key/value stream. Returns whatever it could recover and describes what it could not,
    /// rather than throwing — the caller is diagnosing damage, so a partial dictionary plus an explanation beats an
    /// exception.
    /// </summary>
    /// <param name="stream">The stream region of the meta page, starting at the 2-byte length header.</param>
    /// <param name="diagnostics">Receives human-readable parse problems.</param>
    /// <param name="sentinelReached">Receives whether the <c>0xFF</c> end sentinel was found.</param>
    /// <param name="declaredLength">Receives the declared entry-bytes length.</param>
    private static List<KeyValuePair<string, BootstrapDictionary.Value>> ParseStream(ReadOnlySpan<byte> stream, out List<string> diagnostics,
        out bool sentinelReached, out int declaredLength)
    {
        diagnostics = [];
        sentinelReached = false;
        declaredLength = 0;

        var entries = new List<KeyValuePair<string, BootstrapDictionary.Value>>();
        if (stream.Length < 3)
        {
            diagnostics.Add("Bootstrap stream region is shorter than its 3-byte minimum.");
            return entries;
        }

        declaredLength = MemoryMarshal.Read<ushort>(stream);
        var end = 2 + declaredLength;
        if (end > stream.Length)
        {
            diagnostics.Add($"Declared bootstrap stream length {declaredLength} runs {end - stream.Length} bytes past the page; truncating.");
            end = stream.Length;
        }

        var pos = 2;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pos < end)
        {
            var tag = (BootstrapDictionary.ValueType)stream[pos++];
            if (tag == BootstrapDictionary.ValueType.End)
            {
                sentinelReached = true;
                break;
            }

            var keyStart = pos;
            while (pos < end && stream[pos] != 0)
            {
                pos++;
            }

            if (pos >= end)
            {
                diagnostics.Add($"Bootstrap entry #{entries.Count} has an unterminated key; stream truncated.");
                break;
            }

            var key = Encoding.UTF8.GetString(stream[keyStart..pos]);
            pos++;   // skip NUL

            if (!TryReadValue(stream, ref pos, end, tag, out var value))
            {
                diagnostics.Add($"Bootstrap key '{key}' has an unreadable value of type {tag}; stream truncated.");
                break;
            }

            if (!seen.Add(key))
            {
                diagnostics.Add($"Bootstrap key '{key}' appears more than once; the last occurrence wins.");
            }

            entries.Add(new KeyValuePair<string, BootstrapDictionary.Value>(key, value));
        }

        if (!sentinelReached && pos < stream.Length && stream[pos] == (byte)BootstrapDictionary.ValueType.End)
        {
            sentinelReached = true;
        }

        if (!sentinelReached)
        {
            diagnostics.Add("Bootstrap stream did not terminate on its 0xFF sentinel.");
        }

        return entries;
    }

    private static bool TryReadValue(ReadOnlySpan<byte> stream, ref int pos, int end, BootstrapDictionary.ValueType type,
        out BootstrapDictionary.Value value)
    {
        value = default;
        switch (type)
        {
            case BootstrapDictionary.ValueType.Bool:
                if (pos >= end)
                {
                    return false;
                }

                value = BootstrapDictionary.Value.FromBool(stream[pos++] != 0);
                return true;

            case >= BootstrapDictionary.ValueType.Int1 and <= BootstrapDictionary.ValueType.Int6:
                var count = (byte)type - (byte)BootstrapDictionary.ValueType.Int1 + 1;
                if (pos + (count * 4) > end)
                {
                    return false;
                }

                Span<int> ints = stackalloc int[6];
                for (var i = 0; i < count; i++)
                {
                    ints[i] = MemoryMarshal.Read<int>(stream[(pos + (i * 4))..]);
                }

                pos += count * 4;
                value = count switch
                {
                    1 => BootstrapDictionary.Value.FromInt(ints[0]),
                    2 => BootstrapDictionary.Value.FromInt2(ints[0], ints[1]),
                    3 => BootstrapDictionary.Value.FromInt3(ints[0], ints[1], ints[2]),
                    4 => BootstrapDictionary.Value.FromInt4(ints[0], ints[1], ints[2], ints[3]),
                    5 => BootstrapDictionary.Value.FromInt5(ints[0], ints[1], ints[2], ints[3], ints[4]),
                    _ => BootstrapDictionary.Value.FromInt6(ints[0], ints[1], ints[2], ints[3], ints[4], ints[5])
                };
                return true;

            case BootstrapDictionary.ValueType.Long:
                if (pos + 8 > end)
                {
                    return false;
                }

                value = BootstrapDictionary.Value.FromLong(MemoryMarshal.Read<long>(stream[pos..]));
                pos += 8;
                return true;

            case BootstrapDictionary.ValueType.DateTime:
                if (pos + 8 > end)
                {
                    return false;
                }

                var ticks = MemoryMarshal.Read<long>(stream[pos..]);
                pos += 8;
                // A damaged stream can carry ticks outside DateTime's range; clamp rather than throw.
                value = BootstrapDictionary.Value.FromDateTime(ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
                    ? new DateTime(ticks)
                    : DateTime.MinValue);
                return true;

            case BootstrapDictionary.ValueType.String:
                var start = pos;
                while (pos < end && stream[pos] != 0)
                {
                    pos++;
                }

                if (pos >= end)
                {
                    return false;
                }

                value = BootstrapDictionary.Value.FromString(Encoding.UTF8.GetString(stream[start..pos]));
                pos++;
                return true;

            default:
                return false;
        }
    }

    private static string ReadFixedString(ReadOnlySpan<byte> field)
    {
        var len = field.IndexOf((byte)0);
        if (len < 0)
        {
            len = field.Length;
        }

        return Encoding.UTF8.GetString(field[..len]);
    }
}
