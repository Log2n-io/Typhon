using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Dedicated background thread that drains all <see cref="ThreadSlotRegistry"/> slot rings on a fixed cadence, sorts the merged records by
/// timestamp, and fans them out to every attached <see cref="IProfilerExporter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Base class:</b> derives from <see cref="HighResolutionTimerServiceBase"/> to inherit the three-phase self-calibrating wait
/// (Sleep → Yield → Spin). Hits the 1 ms target cadence on Windows without P/Invoke, gives us free timing metrics, and integrates with the
/// resource graph.
/// </para>
/// <para>
/// <b>Drain algorithm (variable-size records, Phase 3):</b>
/// <list type="number">
///   <item>Scan 0..<see cref="ThreadSlotRegistry.HighWaterMark"/>, reading each slot's state.</item>
///   <item>For Active / Retiring slots, <see cref="TraceRecordRing.Drain"/> into <see cref="_mergeScratch"/>. Track per-record offsets in
///         <see cref="_offsets"/> by walking the appended bytes' u16 size fields — and simultaneously extract each record's 8-byte start
///         timestamp from offset +4 of the common header into the parallel <see cref="_keys"/> array. One touch of the byte buffer covers
///         both tasks, keeping the cache line hot.</item>
///   <item>For fully-drained Retiring slots, transition them to Free via <see cref="ThreadSlotRegistry.FreeRetiringSlot"/>.</item>
///   <item>Sort <see cref="_keys"/> ascending, carrying <see cref="_offsets"/> along for the ride, via
///         <see cref="Array.Sort{TKey, TValue}(TKey[], TValue[], int, int)"/>. This is the primitive-key specialization — direct
///         <c>long</c> compares, no virtual dispatch through an <see cref="IComparer{T}"/>, no per-comparison re-dereference of the
///         byte buffer. For typical N ≈ 15k records the sort drops from ~3 ms (IComparer path) to ~0.5 ms.</item>
///   <item>Walk sorted offsets, slice into <see cref="TraceRecordBatch"/>es sized to <see cref="TraceRecordBatchPool.MaxPayloadBytes"/>, and
///         <c>TryEnqueue</c> each onto every attached exporter's queue.</item>
/// </list>
/// </para>
/// <para>
/// <b>Why full sort instead of k-way merge across slots:</b> an earlier design considered merging pre-sorted per-slot runs (K-way merge is
/// O(N log K) vs. O(N log N)), but per-slot runs are NOT sorted by start-timestamp. Span records are emitted on <c>Dispose</c>, so a nested
/// chain like <c>Commit → Insert</c> writes the inner record first (smaller end-ts, but larger start-ts) and the outer record last. Each
/// slot's run is sorted by end-timestamp, not start-timestamp — and the viewer needs start-timestamp order. Extracting keys upfront and
/// using the primitive-specialized sort gives a comparable speedup with much less code and zero assumptions about run order.
/// </para>
/// <para>
/// <b>Backpressure:</b> drop-newest at the exporter queue. If an exporter is slow, the consumer keeps draining producer rings so the producer
/// hot path never blocks — but the exporter sees gaps. <see cref="TraceRecordBatch.Release"/> is called on drop to balance the refcount.
/// </para>
/// </remarks>
internal sealed class ProfilerConsumerThread : HighResolutionTimerServiceBase
{
    private readonly long _intervalTicks;
    private readonly byte[] _mergeScratch;
    private readonly int[] _offsets;
    private readonly long[] _keys;
    private readonly List<IProfilerExporter> _exporters;
    private long _nextTick;
    private bool _selfOptOutDone;
    private long _batchesFannedOut;
    private long _recordsFannedOut;

    /// <summary>Diagnostic: total batches FanOut has produced (each batch is enqueued to every exporter's queue).</summary>
    public long BatchesFannedOut => _batchesFannedOut;

    /// <summary>Diagnostic: total records written into batches so far.</summary>
    public long RecordsFannedOut => _recordsFannedOut;

    /// <summary>The exporter list this consumer fans out to. Mutated only at <c>TyphonProfiler.Start/Stop</c>, so iteration is lock-free.</summary>
    public List<IProfilerExporter> Exporters => _exporters;

    public ProfilerConsumerThread(IResource parent, ProfilerOptions options, List<IProfilerExporter> exporters) : base("ConsumerThread", parent)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        options.Validate();

        _intervalTicks = (long)(Stopwatch.Frequency * options.ConsumerCadence.TotalSeconds);
        if (_intervalTicks < 1)
        {
            _intervalTicks = 1;
        }

        _mergeScratch = new byte[options.MergeBufferBytes];
        // Worst-case record count: the smallest possible record is a 12 B TickStart (common header only). Size the offsets array so a scratch
        // packed entirely with min-size records still fits. +1 guards against integer division truncation.
        var maxRecords = (options.MergeBufferBytes / TraceRecordHeader.CommonHeaderSize) + 1;
        _offsets = new int[maxRecords];
        _keys = new long[maxRecords];
        _exporters = exporters ?? throw new ArgumentNullException(nameof(exporters));
        _nextTick = Stopwatch.GetTimestamp();
    }

    /// <inheritdoc />
    protected override string ThreadName => "TyphonProfilerConsumer";

    /// <inheritdoc />
    protected override long GetNextTick()
    {
        _nextTick += _intervalTicks;
        return _nextTick;
    }

    /// <inheritdoc />
    protected override void ExecuteCallbacks(long scheduledTick, long actualTick)
    {
        if (!_selfOptOutDone)
        {
            TyphonEvent.SuppressActivityContextOnThisThread();
            _selfOptOutDone = true;
        }
        DrainAndFanOut();
    }

    /// <summary>
    /// Drain all slot rings, sort merged records by timestamp, and fan out to exporters. Internal for testing — production callers should not
    /// invoke directly; the timer base class handles cadence.
    /// </summary>
    private void DrainAndFanOut()
    {
        // ── Phase 1: drain all slots into the merge scratch, tracking record offsets ──
        var bytesDrained = 0;
        var recordCount = 0;
        var scanLimit = ThreadSlotRegistry.HighWaterMark;

        for (var i = 0; i < scanLimit; i++)
        {
            var state = ThreadSlotRegistry.GetSlotState(i);
            if (state != (int)SlotState.Active && state != (int)SlotState.Retiring)
            {
                continue;
            }

            var slot = ThreadSlotRegistry.GetSlot(i);
            var primary = slot.Buffer;
            if (primary == null)
            {
                continue;
            }

            // Walk the slot's chain forward from ChainHead. Drain each ring, recycling spillovers back to the pool
            // when their buffer empties AND a successor is observed. The primary (slot.Buffer) is never recycled —
            // it stays attached for the slot's lifetime and is reused across re-claims.
            //
            // Termination conditions per ring:
            //   - destination span runs out (merge scratch full)  → break outer
            //   - record count saturates                          → break outer
            //   - current head ring still has data after drain    → stop on this slot, try this ring next pass
            //   - current head ring empty AND has no successor    → stop on this slot, primary is fully drained
            //   - current head ring empty AND has successor       → recycle (if spillover), advance ChainHead
            var head = slot.ChainHead;
            var slotDoneOuter = false;
            while (head != null && !slotDoneOuter)
            {
                var remaining = _mergeScratch.Length - bytesDrained;
                if (remaining <= 0)
                {
                    slotDoneOuter = true;
                    break;
                }
                var drained = head.Drain(_mergeScratch.AsSpan(bytesDrained, remaining));

                // Walk the newly-drained bytes to build the offsets + keys index. Same logic as the pre-chain
                // implementation — the chain just feeds the same merge buffer through multiple Drain calls.
                var walkPos = bytesDrained;
                var walkEnd = bytesDrained + drained;
                while (walkPos < walkEnd)
                {
                    if (recordCount >= _offsets.Length)
                    {
                        slotDoneOuter = true;  // can't index any more records this pass
                        break;
                    }

                    var headerSpan = _mergeScratch.AsSpan(walkPos);
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(headerSpan);
                    if (size == 0 || walkPos + size > walkEnd)
                    {
                        slotDoneOuter = true;  // defensive stop on corrupt data
                        break;
                    }

                    _offsets[recordCount] = walkPos;
                    _keys[recordCount] = BinaryPrimitives.ReadInt64LittleEndian(headerSpan[4..]);
                    recordCount++;
                    walkPos += size;
                }
                bytesDrained = walkPos;

                if (slotDoneOuter || drained == 0 && !head.IsEmpty)
                {
                    // Either we ran out of merge buffer, or Drain stopped on a record-doesn't-fit boundary; either
                    // way the head ring still has data. Don't advance — pick up where we left off next pass.
                    break;
                }

                if (!head.IsEmpty)
                {
                    // Head still has data (we drained some but not all this pass). Stop here.
                    break;
                }

                // Head is empty. Can we advance to the next ring?
                var next = head.Next;
                if (next == null)
                {
                    // No successor — head is the producer's current ChainTail OR the producer hasn't extended yet.
                    // Either way, nothing to advance to.
                    break;
                }

                // Empty head AND has successor → recycle (if it's a spillover) and advance ChainHead.
                var spent = head;
                head = next;
                slot.ChainHead = head;
                if (spent != primary)
                {
                    SpilloverRingPool.Release(spent);
                }
            }

            // A retiring slot can only be freed once its entire chain has fully collapsed back to an empty primary
            // with no successor — the producer is dead and can never extend again, so eventually that condition
            // will hold across subsequent drain passes.
            if (state == (int)SlotState.Retiring
                && slot.ChainHead == primary
                && primary.IsEmpty
                && primary.Next == null)
            {
                ThreadSlotRegistry.FreeRetiringSlot(i);
            }
        }

        if (recordCount == 0)
        {
            return;
        }

        // Early exit: nothing attached to consume.
        var exporterCount = _exporters.Count;
        if (exporterCount == 0)
        {
            return;
        }

        // ── Phase 2: sort offsets by the extracted keys using the primitive-long fast path ──
        // Array.Sort<TKey, TValue> with TKey=long goes through the introsort specialization that does direct long compares with no virtual
        // dispatch and no IComparer allocation. Keys were pre-extracted during drain, so this phase touches ONLY _keys and _offsets — the
        // 500 KB _mergeScratch buffer stays untouched during the sort, sparing ~200K cache-line re-fetches vs. the old IComparer approach
        // that dereferenced _mergeScratch on every comparison.
        Array.Sort(_keys, _offsets, 0, recordCount);

        // ── Phase 3: slice sorted records into batches and TryEnqueue onto each exporter ──
        EmitBatches(exporterCount, recordCount);
    }

    /// <summary>
    /// Walk the sorted offsets, copying records into <see cref="TraceRecordBatch"/>es sized to <see cref="TraceRecordBatchPool.MaxPayloadBytes"/>.
    /// Each completed batch is fanned out to every exporter; dropped enqueues are balanced via <see cref="TraceRecordBatch.Release"/>.
    /// </summary>
    private void EmitBatches(int exporterCount, int recordCount)
    {
        var batch = TraceRecordBatchPool.Rent(exporterCount);
        var dstPos = 0;

        for (var i = 0; i < recordCount; i++)
        {
            var srcOffset = _offsets[i];
            var size = BinaryPrimitives.ReadUInt16LittleEndian(_mergeScratch.AsSpan(srcOffset));

            // If this record wouldn't fit in the current batch, seal and fan out, then rent a fresh batch.
            if (dstPos + size > batch.Payload.Length || batch.Count >= batch.Offsets.Length)
            {
                batch.PayloadBytes = dstPos;
                FanOut(batch, exporterCount);

                batch = TraceRecordBatchPool.Rent(exporterCount);
                dstPos = 0;
            }

            batch.Offsets[batch.Count++] = dstPos;
            _mergeScratch.AsSpan(srcOffset, size).CopyTo(batch.Payload.AsSpan(dstPos));
            dstPos += size;
        }

        if (batch.Count > 0)
        {
            batch.PayloadBytes = dstPos;
            FanOut(batch, exporterCount);
        }
        else
        {
            // Rented batch we never filled — release our reference so it returns to the pool.
            batch.Release();
        }
    }

    private void FanOut(TraceRecordBatch batch, int exporterCount)
    {
        Interlocked.Increment(ref _batchesFannedOut);
        Interlocked.Add(ref _recordsFannedOut, batch.Count);
        for (var e = 0; e < exporterCount; e++)
        {
            _exporters[e].Queue.TryEnqueue(batch);
        }
    }

    /// <summary>
    /// Stops the timer loop without disposing the instance. Must be called before <see cref="FinalDrainAndComplete"/> to avoid a race between the
    /// timer's ongoing drain pass and the shutdown drain on the same <c>_mergeScratch</c> / <c>_offsets</c> scratch buffers.
    /// </summary>
    internal void StopTimer() => StopTimerThread();

    /// <summary>
    /// Final-drain hook called by <c>TyphonProfiler.Stop</c> after <see cref="StopTimer"/>. Drains any remaining records and completes each
    /// exporter queue so the exporter threads' foreach loops terminate.
    /// </summary>
    /// <remarks>
    /// <b>Why a loop:</b> a single <see cref="DrainAndFanOut"/> pass drains as many records as fit in <c>_mergeScratch</c> (typ. 500 KB). On
    /// shutdown from a bursty workload (e.g., checkpoint-after-spawn) a single slot can hold more than that — the tail stays in the ring and is
    /// lost when <c>Dispose</c> tears the slots down. Looping until every producer ring reports <c>IsEmpty</c> (bounded by a large safety
    /// cap to prevent runaway if a producer somehow writes after <c>GcTracingHost.Dispose</c> + <c>StopTimer</c>) captures the full tail.
    /// </remarks>
    internal void FinalDrainAndComplete()
    {
        const int maxDrainPasses = 256;
        int passes = 0;
        long prevPendingBytes = CountPendingBytesAcrossSlots();
        long zeroProgressPasses = 0;

        for (var pass = 0; pass < maxDrainPasses; pass++)
        {
            DrainAndFanOut();
            passes++;
            if (AllSlotsEmpty())
            {
                break;
            }
            var curPendingBytes = CountPendingBytesAcrossSlots();
            if (curPendingBytes >= prevPendingBytes)
            {
                zeroProgressPasses++;
            }
            prevPendingBytes = curPendingBytes;
        }
        FinalDrainPasses = passes;
        FinalDrainZeroProgressPasses = zeroProgressPasses;
        FinalDrainPendingBytes = CountPendingBytesAcrossSlots();

        foreach (var exporter in _exporters)
        {
            exporter.Queue.CompleteAdding();
        }
    }

    public long FinalDrainPasses { get; private set; }

    public long FinalDrainZeroProgressPasses { get; private set; }

    public long FinalDrainPendingBytes { get; private set; }

    private static long CountPendingBytesAcrossSlots()
    {
        long total = 0;
        var scanLimit = ThreadSlotRegistry.HighWaterMark;
        for (var i = 0; i < scanLimit; i++)
        {
            var state = ThreadSlotRegistry.GetSlotState(i);
            if (state != (int)SlotState.Active && state != (int)SlotState.Retiring)
            {
                continue;
            }
            // Walk the chain forward from ChainHead to count pending bytes across the primary AND any spillovers.
            // Without this walk, FinalDrainAndComplete would terminate while spillover rings still hold data.
            var slot = ThreadSlotRegistry.GetSlot(i);
            var ring = slot.ChainHead;
            while (ring != null)
            {
                total += ring.BytesPending;
                ring = ring.Next;
            }
        }
        return total;
    }

    /// <summary>Diagnostic: dump per-slot state + pending bytes. Called at shutdown when FinalDrain couldn't empty the rings.</summary>
    public static string DumpSlotStates()
    {
        var sb = new System.Text.StringBuilder();
        var hwm = ThreadSlotRegistry.HighWaterMark;
        sb.Append($"HWM={hwm}");
        for (var i = 0; i < hwm; i++)
        {
            var state = ThreadSlotRegistry.GetSlotState(i);
            var buffer = ThreadSlotRegistry.GetSlot(i).Buffer;
            var pending = buffer?.BytesPending ?? 0;
            var stateName = state switch { 0 => "Free", 1 => "Active", 2 => "Retiring", _ => state.ToString() };
            sb.Append($" [slot{i}:{stateName} pending={pending}");
            if (buffer != null && pending > 0)
            {
                sb.Append($" {buffer.DumpAtTail(24)}");
            }
            sb.Append(']');
        }
        return sb.ToString();
    }


    /// <summary>Returns <c>true</c> iff every active/retiring slot's chain (primary + spillovers) has no pending records.</summary>
    private static bool AllSlotsEmpty()
    {
        var scanLimit = ThreadSlotRegistry.HighWaterMark;
        for (var i = 0; i < scanLimit; i++)
        {
            var state = ThreadSlotRegistry.GetSlotState(i);
            if (state != (int)SlotState.Active && state != (int)SlotState.Retiring)
            {
                continue;
            }
            var slot = ThreadSlotRegistry.GetSlot(i);
            // Walk the chain — FinalDrainAndComplete would otherwise terminate prematurely while spillovers
            // still hold records.
            var ring = slot.ChainHead;
            while (ring != null)
            {
                if (!ring.IsEmpty)
                {
                    return false;
                }
                ring = ring.Next;
            }
        }
        return true;
    }

}
