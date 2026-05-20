using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using Typhon.Profiler;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// The §8.7 class of one CPU sample — whether the sampled thread was actually burning a core, parked because the program
/// asked it to, or frozen from outside. <see cref="Unknown"/> means the classifier has no scheduling evidence for that
/// <c>(qpc, slot)</c> and the caller must fall back to the <c>SampleType</c> proxy (§8.7 graceful degradation).
/// </summary>
public enum SampleClass : byte
{
    /// <summary>No scheduling evidence — caller falls back to the <c>SampleType</c> Managed/External proxy.</summary>
    Unknown = 0,

    /// <summary>Thread was executing on a core — real CPU cost, folds per-method.</summary>
    OnCpu,

    /// <summary>Thread was blocked because the program asked (lock, <c>Monitor.Wait</c>, IO, sleep, idle pool thread).
    /// The stack says <i>why</i> — folds per-method, but in the wall-clock view only.</summary>
    Voluntary,

    /// <summary>Thread frozen by a GC EE-suspension. Stack is noise — collapses into the <c>[GC suspension]</c> aggregate.</summary>
    InvoluntaryGc,

    /// <summary>Thread booted off the core by the OS scheduler (preemption / quantum-end). Collapses into <c>[Preempted]</c>.</summary>
    InvoluntaryScheduler,

    /// <summary>Thread frozen on a paging / virtual-memory stall. Collapses into <c>[Paging]</c>.</summary>
    InvoluntaryPaging,
}

/// <summary>
/// Per-session, post-hoc classifier that joins each CPU sample against scheduling evidence already present in the trace —
/// GC-suspension intervals (<see cref="TraceEventKind.GcSuspension"/>, cross-platform runtime events) and per-thread
/// context-switch slices (<see cref="TraceEventKind.ThreadContextSwitch"/>, Windows ETW) — to label it on-CPU /
/// voluntary-wait / involuntary-stall. See <c>claude/design/Profiler/11-cpu-sampling-integration.md</c> §8.7.
/// </summary>
/// <remarks>
/// Built once per session by a single chunk-stream walk (the same pattern as <see cref="SpanInstanceIndex.Build"/> and
/// <see cref="TraceSessionRuntime.ComputeGcSuspensions"/>) — <b>zero engine / capture / hot-path cost</b>: nothing new is
/// measured, this is a join of data the trace already carries. Classification of one sample is a binary search per input,
/// <c>O(log n)</c>. When the trace has no context-switch records (non-Windows / non-elevated / <c>ThreadScheduling</c> off)
/// <see cref="ClassificationAvailable"/> is <c>false</c>: GC samples are still classified, everything else degrades to
/// <see cref="SampleClass.Unknown"/> so the caller falls back to the <c>SampleType</c> proxy.
/// </remarks>
public sealed class SampleClassifier
{
    /// <summary>Sentinel for traces with no scheduling evidence (or any build failure) — every sample classifies <see cref="SampleClass.Unknown"/>.</summary>
    public static readonly SampleClassifier Empty = new([], new Dictionary<int, OnCpuSlice[]>(), false);

    /// <summary>One ON-CPU slice for a Typhon thread: <c>[Start, End]</c> QPC, plus the §8.7 class of the off-CPU gap that follows it.</summary>
    public readonly record struct OnCpuSlice(long Start, long End, SampleClass OffCpuClass);

    // GC EE-suspension is process-wide — every managed thread is frozen — so intervals are global, not per-slot. Merged + sorted by Start.
    private readonly (long Start, long End)[] _gcIntervals;
    // ON-CPU slices per Typhon thread slot, sorted by Start. A sample whose qpc falls between two slices was off-CPU.
    private readonly Dictionary<int, OnCpuSlice[]> _slicesBySlot;

    private SampleClassifier((long Start, long End)[] gcIntervals, Dictionary<int, OnCpuSlice[]> slicesBySlot, bool classificationAvailable)
    {
        _gcIntervals = gcIntervals;
        _slicesBySlot = slicesBySlot;
        ClassificationAvailable = classificationAvailable;
    }

    /// <summary>
    /// True when the trace carried at least one context-switch record — i.e. the full on-CPU / voluntary / involuntary split
    /// is available. False ⇒ degraded mode: only GC stalls are classified, the view labels itself "thread time" (§8.7).
    /// </summary>
    public bool ClassificationAvailable { get; }

    /// <summary>
    /// Classify the sample at <paramref name="qpc"/> on Typhon thread <paramref name="threadSlot"/> (<c>-1</c> for a
    /// non-Typhon thread). GC suspension wins over context-switch evidence (rule order — §8.7). Returns
    /// <see cref="SampleClass.Unknown"/> when no context-switch slice covers the sample (degraded / non-Typhon / out of range).
    /// </summary>
    public SampleClass Classify(long qpc, int threadSlot)
    {
        // Rule 1 — a GC EE-suspension freezes every thread; it outranks any context-switch slice.
        if (GcIntervalContains(qpc))
        {
            return SampleClass.InvoluntaryGc;
        }
        // Rule 2 — locate the thread's ON-CPU slice covering qpc; the gap after a slice carries that slice's off-CPU class.
        if (threadSlot < 0 || !_slicesBySlot.TryGetValue(threadSlot, out var slices) || slices.Length == 0)
        {
            return SampleClass.Unknown;
        }
        var i = LatestSliceStartingAtOrBefore(slices, qpc);
        if (i < 0)
        {
            return SampleClass.Unknown; // qpc precedes the first observed slice — no evidence.
        }
        ref readonly var slice = ref slices[i];
        return qpc <= slice.End ? SampleClass.OnCpu : slice.OffCpuClass;
    }

    private bool GcIntervalContains(long qpc)
    {
        var lo = 0;
        var hi = _gcIntervals.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            ref readonly var iv = ref _gcIntervals[mid];
            if (qpc < iv.Start)
            {
                hi = mid - 1;
            }
            else if (qpc > iv.End)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Index of the last slice whose <c>Start ≤ qpc</c>, or <c>-1</c> when every slice starts after <paramref name="qpc"/>.</summary>
    private static int LatestSliceStartingAtOrBefore(OnCpuSlice[] slices, long qpc)
    {
        var lo = 0;
        var hi = slices.Length - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (slices[mid].Start <= qpc)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Build — one chunk-stream walk
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a classifier directly from interval data — the non-chunk-walk construction path, for tests and any future
    /// non-file sample source. <paramref name="gcIntervals"/> are merged; each slot's <paramref name="slicesBySlot"/> entry
    /// is sorted by start. Mirrors <see cref="SpanInstanceIndex.FromWindows"/>.
    /// </summary>
    public static SampleClassifier Create(
        IEnumerable<(long Start, long End)> gcIntervals,
        IReadOnlyDictionary<int, OnCpuSlice[]> slicesBySlot,
        bool classificationAvailable)
    {
        ArgumentNullException.ThrowIfNull(gcIntervals);
        ArgumentNullException.ThrowIfNull(slicesBySlot);
        var slices = new Dictionary<int, List<OnCpuSlice>>(slicesBySlot.Count);
        foreach (var kv in slicesBySlot)
        {
            slices[kv.Key] = [.. kv.Value ?? []];
        }
        return new SampleClassifier(
            MergeIntervals([.. gcIntervals]),
            FinalizeSlices(slices),
            classificationAvailable);
    }

    /// <summary>
    /// Walks every chunk in <paramref name="reader"/> once, decoding GC-suspension intervals and per-thread context-switch
    /// slices into a ready-to-query classifier. Returns <see cref="Empty"/> for a reader with no chunks.
    /// </summary>
    public static SampleClassifier Build(TraceFileCacheReader reader)
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
        var gcIntervals = new List<(long Start, long End)>();
        var slicesBySlot = new Dictionary<int, List<OnCpuSlice>>();
        var sawContextSwitch = false;
        try
        {
            foreach (var entry in reader.ChunkManifest)
            {
                var compSpan = compressedScratch.AsSpan(0, (int)entry.CacheByteLength);
                var uncompSpan = uncompressedScratch.AsSpan(0, (int)entry.UncompressedBytes);
                reader.DecompressChunk(entry, uncompSpan, compSpan);
                WalkRecords(uncompSpan, gcIntervals, slicesBySlot, ref sawContextSwitch);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressedScratch);
            ArrayPool<byte>.Shared.Return(uncompressedScratch);
        }

        return new SampleClassifier(MergeIntervals(gcIntervals), FinalizeSlices(slicesBySlot), sawContextSwitch);
    }

    /// <summary>
    /// Scans one decompressed chunk's packed records. Mirrors the record-walk loop in
    /// <see cref="SpanInstanceIndex.WalkRecordsForSpans"/>: <c>u16 size</c> prefix, kind byte at offset 2, <c>0</c> / <c>0xFFFF</c>
    /// size terminates the scan. Picks up <see cref="TraceEventKind.GcSuspension"/> spans and
    /// <see cref="TraceEventKind.ThreadContextSwitch"/> instant records; everything else is skipped.
    /// </summary>
    private static void WalkRecords(
        ReadOnlySpan<byte> records,
        List<(long Start, long End)> gcIntervals,
        Dictionary<int, List<OnCpuSlice>> slicesBySlot,
        ref bool sawContextSwitch)
    {
        const int commonHeader = TraceRecordHeader.CommonHeaderSize;       // 12
        const int minSpanRecord = commonHeader + TraceRecordHeader.SpanHeaderExtensionSize; // 37
        const int contextSwitchRecord = commonHeader + 13;                 // 25 — see ThreadContextSwitchEvent wire layout

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

            if (kind == TraceEventKind.GcSuspension && size >= minSpanRecord)
            {
                TraceRecordHeader.ReadCommonHeader(records.Slice(pos, size), out _, out _, out _, out var startQpc);
                TraceRecordHeader.ReadSpanHeaderExtension(records.Slice(pos + commonHeader), out var durationTicks, out _, out _, out _);
                if (durationTicks > 0)
                {
                    gcIntervals.Add((startQpc, startQpc + durationTicks));
                }
            }
            else if (kind == TraceEventKind.ThreadContextSwitch && size >= contextSwitchRecord)
            {
                // Wire layout (ThreadContextSwitchEvent): 12 B common header + 13 B payload. Common-header timestamp is the
                // slice START qpc; the slice's thread is the payload's TargetSlotIdx (the common-header slot is the pump's).
                // Payload: [0] u8 TargetSlotIdx, [1] u8 ProcessorNumber, [2] u8 WaitReason, [3] u8 ThreadState,
                //          [4] u8 GettingIdle, [5..9) u32 DurationQpc, [9..13) u32 ReadyTimeQpc.
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
                list.Add(new OnCpuSlice(startQpc, startQpc + durationQpc, OffCpuClassFor(waitReason)));
                sawContextSwitch = true;
            }

            pos += size;
        }
    }

    private static (long Start, long End)[] MergeIntervals(List<(long Start, long End)> intervals)
    {
        if (intervals.Count == 0)
        {
            return [];
        }
        intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(long Start, long End)>(intervals.Count);
        var cur = intervals[0];
        for (var i = 1; i < intervals.Count; i++)
        {
            var next = intervals[i];
            if (next.Start <= cur.End)
            {
                cur.End = Math.Max(cur.End, next.End);
            }
            else
            {
                merged.Add(cur);
                cur = next;
            }
        }
        merged.Add(cur);
        return merged.ToArray();
    }

    private static Dictionary<int, OnCpuSlice[]> FinalizeSlices(Dictionary<int, List<OnCpuSlice>> bySlot)
    {
        var result = new Dictionary<int, OnCpuSlice[]>(bySlot.Count);
        foreach (var kv in bySlot)
        {
            var list = kv.Value;
            list.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            result[kv.Key] = list.ToArray();
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Wait-reason → §8.7 off-CPU class
    // ═══════════════════════════════════════════════════════════════════════

    // Mirrors the client's WAIT_REASON_CATEGORY_LUT (ClientApp/src/libs/profiler/model/types.ts) so server and client agree:
    // Preempted/QuantumEnd buckets → involuntary(scheduler); paging/VM buckets → involuntary(paging); everything else
    // (sync-primitive waits, user waits, idle, Other) → voluntary. §8.7's axis is voluntary-vs-involuntary, not GC-vs-rest.
    private static readonly SampleClass[] WaitClassLut = BuildWaitClassLut();

    private static SampleClass[] BuildWaitClassLut()
    {
        var lut = new SampleClass[256];
        Array.Fill(lut, SampleClass.Voluntary);
        // Preempted / dispatch-interrupt / yield / process-in-swap, plus quantum-end — booted off the core, not a program decision.
        foreach (var r in new[] { 23, 30, 31, 32, 33 })
        {
            lut[r] = SampleClass.InvoluntaryScheduler;
        }
        // Paging / virtual-memory / pool-allocation stalls — frozen waiting on the memory manager.
        foreach (var r in new[] { 1, 2, 3, 8, 9, 10, 18, 19 })
        {
            lut[r] = SampleClass.InvoluntaryPaging;
        }
        return lut;
    }

    /// <summary>The §8.7 off-CPU class implied by a kernel <see cref="ThreadWaitReason"/> byte.</summary>
    public static SampleClass OffCpuClassFor(byte waitReason) => WaitClassLut[waitReason];
}
