using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using Typhon.Profiler;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// The single decompress-once walk of a trace's cache chunk stream that feeds <b>both</b> per-session lazy indexes —
/// the <see cref="SpanInstanceIndex"/> (span windows by kind, for scope resolution) and the
/// <see cref="SampleClassifier"/> (GC-suspension intervals + context-switch slices, for off-CPU classification).
/// Each index previously ran its own full LZ4-decompress pass over every chunk with a near-identical record loop
/// (#351 / #364); this folds the two into one pass and one record loop.
/// </summary>
/// <remarks>
/// The eager build-time GC-suspension scan (<see cref="TraceSessionRuntime.ComputeGcSuspensions"/>) stays separate by
/// design: it runs during metadata assembly — before any lazy index is touched — and emits a different shape (µs +
/// thread slot), so it cannot reuse this lazy walk without forcing the indexes to build eagerly.
/// </remarks>
internal static class TraceChunkScan
{
    /// <summary>
    /// Walks every chunk in <paramref name="reader"/> once, decoding span windows, GC-suspension intervals, and
    /// per-thread context-switch slices, then constructs both indexes via their unit-tested finalizers
    /// (<see cref="SpanInstanceIndex.FromWindows"/> / <see cref="SampleClassifier.Create"/>). A reader with no chunks
    /// yields the two empties.
    /// </summary>
    internal static void BuildIndexes(TraceFileCacheReader reader, out SpanInstanceIndex spans, out SampleClassifier samples)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var byKind = new Dictionary<int, List<(long Start, long End)>>();
        var gcIntervals = new List<(long Start, long End)>();
        var slicesBySlot = new Dictionary<int, List<SampleClassifier.OnCpuSlice>>();
        var sawContextSwitch = false;

        if (reader.ChunkManifest.Count > 0)
        {
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

            if (maxUncompressed > 0)
            {
                var compressedScratch = ArrayPool<byte>.Shared.Rent(maxCompressed);
                var uncompressedScratch = ArrayPool<byte>.Shared.Rent(maxUncompressed);
                try
                {
                    foreach (var entry in reader.ChunkManifest)
                    {
                        var compSpan = compressedScratch.AsSpan(0, (int)entry.CacheByteLength);
                        var uncompSpan = uncompressedScratch.AsSpan(0, (int)entry.UncompressedBytes);
                        reader.DecompressChunk(entry, uncompSpan, compSpan);
                        WalkRecords(uncompSpan, byKind, gcIntervals, slicesBySlot, ref sawContextSwitch);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(compressedScratch);
                    ArrayPool<byte>.Shared.Return(uncompressedScratch);
                }
            }
        }

        spans = BuildSpanIndex(byKind);
        samples = BuildClassifier(gcIntervals, slicesBySlot, sawContextSwitch);
    }

    private static SpanInstanceIndex BuildSpanIndex(Dictionary<int, List<(long Start, long End)>> byKind)
    {
        if (byKind.Count == 0)
        {
            return SpanInstanceIndex.Empty;
        }
        var windowsByKind = new Dictionary<int, (long Start, long End)[]>(byKind.Count);
        foreach (var kv in byKind)
        {
            windowsByKind[kv.Key] = kv.Value.ToArray();
        }
        return SpanInstanceIndex.FromWindows(windowsByKind);
    }

    private static SampleClassifier BuildClassifier(
        List<(long Start, long End)> gcIntervals,
        Dictionary<int, List<SampleClassifier.OnCpuSlice>> slicesBySlot,
        bool sawContextSwitch)
    {
        var slices = new Dictionary<int, SampleClassifier.OnCpuSlice[]>(slicesBySlot.Count);
        foreach (var kv in slicesBySlot)
        {
            slices[kv.Key] = kv.Value.ToArray();
        }
        return SampleClassifier.Create(gcIntervals, slices, sawContextSwitch);
    }

    /// <summary>
    /// Scans one decompressed chunk's packed records, feeding all sinks. The union of the two former per-index loops:
    /// <c>u16 size</c> prefix, kind byte at offset 2, <c>0</c> / <c>0xFFFF</c> terminates. Every span record yields a
    /// window in its kind bucket (for scope resolution); a <see cref="TraceEventKind.GcSuspension"/> span also yields a
    /// GC interval; a <see cref="TraceEventKind.ThreadContextSwitch"/> instant yields a per-slot ON-CPU slice. The
    /// per-sink guards match the originals exactly, so the constructed indexes are byte-for-byte what two passes produced.
    /// </summary>
    private static void WalkRecords(
        ReadOnlySpan<byte> records,
        Dictionary<int, List<(long Start, long End)>> byKind,
        List<(long Start, long End)> gcIntervals,
        Dictionary<int, List<SampleClassifier.OnCpuSlice>> slicesBySlot,
        ref bool sawContextSwitch)
    {
        const int commonHeader = TraceRecordHeader.CommonHeaderSize;                        // 12
        const int minSpanRecord = commonHeader + TraceRecordHeader.SpanHeaderExtensionSize; // 37
        const int contextSwitchRecord = commonHeader + 13;                                  // 25 — ThreadContextSwitchEvent wire layout

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
                TraceRecordHeader.ReadSpanHeaderExtension(records.Slice(pos + commonHeader), out var durationTicks, out _, out _, out _);
                if (!byKind.TryGetValue((int)kind, out var list))
                {
                    list = [];
                    byKind[(int)kind] = list;
                }
                list.Add((startQpc, startQpc + durationTicks));

                if (kind == TraceEventKind.GcSuspension && size >= minSpanRecord && durationTicks > 0)
                {
                    gcIntervals.Add((startQpc, startQpc + durationTicks));
                }
            }
            else if (kind == TraceEventKind.ThreadContextSwitch && size >= contextSwitchRecord)
            {
                // Wire layout (ThreadContextSwitchEvent): 12 B common header + 13 B payload. Common-header timestamp is the
                // slice START qpc; the slice's thread is the payload's TargetSlotIdx (the common-header slot is the pump's).
                // Payload: [0] u8 TargetSlotIdx, [2] u8 WaitReason, [5..9) u32 DurationQpc.
                TraceRecordHeader.ReadCommonHeader(records.Slice(pos, size), out _, out _, out _, out var startQpc);
                var payload = records.Slice(pos + commonHeader);
                int targetSlot = payload[0];
                var waitReason = payload[2];
                var durationQpc = BinaryPrimitives.ReadUInt32LittleEndian(payload[5..]);
                if (!slicesBySlot.TryGetValue(targetSlot, out var list))
                {
                    list = [];
                    slicesBySlot[targetSlot] = list;
                }
                list.Add(new SampleClassifier.OnCpuSlice(startQpc, startQpc + durationQpc, SampleClassifier.OffCpuClassFor(waitReason)));
                sawContextSwitch = true;
            }

            pos += size;
        }
    }
}
