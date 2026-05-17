using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using Typhon.Profiler;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Per-session index of every instrumented span instance in a trace, grouped by <see cref="TraceEventKind"/> (#351 Phase 5,
/// §8.3). Built lazily by a one-pass walk of the cache chunk stream — the same pattern as
/// <see cref="TraceSessionRuntime.ComputeGcSuspensions"/> — and consumed by the <see cref="Services.ScopeResolver"/> to turn a
/// "scope to span kind X" request into the union of that kind's time-windows.
/// </summary>
/// <remarks>
/// Each window is a <c>[startQpc, endQpc)</c> pair in the trace's QPC base (the same base as the CPU samples and tick
/// summaries — file traces have <c>baselineQpc 0</c>). Windows per kind are sorted by start. Best-effort: any read failure
/// yields <see cref="Empty"/> — absent span data is surfaced, never fatal to the session.
/// </remarks>
public sealed class SpanInstanceIndex
{
    /// <summary>Sentinel for traces with no decodable span instances (or any build failure).</summary>
    public static readonly SpanInstanceIndex Empty = new(new Dictionary<int, (long, long)[]>());

    private readonly Dictionary<int, (long Start, long End)[]> _windowsByKind;

    private SpanInstanceIndex(Dictionary<int, (long Start, long End)[]> windowsByKind)
    {
        _windowsByKind = windowsByKind;
    }

    /// <summary>The <see cref="TraceEventKind"/> values (as ints) that have at least one span instance in the trace.</summary>
    public IReadOnlyCollection<int> AvailableKinds => _windowsByKind.Keys;

    /// <summary>
    /// The start-sorted <c>[startQpc, endQpc)</c> windows of every instance of <paramref name="kind"/>. Empty when the trace
    /// carries no span of that kind.
    /// </summary>
    public (long Start, long End)[] WindowsForKind(int kind)
        => _windowsByKind.TryGetValue(kind, out var windows) ? windows : [];

    /// <summary>
    /// Builds an index directly from a pre-computed <c>kind → windows</c> map (the non-chunk-walk construction path).
    /// Each window list is start-sorted defensively. An empty map yields <see cref="Empty"/>.
    /// </summary>
    public static SpanInstanceIndex FromWindows(IReadOnlyDictionary<int, (long Start, long End)[]> windowsByKind)
    {
        ArgumentNullException.ThrowIfNull(windowsByKind);
        if (windowsByKind.Count == 0)
        {
            return Empty;
        }
        var copy = new Dictionary<int, (long Start, long End)[]>(windowsByKind.Count);
        foreach (var kv in windowsByKind)
        {
            var src = kv.Value ?? [];
            var windows = new (long Start, long End)[src.Length];
            Array.Copy(src, windows, src.Length);
            Array.Sort(windows, static (a, b) => a.Start.CompareTo(b.Start));
            copy[kv.Key] = windows;
        }
        return new SpanInstanceIndex(copy);
    }

    /// <summary>
    /// Walks every chunk in <paramref name="reader"/> once, extracting each span record's <c>[startQpc, startQpc + duration)</c>
    /// window grouped by kind. Returns <see cref="Empty"/> for a reader with no chunks.
    /// </summary>
    public static SpanInstanceIndex Build(TraceFileCacheReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.ChunkManifest.Count == 0)
        {
            return Empty;
        }

        var maxCompressed = 0;
        var maxUncompressed = 0;
        foreach (var entry in reader.ChunkManifest)
        {
            if ((int)entry.CacheByteLength > maxCompressed)
            {
                maxCompressed = (int)entry.CacheByteLength;
            }
            if ((int)entry.UncompressedBytes > maxUncompressed)
            {
                maxUncompressed = (int)entry.UncompressedBytes;
            }
        }
        if (maxUncompressed == 0)
        {
            return Empty;
        }

        var compressedScratch = ArrayPool<byte>.Shared.Rent(maxCompressed);
        var uncompressedScratch = ArrayPool<byte>.Shared.Rent(maxUncompressed);
        var byKind = new Dictionary<int, List<(long Start, long End)>>();
        try
        {
            foreach (var entry in reader.ChunkManifest)
            {
                var compSpan = compressedScratch.AsSpan(0, (int)entry.CacheByteLength);
                var uncompSpan = uncompressedScratch.AsSpan(0, (int)entry.UncompressedBytes);
                reader.DecompressChunk(entry, uncompSpan, compSpan);
                WalkRecordsForSpans(uncompSpan, byKind);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressedScratch);
            ArrayPool<byte>.Shared.Return(uncompressedScratch);
        }

        if (byKind.Count == 0)
        {
            return Empty;
        }

        var windowsByKind = new Dictionary<int, (long Start, long End)[]>(byKind.Count);
        foreach (var kv in byKind)
        {
            var list = kv.Value;
            list.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            windowsByKind[kv.Key] = list.ToArray();
        }
        return new SpanInstanceIndex(windowsByKind);
    }

    /// <summary>
    /// Scans one decompressed chunk's packed records, appending each span record's window to its kind bucket. Mirrors the
    /// record-walk loop in <see cref="TraceSessionRuntime.WalkRecordsForSuspensions"/>: <c>u16 size</c> prefix, kind byte at
    /// offset 2, <c>0</c> / <c>0xFFFF</c> size terminates the scan.
    /// </summary>
    private static void WalkRecordsForSpans(ReadOnlySpan<byte> records, Dictionary<int, List<(long Start, long End)>> sink)
    {
        var pos = 0;
        while (pos + 3 <= records.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size == 0 || size == 0xFFFF)
            {
                break;
            }
            if (pos + size > records.Length)
            {
                break;
            }
            var kind = (TraceEventKind)records[pos + 2];
            if (kind.IsSpan())
            {
                TraceRecordHeader.ReadCommonHeader(records.Slice(pos, size), out _, out _, out _, out var startQpc);
                TraceRecordHeader.ReadSpanHeaderExtension(
                    records.Slice(pos + TraceRecordHeader.CommonHeaderSize),
                    out var durationTicks,
                    out _,
                    out _,
                    out _);
                if (!sink.TryGetValue((int)kind, out var list))
                {
                    list = [];
                    sink[(int)kind] = list;
                }
                list.Add((startQpc, startQpc + durationTicks));
            }
            pos += size;
        }
    }
}
