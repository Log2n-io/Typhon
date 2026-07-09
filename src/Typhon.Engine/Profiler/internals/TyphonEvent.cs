using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Static producer-side API for the Tracy-style typed-event profiler. Engine call sites that want to record a span construct a typed ref-struct
/// event via one of the <c>Begin*Event</c> factories, fill its fields, and let <c>Dispose</c> publish the record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hot path:</b> every <c>Begin*Event</c> factory is a thin wrapper that loads the current slot, captures a start timestamp, and returns a
/// populated ref struct. The returned struct carries everything the <c>Dispose</c>/<c>Publish</c> path needs — no hidden state, no TLS reads on
/// the fast-path tail.
/// </para>
/// <para>
/// <b>JIT elimination when disabled:</b> the first instruction of every factory is <c>if (!TelemetryConfig.ProfilerActive) return default;</c>.
/// <c>ProfilerActive</c> is <c>static readonly</c>, initialized from config in the class's static constructor. When the profiler is disabled,
/// the JIT folds the factory body to <c>return default;</c> and every <c>Dispose</c> becomes a no-op. Zero CPU cost at the call site.
/// </para>
/// <para>
/// <b>Per-thread state:</b> <see cref="CurrentOpenSpanId"/> tracks the innermost open Typhon span on this thread for LIFO parent linking;
/// <see cref="CurrentTickNumber"/> holds the scheduler tick number for tick attribution. <see cref="SuppressActivityCapture"/> is the
/// per-thread opt-out for skipping <see cref="Activity.Current"/> reads.
/// </para>
/// </remarks>
internal static partial class TyphonEvent
{
    /// <summary>Innermost open Typhon span on this thread. Captured in the <c>Begin*</c> factories as the new span's <c>ParentSpanId</c>.</summary>
    [ThreadStatic] 
    private static ulong CurrentOpenSpanId;

    /// <summary>Per-thread opt-out flag for <see cref="Activity.Current"/> capture.</summary>
    [ThreadStatic]
    private static bool SuppressActivityCapture;

    /// <summary>Current scheduler tick number for this thread. Set by <c>DagScheduler</c>; read implicitly via the session's tick tracking.</summary>
    [ThreadStatic]
    internal static int CurrentTickNumber;

    // ═══════════════════════════════════════════════════════════════════════
    // Per-kind suppression deny-list
    // ═══════════════════════════════════════════════════════════════════════
    //
    // Indexed by (int)TraceEventKind (0..255). When an entry is true, the matching Begin*Event factory short-circuits and returns default(T),
    // the same way it does when the profiler is globally off. The check lives inside BeginPrologue, guarded by the ProfilerActive check — so
    // when the profiler is off, the JIT still dead-code-eliminates the entire prologue including this array access. When the profiler is on,
    // the cost is one predictable cache-hot load + branch per factory call (~1 ns).
    //
    // Defaults: the deny-list is reserved for truly extreme-frequency kinds (≥10⁵ events/sec on realistic workloads) where accidentally
    // enabling them via the JSON config would saturate the trace ring buffer in microseconds. Diagnostic-grade kinds — those that fire
    // at most a few hundred times per second (per-tick checkpoints, per-UoW state transitions, async I/O completions) — are gated solely
    // by their JSON category and do NOT live here. Operators who want them flip the parent category in typhon.telemetry.json.
    //
    // The original deny-list grouped everything page-cache-related "for consistency" with PageCacheFetch (the actual killer at millions/sec),
    // and everything WAL/UoW-related with WalFrame. That overshot — kinds like PageCacheFlushCompleted (≪1/sec) ended up unreachable from
    // config alone, forcing a C# UnsuppressKind call just to diagnose a slow flush. The 2026-04-30 re-tier removed those.
    //
    // Replaces the pre-#243 TelemetryConfig.PagedMMFSpanCacheMiss / PagedMMFSpanIOOnly flags, which were compile-time gates for the old
    // TyphonActivitySource.StartActivity call path. The typed-event profiler has a single coarse gate (ProfilerActive) plus this fine-grained
    // per-kind deny-list — more flexible, same zero-cost-when-off guarantee.

    private static readonly bool[] SuppressedKinds = new bool[256];

    static TyphonEvent()
    {
        // Page cache: only PageCacheFetch is truly extreme (every ChunkAccessor.GetPage in hot loops, easily millions/sec on read-heavy
        // workloads). The other 9 page-cache kinds (DiskRead/Write/Flush kickoffs + their async Completed peers + AllocatePage + Evicted
        // + Backpressure) all fire at the rate of disk operations or eviction events — orders of magnitude less. Those are gated solely
        // by Storage:PageCache:Enabled in the JSON config now.
        SuppressedKinds[(int)TraceEventKind.PageCacheFetch] = true;

        // Data plane: high-frequency MVCC and B+Tree leaves stay deny-listed even when Data:* flips on in JSON.
        SuppressedKinds[(int)TraceEventKind.DataMvccChainWalk] = true;
        SuppressedKinds[(int)TraceEventKind.DataIndexBTreeSearch] = true;
        SuppressedKinds[(int)TraceEventKind.DataIndexBTreeNodeCow] = true;

        // Extreme/high-frequency Query / ECS:View leaves.
        SuppressedKinds[(int)TraceEventKind.QueryExecuteIterate] = true;
        SuppressedKinds[(int)TraceEventKind.QueryExecuteFilter] = true;
        SuppressedKinds[(int)TraceEventKind.QueryExecutePagination] = true;
        SuppressedKinds[(int)TraceEventKind.EcsQueryMaskAnd] = true;
        SuppressedKinds[(int)TraceEventKind.EcsViewProcessEntry] = true;
        SuppressedKinds[(int)TraceEventKind.EcsViewProcessEntryOr] = true;

        // Durability: only WalFrame qualifies (one record per commit on a high-throughput workload = 10⁴+/sec). RecoveryRecord is startup-only;
        // UoW State/Deadline fire at the per-tick rate (~60-300/sec at 60 TPS) and are exactly what an operator wants to see when diagnosing
        // a slow UoW.Flush. Both came off the deny-list — they are gated by Durability:* in JSON like the rest.
        SuppressedKinds[(int)TraceEventKind.DurabilityWalFrame] = true;

        // High-frequency Subscription leaves.
        SuppressedKinds[(int)TraceEventKind.RuntimeSubscriptionSubscriber] = true;
        SuppressedKinds[(int)TraceEventKind.RuntimeSubscriptionDeltaSerialize] = true;
    }

    /// <summary>
    /// Mark a <see cref="TraceEventKind"/> as suppressed. Subsequent <c>Begin*</c> factory calls for that kind return <c>default</c> and emit
    /// no record. Existing in-flight scopes are unaffected — their Dispose still runs the PublishEvent path because they already hold a
    /// non-zero <c>SpanId</c>.
    /// </summary>
    /// <remarks>
    /// Thread-safe for concurrent readers (the hot path); not guaranteed ordered with concurrent writers. Typically called at profiler
    /// startup or from an admin/diagnostics endpoint. Plain byte-store, no <c>Interlocked</c> needed.
    /// </remarks>
    public static void SuppressKind(TraceEventKind kind) => SuppressedKinds[(int)kind] = true;

    /// <summary>Clear the suppression flag for a specific event kind. Inverse of <see cref="SuppressKind"/>.</summary>
    public static void UnsuppressKind(TraceEventKind kind) => SuppressedKinds[(int)kind] = false;

    /// <summary>Whether a specific event kind is currently in the deny-list.</summary>
    public static bool IsKindSuppressed(TraceEventKind kind) => SuppressedKinds[(int)kind];

    // ═══════════════════════════════════════════════════════════════════════
    // Prologue — shared by every Begin*Event factory
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Common prologue: check gate, check per-kind suppression, acquire slot, capture start timestamp, compute SpanId, set TLS, optionally
    /// read <see cref="Activity.Current"/>. Returns <c>false</c> if the span should be skipped (profiler off, kind suppressed, registry full)
    /// — callers return <c>default</c> in that case.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BeginPrologue(TraceEventKind kind, out int slotIdx, out long startTs, out ulong spanId, out ulong parentSpanId,
        out ulong previousSpanId, out ulong traceIdHi, out ulong traceIdLo)
    {
        slotIdx = -1;
        startTs = 0;
        spanId = 0;
        parentSpanId = 0;
        previousSpanId = 0;
        traceIdHi = 0;
        traceIdLo = 0;

        if (!TelemetryConfig.ProfilerActive)
        {
            return false;
        }

        // Per-kind suppression deny-list. Ordered AFTER the ProfilerActive check so the JIT still eliminates the entire prologue body (including
        // this array load) when the profiler is globally off.
        if (SuppressedKinds[(int)kind])
        {
            return false;
        }

        var idx = ThreadSlotRegistry.GetOrAssignSlot();
        if (idx < 0)
        {
            return false;
        }

        var slot = ThreadSlotRegistry.GetSlot(idx);
        startTs = Stopwatch.GetTimestamp();
        spanId = SpanIdGenerator.NextId(idx, slot);
        previousSpanId = CurrentOpenSpanId;
        parentSpanId = previousSpanId;

        if (slot.CaptureActivityContext && !SuppressActivityCapture)
        {
            var activity = Activity.Current;
            if (activity != null)
            {
                Span<byte> traceBuf = stackalloc byte[16];
                activity.TraceId.CopyTo(traceBuf);
                traceIdHi = MemoryMarshal.Read<ulong>(traceBuf);
                traceIdLo = MemoryMarshal.Read<ulong>(traceBuf[8..]);

                if (parentSpanId == 0)
                {
                    Span<byte> spanBuf = stackalloc byte[8];
                    activity.SpanId.CopyTo(spanBuf);
                    parentSpanId = MemoryMarshal.Read<ulong>(spanBuf);
                }
            }
        }

        CurrentOpenSpanId = spanId;
        slotIdx = idx;
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Publishing — shared by every ref-struct event's Dispose
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Chain-aware reservation. Tries the slot's current <see cref="ThreadSlot.ChainTail"/>; on overflow, attempts
    /// to acquire a spillover from <see cref="SpilloverRingPool"/>, link it onto the chain, and reserve there.
    /// Returns the ring the reservation landed on via <paramref name="reservedOn"/> so the caller can publish to
    /// the SAME ring (publishing to a different ring would corrupt SPSC ordering). When the slot has a null
    /// primary or the pool is exhausted, returns <c>false</c> without reserving — the per-kind drop counter on
    /// the overflowing tail has already been bumped via <see cref="TraceRecordRing.TryReserve(int, byte, out Span{byte})"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Capture-once discipline.</b> The producer reads <c>slot.ChainTail</c> exactly once per call. If the
    /// reservation succeeds on the first try, the producer publishes on that same captured ring. If the first try
    /// fails and the chain extends, the producer publishes on the new spillover, again the captured one. There is
    /// no path where TryReserve and Publish target different rings.
    /// </para>
    /// <para>
    /// <b>SPSC ordering on chain link.</b> The producer assigns <c>tail.SetNext(spill)</c> before <c>slot.ChainTail = spill</c>.
    /// On x64 TSO both stores are release-ordered. The consumer reads <c>head.Next</c> with <see cref="Volatile.Read"/>
    /// to defeat JIT hoisting. A consumer that observes <c>head.IsEmpty == true</c> with a stale-null <c>head.Next</c>
    /// simply doesn't advance this pass — next pass picks it up. Not a correctness bug, just a fraction of a millisecond
    /// of latency.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryReserveOnChain(ThreadSlot slot, int size, byte kind, out Span<byte> destination, out TraceRecordRing reservedOn)
    {
        var tail = slot.ChainTail;
        if (tail == null)
        {
            // Slot not claimed (Buffer null) — cannot reserve.
            destination = default;
            reservedOn = null;
            return false;
        }
        if (tail.TryReserve(size, kind, out destination))
        {
            reservedOn = tail;
            return true;
        }
        // Overflow on the current tail. Try to extend the chain with a fresh spillover from the pool.
        var spill = SpilloverRingPool.TryAcquire();
        if (spill == null)
        {
            // Pool exhausted (or not initialised) — drop. The TryReserve above already bumped the per-kind drop
            // counter on `tail`, so the breakdown stays accurate.
            reservedOn = null;
            return false;
        }
        // Publish the new ring into the chain, then advance ChainTail. SPSC: this method only ever runs on the
        // owning thread, so concurrent producers on the same slot are impossible.
        tail.SetNext(spill);
        slot.ChainTail = spill;
        if (spill.TryReserve(size, kind, out destination))
        {
            // Recovery succeeded — the failing TryReserve on `tail` above bumped its drop counters, but no data
            // was actually lost. Rescind that bump so the diagnostic counters reflect real loss only.
            tail.RescindLastDrop(kind);
            reservedOn = spill;
            return true;
        }
        // Should be unreachable — a fresh spillover is at least 64 KiB and the record fits within MaxRecordSize
        // (0xFFFE bytes). If it ever happens (e.g. someone configured a too-small spillover for a giant record),
        // the per-kind drop counter on the spillover has been bumped by the failing TryReserve.
        reservedOn = null;
        return false;
    }

    /// <summary>
    /// Publish a typed event to its owner slot's ring buffer and restore the parent scope's TLS open-span ID. Called from every typed event
    /// struct's <c>Dispose</c> method via a generic constraint that lets the JIT inline the full encode path for each concrete event type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>allows ref struct</c> constraint</b> (C# 13) permits <typeparamref name="T"/> to be a <c>ref struct</c> — which every Phase 1
    /// typed event is. Without it, the generic constraint would reject ref-struct instantiations. With it, the JIT specializes this method for
    /// each event type, inlining <see cref="ITraceEventEncoder.ComputeSize"/> and <see cref="ITraceEventEncoder.EncodeTo"/> at the call site.
    /// </para>
    /// <para>
    /// <b>Default-struct detection:</b> when <paramref name="spanId"/> is zero the event was returned from a short-circuit path
    /// (profiler disabled, registry full, suppressed). In that case the Dispose is a no-op — nothing was reserved, nothing to publish, no TLS to
    /// restore.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PublishEvent<T>(ref T evt, byte threadSlot, ulong previousSpanId, ulong spanId) where T : struct, ITraceEventEncoder, allows ref struct
    {
        // `ref T` rather than `in T` is deliberate: ITraceEventEncoder's ComputeSize/EncodeTo aren't (and can't be) marked `readonly` on
        // the interface, so calling them through an `in` parameter would force a defensive struct copy at every call site. Taking `ref`
        // gives the JIT a mutable alias and inlines the calls with zero copies.
        if (spanId == 0)
        {
            return;  // default struct — Dispose of a skipped span
        }

        var endTs = Stopwatch.GetTimestamp();
        var size = evt.ComputeSize();
        var slot = ThreadSlotRegistry.GetSlot(threadSlot);
        // Chain-aware reserve: try the current ChainTail (primary or latest spillover), and on overflow extend the
        // chain with a fresh spillover from the pool. The reservation MUST land on the same ring as Publish — we
        // capture the ring used (`reservedOn`) and publish to that exact instance.
        if (TryReserveOnChain(slot, size, T.Kind, out var dst, out var reservedOn))
        {
            evt.EncodeTo(dst, endTs, out _);
            reservedOn.Publish();
        }
        CurrentOpenSpanId = previousSpanId;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Header construction helper — used by every Begin* factory below.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a populated <see cref="TraceSpanHeader"/> from the seven prologue out-params returned by <see cref="BeginPrologue"/>. Inlined into
    /// every Begin* factory so the produced code is identical to the pre-#294 hand-written field assignments.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TraceSpanHeader MakeHeader(int slotIdx, long startTs, ulong spanId, ulong parentSpanId, ulong previousSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort siteId)
        => new()
        {
            ThreadSlot = (byte)slotIdx,
            StartTimestamp = startTs,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            PreviousSpanId = previousSpanId,
            TraceIdHi = traceIdHi,
            TraceIdLo = traceIdLo,
            SourceLocationId = siteId,
        };

    // ═══════════════════════════════════════════════════════════════════════
    // Scheduler-internal fast paths — called from DagScheduler wrappers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Page-cache-internal: emit a zero-duration <see cref="TraceEventKind.PageEvicted"/> marker span recording that
    /// <paramref name="evictedFilePageIndex"/> was displaced from the cache. Parents under the currently-open span via
    /// <see cref="CurrentOpenSpanId"/> TLS (typically the enclosing <see cref="TraceEventKind.PageCacheAllocatePage"/> scope), so the viewer
    /// renders it nested inside the AllocatePage bar. Reuses <see cref="PageCacheEventCodec"/>'s wire shape — no new codec.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="BeginPrologue"/> so it honours both the global <see cref="TelemetryConfig.ProfilerActive"/> gate and the
    /// per-kind deny-list (<see cref="TraceEventKind.PageEvicted"/> is suppressed by default). When suppressed the whole body dead-code
    /// eliminates in Tier 1 JIT, just like the <c>Begin*</c> factories.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitPageEvicted(int evictedFilePageIndex, byte dirtyBit = 0)
    {
        if (!BeginPrologue(TraceEventKind.PageEvicted, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        // Phase 5 wire-additive: always set OptDirtyBit so the trailing 1-byte dirty flag is encoded.
        const byte optMask = PageCacheEventCodec.OptDirtyBit;
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageEvicted, traceIdHi != 0 || traceIdLo != 0, optMask);
        if (slot.Buffer.TryReserve(size, out var dst))
        {
            // Zero-duration marker: end == start. PageCacheEventCodec writes the duration as (end - start), so duration lands at 0.
            PageCacheEventCodec.Encode(dst, startTs, TraceEventKind.PageEvicted, (byte)slotIdx, startTs,
                spanId, parentSpanId, traceIdHi, traceIdLo, evictedFilePageIndex, 0, optMask, out _, dirtyBit);
            slot.Buffer.Publish();
        }

        // Restore TLS immediately — this is a zero-duration marker, not a nestable scope.
        CurrentOpenSpanId = previousSpanId;
    }

    /// <summary>
    /// Page-cache-internal: emit a <see cref="TraceEventKind.PageCacheDiskReadCompleted"/> record from a thread-pool completion thread,
    /// carrying the full async-tail duration as <c>completionTimestamp - beginTimestamp</c>. The <paramref name="spanId"/> matches the
    /// originating <see cref="TraceEventKind.PageCacheDiskRead"/> span, giving the viewer a zero-cost correlator.
    /// </summary>
    /// <remarks>
    /// <b>Thread safety:</b> runs on whichever thread completes the <c>ReadAsync</c>, not the thread that began the span. Claims that thread's
    /// own slot via <see cref="ThreadSlotRegistry.GetOrAssignSlot"/> and publishes to its SPSC ring — no cross-thread writes. Does NOT touch
    /// <see cref="CurrentOpenSpanId"/> (different thread's TLS has nothing to do with this record).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitPageCacheDiskReadCompleted(ulong spanId, long beginTimestamp, int filePageIndex, long completionTimestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        if (SuppressedKinds[(int)TraceEventKind.PageCacheDiskReadCompleted])
        {
            return;
        }
        // Phase 5: producer-side duration threshold gate (default 1 ms).
        if (IsBelowCompletionThreshold(beginTimestamp, completionTimestamp))
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheDiskReadCompleted, false, 0);
        if (!slot.Buffer.TryReserve(size, out var dst))
        {
            return;
        }

        PageCacheEventCodec.Encode(dst, completionTimestamp, TraceEventKind.PageCacheDiskReadCompleted, (byte)slotIdx, beginTimestamp,
            spanId, 0, 0, 0, filePageIndex, 0, 0, out _);
        slot.Buffer.Publish();
    }

    /// <summary>
    /// Page-cache-internal: emit a <see cref="TraceEventKind.PageCacheDiskWriteCompleted"/> record from a thread-pool completion thread.
    /// Same correlation pattern as <see cref="EmitPageCacheDiskReadCompleted"/> — carries the originating DiskWrite span's <paramref name="spanId"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitPageCacheDiskWriteCompleted(ulong spanId, long beginTimestamp, int filePageIndex, long completionTimestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        if (SuppressedKinds[(int)TraceEventKind.PageCacheDiskWriteCompleted])
        {
            return;
        }
        // Phase 5: producer-side duration threshold gate (default 1 ms).
        if (IsBelowCompletionThreshold(beginTimestamp, completionTimestamp))
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheDiskWriteCompleted, false, 0);
        if (!slot.Buffer.TryReserve(size, out var dst))
        {
            return;
        }

        PageCacheEventCodec.Encode(dst, completionTimestamp, TraceEventKind.PageCacheDiskWriteCompleted, (byte)slotIdx, beginTimestamp,
            spanId, 0, 0, 0, filePageIndex, 0, 0, out _);
        slot.Buffer.Publish();
    }

    /// <summary>
    /// Page-cache-internal: emit a <see cref="TraceEventKind.PageCacheFlushCompleted"/> record from the <c>Task.WhenAll(...).ContinueWith</c>
    /// continuation in <c>SavePages</c>. The record's duration covers the full flush tail (all WriteAsync completions + fsync).
    /// </summary>
    /// <remarks>
    /// Following Flush convention, <paramref name="pageCount"/> is stored in the primary <c>filePageIndex</c> slot of the PageCache codec —
    /// matches how <see cref="BeginPageCacheFlush"/> encodes its own record, so the decoder path is identical.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitPageCacheFlushCompleted(ulong spanId, long beginTimestamp, int pageCount, long completionTimestamp)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        if (SuppressedKinds[(int)TraceEventKind.PageCacheFlushCompleted])
        {
            return;
        }
        // Phase 5: producer-side duration threshold gate (default 1 ms).
        if (IsBelowCompletionThreshold(beginTimestamp, completionTimestamp))
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheFlushCompleted, false, 0);
        if (!slot.Buffer.TryReserve(size, out var dst))
        {
            return;
        }

        // Flush convention: pageCount lives in the primary "filePageIndex" slot; the optional PageCount slot is unused (optMask=0).
        PageCacheEventCodec.Encode(dst, completionTimestamp, TraceEventKind.PageCacheFlushCompleted, (byte)slotIdx, beginTimestamp,
            spanId, 0, 0, 0, pageCount, 0, 0, out _);
        slot.Buffer.Publish();
    }

    // EmitSchedulerChunk and EmitSchedulerSystemArchetype are now generator-emitted via the
    // [TraceEvent(..., ExternalTimestamps = true)] declarations on SchedulerChunkEvent / SchedulerSystemArchetypeEvent.
    // Wire format unchanged; signature reordered to (startTimestamp, endTimestamp, ...payloadParams).


    // EmitTickStart / TickEnd / PhaseStart / PhaseEnd / SystemReady are generator-emitted via the
    // [TraceEvent(Shape=Instant)] declarations in InstantEvents.cs. Per-kind drop counters were retired alongside
    // the legacy hand-written body — the same data is available via the per-kind ring drop counters
    // (TraceRecordRing tracks drops per kind via TryReserve(size, kind, ...)).


    // ═══════════════════════════════════════════════════════════════════════
    // GC-ingestion-internal emit helpers (called only by GcIngestionThread —
    // slot is owned by the caller, not looked up per call)
    // ═══════════════════════════════════════════════════════════════════════

    // GC-ingestion-internal: emit a GcStart instant record. The caller's slot must be the ingestion thread's own claimed slot —
    // preserving the per-slot SPSC invariant without any locking on the ring itself. Does not participate in the CurrentOpenSpanId
    // parent-linking scheme — GC events are process-level and independent of any ambient Typhon span. Does not read Activity.Current either.
    // EmitGcStart / EmitGcEnd are generator-emitted via [TraceEvent(Shape=Instant, ExternalSlot=true)]
    // declarations in InstantEvents.cs. Caller passes the GC-ingestion thread's slot explicitly.

    // EmitGcSuspension is generator-emitted via [TraceEvent(GcSuspension, ExternalTimestamps=true, ExternalSlot=true)] in GcSuspensionEvent.cs.

    /// <summary>
    /// Emit a <see cref="TraceEventKind.ThreadInfo"/> instant record carrying the slot's managed thread ID and UTF-8 name. Called once by
    /// <c>ThreadSlotRegistry.AssignClaim</c> from the claiming thread immediately after the claim completes, so the record lands in that
    /// thread's own slot (single-producer invariant preserved). The viewer uses these records to label lanes with real thread names.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitThreadInfo(byte slot, int managedThreadId, string name, ThreadKind kind)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }
        var ring = ThreadSlotRegistry.GetSlot(slot).Buffer;
        if (ring == null)
        {
            return;
        }

        // Encode the name to a stack buffer sized for typical thread-name lengths (e.g., "TyphonProfilerConsumer" = 22 B). If the name
        // somehow exceeds 256 B, fall back to ArrayPool — still no GC pressure on the hot path that matters (this is slot claim, not span).
        ReadOnlySpan<char> nameSpan = name ?? string.Empty;
        Span<byte> nameBuf = stackalloc byte[256];
        int byteCount;
        if (System.Text.Encoding.UTF8.GetByteCount(nameSpan) <= nameBuf.Length)
        {
            byteCount = System.Text.Encoding.UTF8.GetBytes(nameSpan, nameBuf);
            EmitThreadInfoCore(ring, slot, managedThreadId, nameBuf[..byteCount], kind);
        }
        else
        {
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(System.Text.Encoding.UTF8.GetMaxByteCount(nameSpan.Length));
            try
            {
                byteCount = System.Text.Encoding.UTF8.GetBytes(nameSpan, rented);
                EmitThreadInfoCore(ring, slot, managedThreadId, rented.AsSpan(0, byteCount), kind);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitThreadInfoCore(TraceRecordRing ring, byte slot, int managedThreadId, ReadOnlySpan<byte> nameUtf8, ThreadKind kind)
    {
        var size = ThreadInfoEventCodec.ComputeSize(nameUtf8.Length);
        if (!ring.TryReserve(size, out var dst))
        {
            return;
        }
        ThreadInfoEventCodec.WriteThreadInfo(dst, slot, Stopwatch.GetTimestamp(), managedThreadId, nameUtf8, kind, out _);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Memory allocation / gauge snapshot — called from MemoryAllocator and DagScheduler
    // ═══════════════════════════════════════════════════════════════════════

    // Emit a MemoryAllocEvent instant record. Called from MemoryAllocator.AllocatePinned / AllocateArray (direction=Alloc) and
    // MemoryAllocator.Remove (direction=Free). Gated on TelemetryConfig.ProfilerMemoryAllocationsActive (separate knob from the master
    // profiler gate) so operators can run the profiler for span tracing without paying per-alloc event cost. The first line's
    // `if (!active) return` dead-code-eliminates in Tier 1 JIT when the flag is off. Runs on whichever thread allocates/frees —
    // claims that thread's own ring slot, preserving the per-slot SPSC invariant.
    // EmitMemoryAlloc is generator-emitted via [TraceEvent(TraceEventKind.MemoryAllocEvent, Shape=Instant)] in InstantEvents.cs.

    /// <summary>
    /// Emit a <see cref="TraceEventKind.PerTickSnapshot"/> record carrying the caller-collected gauge values. Intended single caller:
    /// <c>DagScheduler</c> at end-of-tick, running on the scheduler thread.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="TelemetryConfig.ProfilerGaugesActive"/>. The caller is expected to prepare <paramref name="values"/> as a
    /// <c>stackalloc</c> buffer — no allocation on the hot path. Snapshot size is variable; the codec computes total wire size from the
    /// value list before claiming ring space.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitPerTickSnapshot(uint tickNumber, long timestamp, uint flags, ReadOnlySpan<GaugeValue> values)
    {
        if (!TelemetryConfig.ProfilerGaugesActive)
        {
            Interlocked.Increment(ref SSnapshotSkippedGaugesInactive);
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            Interlocked.Increment(ref SSnapshotSkippedNoSlot);
            return;
        }

        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null)
        {
            Interlocked.Increment(ref SSnapshotSkippedNullRing);
            return;
        }

        var size = PerTickSnapshotEventCodec.ComputeSize(values);
        if (!ring.TryReserve(size, out var dst))
        {
            Interlocked.Increment(ref SSnapshotSkippedRingFull);
            return;
        }

        PerTickSnapshotEventCodec.WritePerTickSnapshot(dst, (byte)slotIdx, timestamp, tickNumber, flags, values, out _);
        ring.Publish();
        Interlocked.Increment(ref SSnapshotPublished);
    }

    private static long SSnapshotPublished;
    private static long SSnapshotSkippedGaugesInactive;
    private static long SSnapshotSkippedNoSlot;
    private static long SSnapshotSkippedNullRing;
    private static long SSnapshotSkippedRingFull;

    public static long SnapshotPublished => SSnapshotPublished;
    public static long SnapshotSkippedGaugesInactive => SSnapshotSkippedGaugesInactive;
    public static long SnapshotSkippedNoSlot => SSnapshotSkippedNoSlot;
    public static long SnapshotSkippedNullRing => SSnapshotSkippedNullRing;
    public static long SnapshotSkippedRingFull => SSnapshotSkippedRingFull;

    // Scheduler-internal: emit a SystemSkipped marker. Phase 4 (#282) extended the payload (wire-additive):
    // wouldBeChunkCount and successorsUnblocked are new fields.
    // EmitSystemSkipped is generator-emitted via [TraceEvent(TraceEventKind.SystemSkipped, Shape=Instant)] in InstantEvents.cs.
    // Signature: EmitSystemSkipped(long timestamp, ushort systemIdx, byte skipReason, ushort wouldBeChunkCount, ushort successorsUnblocked).

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrency tracing (Phase 2, #280) — instant Emit methods.
    //
    // All gate on a Tier-2 leaf flag from the TelemetryConfig.Concurrency*
    // tree. Every method follows the EmitMemoryAlloc shape:
    //   1. gate check (JIT-eliminated when off)
    //   2. acquire ring slot
    //   3. reserve ring space
    //   4. encode via per-subtree codec
    //   5. publish.
    //
    // Cost when Tier-2 disabled (proven by Phase 1 microbench): 0 ns.
    // Cost when enabled, ring available: ~5 ns per emission.
    // ═══════════════════════════════════════════════════════════════════════

    // ── Concurrency (kinds 90-116): all generator-emitted via [TraceEvent(Shape=Instant)] in InstantEvents.cs ──
    //   AccessControl: SharedAcquire/Release, ExclusiveAcquire/Release, Promotion, Contention
    //   AccessControlSmall: SharedAcquire/Release, ExclusiveAcquire/Release, Contention
    //   ResourceAccessControl: Accessing, Modify, Destroy, ModifyPromotion, Contention
    //   Epoch: ScopeEnter, ScopeExit, Advance, Refresh, SlotClaim, SlotReclaim
    //   AdaptiveWaiter: YieldOrSleep
    //   OlcLatch: WriteLockAttempt, WriteUnlock, MarkObsolete, ValidationFail


    // ═══════════════════════════════════════════════════════════════════════
    // Thread-local control
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Opt the current thread out of <see cref="Activity.Current"/> capture.</summary>
    public static void SuppressActivityContextOnThisThread() => SuppressActivityCapture = true;

    /// <summary>Re-enable <see cref="Activity.Current"/> capture for the current thread.</summary>
    public static void RestoreActivityContextOnThisThread() => SuppressActivityCapture = false;

    /// <summary>Set this thread's current scheduler tick number. Called by <c>DagScheduler</c> at tick entry.</summary>
    public static void SetCurrentTickNumber(int tickNumber) => CurrentTickNumber = tickNumber;

    // ═══════════════════════════════════════════════════════════════════════
    // Diagnostics
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Total records dropped across all slots due to ring-buffer overflow — primary AND any in-flight spillovers in each slot's chain.</summary>
    public static long TotalDroppedEvents
    {
        get
        {
            long total = 0;
            var hwm = ThreadSlotRegistry.HighWaterMark;
            for (var i = 0; i < hwm; i++)
            {
                var slot = ThreadSlotRegistry.GetSlot(i);
                var ring = slot.ChainHead ?? slot.Buffer;
                while (ring != null)
                {
                    total += ring.DroppedEvents;
                    ring = ring.Next;
                }
            }
            return total;
        }
    }

    /// <summary>
    /// Drop counts broken down by <see cref="TraceEventKind"/>, summed across all per-thread rings. Caller-visible
    /// only after a quiesced state (e.g. shutdown post-<see cref="TyphonProfiler.Stop"/>) since the per-ring
    /// counters are updated lock-free SPSC by the producer threads.
    /// </summary>
    /// <remarks>
    /// Currently only span events (those routed through <see cref="PublishEvent{T}"/>) are tracked per-kind — they
    /// pass <c>T.Kind</c> to <see cref="TraceRecordRing.TryReserve(int, byte, out Span{byte})"/>. Instant events
    /// (TickStart/TickEnd/Phase/GC/Memory/...) emitted directly via <c>EmitX</c> still drop into the aggregate
    /// <see cref="TotalDroppedEvents"/> count but aren't broken down here yet. The sum of all values returned can
    /// therefore be lower than <see cref="TotalDroppedEvents"/>; the gap is "instants dropped".
    /// </remarks>
    public static IReadOnlyDictionary<TraceEventKind, long> DroppedEventsByKind
    {
        get
        {
            var totals = new long[256];
            var hwm = ThreadSlotRegistry.HighWaterMark;
            for (var i = 0; i < hwm; i++)
            {
                // Walk the slot's chain — drops can land on the primary (when pool was exhausted on extension) OR
                // on a spillover (when a spillover's own capacity overflowed before we could chain again, rare).
                // Aggregating only the primary would miss the latter.
                var slot = ThreadSlotRegistry.GetSlot(i);
                var ring = slot.ChainHead ?? slot.Buffer;
                while (ring != null)
                {
                    for (var k = 0; k < 256; k++)
                    {
                        totals[k] += ring.DroppedEventsForKind((byte)k);
                    }
                    ring = ring.Next;
                }
            }
            var result = new Dictionary<TraceEventKind, long>();
            for (var k = 0; k < 256; k++)
            {
                if (totals[k] > 0)
                {
                    result[(TraceEventKind)k] = totals[k];
                }
            }
            return result;
        }
    }

    /// <summary>Number of slots currently claimed (Active or Retiring).</summary>
    public static int ActiveSlotCount => ThreadSlotRegistry.ActiveSlotCount;

    // ── Scheduler:Metronome (kind 241) — issue #289 follow-up ───────────────

    /// <summary>
    /// Emit <see cref="TraceEventKind.SchedulerMetronomeWait"/> — span covering the timer thread's
    /// inter-tick wait (Sleep→Yield→Spin). Called by <c>HighResolutionTimerServiceBase</c>'s
    /// <c>OnWaitComplete</c> hook after each wait completes (via <see cref="DagScheduler"/>'s override).
    /// </summary>
    /// <remarks>
    /// Bypasses <see cref="BeginPrologue"/> because we control the start timestamp (it was captured
    /// at wait-loop entry, not at call-time), but mints a real <see cref="SpanIdGenerator"/> id so
    /// each emitted span is uniquely addressable. <c>parentSpanId</c> is left at 0 — the wait has
    /// no lexically enclosing span on the timer thread.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitSchedulerMetronomeWait(long startTimestamp, long endTimestamp, long scheduledTimestamp, byte multiplier, byte intentClass, 
        byte phaseFlags)
    {
        if (!TelemetryConfig.SchedulerMetronomeWaitActive)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var ring = slot.Buffer;
        if (ring == null)
        {
            return;
        }

        var size = SchedulerMetronomeEventCodec.ComputeSizeWait(hasTraceContext: false);
        if (!ring.TryReserve(size, out var dst))
        {
            return;
        }

        var spanId = SpanIdGenerator.NextId(slotIdx, slot);
        SchedulerMetronomeEventCodec.EncodeWait(dst, (byte)slotIdx, startTimestamp, endTimestamp,
            spanId, parentSpanId: 0,
            scheduledTimestamp, multiplier, intentClass, phaseFlags, out _);
        ring.Publish();
    }

    // ── Scheduler:Graph (kinds 159-160) ─────────────────────────────────────

    // ── Runtime:Phase + Transaction + Subscription (kinds 161-164) ──────────
    // ── TLS-based cross-method Tx Lifecycle bracketing ────────────────────────────────────────
    // Used by TyphonRuntime.OnSystemStart/EndInternal: those run on the same worker thread sequentially
    // for a given system, but in different methods — so the ref-struct factory doesn't fit. Instead we
    // stash start state in TLS at OnSystemStart and consume it at OnSystemEnd.

    [ThreadStatic] private static long _txLifecycleStartTs;
    [ThreadStatic] private static ulong _txLifecycleSpanId;
    [ThreadStatic] private static ulong _txLifecyclePreviousSpanId;
    [ThreadStatic] private static ulong _txLifecycleParentSpanId;
    [ThreadStatic] private static ulong _txLifecycleTraceIdHi;
    [ThreadStatic] private static ulong _txLifecycleTraceIdLo;
    [ThreadStatic] private static int _txLifecycleSlotIdx;
    [ThreadStatic] private static ushort _txLifecycleSysIdx;

    /// <summary>Cross-method begin for <see cref="TraceEventKind.RuntimeTransactionLifecycle"/>. Pair with <see cref="EmitRuntimeTransactionLifecycleEnd"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginRuntimeTransactionLifecycleTls(ushort sysIdx)
    {
        if (!TelemetryConfig.RuntimeTransactionLifecycleActive)
        {
            _txLifecycleSpanId = 0;
            return;
        }
        if (!BeginPrologue(TraceEventKind.RuntimeTransactionLifecycle, out var slotIdx, out var startTs, out var spanId, out var parentSpanId,
                           out var previousSpanId, out var traceIdHi, out var traceIdLo))
        {
            _txLifecycleSpanId = 0;
            return;
        }
        _txLifecycleStartTs = startTs;
        _txLifecycleSpanId = spanId;
        _txLifecyclePreviousSpanId = previousSpanId;
        _txLifecycleParentSpanId = parentSpanId;
        _txLifecycleTraceIdHi = traceIdHi;
        _txLifecycleTraceIdLo = traceIdLo;
        _txLifecycleSlotIdx = slotIdx;
        _txLifecycleSysIdx = sysIdx;
    }

    /// <summary>Cross-method end for <see cref="TraceEventKind.RuntimeTransactionLifecycle"/>. Reads TLS state set by <see cref="BeginRuntimeTransactionLifecycleTls"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitRuntimeTransactionLifecycleEnd(bool success)
    {
        var spanId = _txLifecycleSpanId;
        if (spanId == 0)
        {
            return;
        }
        var endTs = Stopwatch.GetTimestamp();
        var startTs = _txLifecycleStartTs;
        var slotIdx = _txLifecycleSlotIdx;
        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var ring = slot.Buffer;
        var hasTC = _txLifecycleTraceIdHi != 0 || _txLifecycleTraceIdLo != 0;
        var size = RuntimeEventCodec.ComputeSizeLifecycle(hasTC);
        if (ring != null && ring.TryReserve(size, out var dst))
        {
            var durationTicks = endTs - startTs;
            var txDurUs = (uint)Math.Min((durationTicks * 1_000_000L) / Stopwatch.Frequency, uint.MaxValue);
            RuntimeEventCodec.EncodeLifecycle(dst, endTs, (byte)slotIdx, startTs,
                spanId, _txLifecycleParentSpanId, _txLifecycleTraceIdHi, _txLifecycleTraceIdLo,
                _txLifecycleSysIdx, txDurUs, success ? (byte)1 : (byte)0, out _);
            ring.Publish();
        }
        CurrentOpenSpanId = _txLifecyclePreviousSpanId;
        _txLifecycleSpanId = 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 5 — Storage & Memory factories
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns <c>true</c> when the configured Phase 5 completion threshold (kinds 56/57/58) is positive AND the elapsed
    /// duration falls below it. Producer-side gate — keeps short-IO traffic out of the ring without changing wire format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBelowCompletionThreshold(long beginTimestamp, long completionTimestamp)
    {
        var thresholdMs = TelemetryConfig.StoragePageCacheCompletionThresholdMs;
        if (thresholdMs <= 0)
        {
            return false;
        }
        var durationMs = (completionTimestamp - beginTimestamp) * 1000L / Stopwatch.Frequency;
        return durationMs < thresholdMs;
    }
    
    // ═══════════════════════════════════════════════════════════════════════
    // Phase 8 — Durability factories (WAL / Checkpoint / Recovery / UoW)
    // ═══════════════════════════════════════════════════════════════════════

    // ── Scheduler:Queue (kind 244 — #311) ───────────────────────────────────

    /// <summary>
    /// Emit <see cref="TraceEventKind.QueueTickEnd"/> — per-(tick, queue) rollup at end-of-tick. Captures the queue's
    /// tick-local accumulators (peak depth, end-of-tick depth, overflow count, produced/consumed counts) for the
    /// Workbench Data API <c>queue/&lt;name&gt;</c> tracks. Called from <c>DagScheduler.OnTickEnd</c> for each active
    /// event queue; gated by <see cref="TelemetryConfig.SchedulerQueueTickEndActive"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitQueueTickEnd(uint tickNumber, ushort queueId, uint peakDepth, uint endOfTickDepth, uint overflowCount, uint produced, uint consumed)
    {
        if (!TelemetryConfig.SchedulerQueueTickEndActive)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var ring = ThreadSlotRegistry.GetSlot(slotIdx).Buffer;
        if (ring == null || !ring.TryReserve(QueueTickEndCodec.Size, out var dst))
        {
            return;
        }

        QueueTickEndCodec.Write(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), tickNumber, queueId, peakDepth, endOfTickDepth, overflowCount, produced, consumed);
        ring.Publish();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Query Definition Export (v9, #342)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Emit a one-shot <see cref="TraceEventKind.QueryDefinitionDescribe"/> record describing a View or EcsQuery definition. Internally deduplicates against
    /// <see cref="QueryDefinitionDescribeTracker"/> — the first call per (kind, localId) per profiling session emits, subsequent calls are no-ops. Gated by
    /// <see cref="TelemetryConfig.QueryActive"/> for hot-path elimination when the profiler's query category is off.
    /// </summary>
    /// <remarks>
    /// Called from <c>PlanBuilder.BuildPlan</c> when the caller (View / EcsQuery) supplies its identity. Skipping the call (passing kind/localId == 0) is valid
    /// for ad-hoc engine-internal queries with no user-facing identity.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitQueryDefinitionDescribe(byte kind, uint localId, ushort targetComponentType, short primaryIndexFieldIdx, short sortFieldIdx,
        byte sortDescending, ushort definitionSourceFileId, int definitionSourceLine, ushort definitionSourceMethodId,
        ReadOnlySpan<byte> evaluatorBlob, ReadOnlySpan<byte> fieldDependenciesBlob)
    {
        if (!TelemetryConfig.QueryActive)
        {
            return;
        }
        if (SuppressedKinds[(int)TraceEventKind.QueryDefinitionDescribe])
        {
            return;
        }

        // Dedup — first time per (kind, localId) we describe; subsequent emits skip.
        if (!QueryDefinitionDescribeTracker.TryMarkAndCheck(kind, localId))
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            // No slot available — undo the mark so the next caller can retry, otherwise the
            // descriptor would be permanently silenced for this identity in the session.
            QueryDefinitionDescribeTracker.Unmark(kind, localId);
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = QueryDefinitionDescribeEventCodec.ComputeSize(
            evaluatorBlob.Length / QueryDefinitionDescribeEventCodec.EvaluatorEntrySize,
            fieldDependenciesBlob.Length / QueryDefinitionDescribeEventCodec.FieldDependencyEntrySize);
        if (!TryReserveOnChain(slot, size, (byte)TraceEventKind.QueryDefinitionDescribe, out var dst, out var ring))
        {
            // Ring saturated — undo the mark; subsequent refresh will retry.
            QueryDefinitionDescribeTracker.Unmark(kind, localId);
            return;
        }

        QueryDefinitionDescribeEventCodec.Write(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), kind, localId, targetComponentType,
            primaryIndexFieldIdx, sortFieldIdx, sortDescending, definitionSourceFileId, definitionSourceLine, definitionSourceMethodId,
            evaluatorBlob, fieldDependenciesBlob, out _);
        ring.Publish();
    }

    /// <summary>
    /// Emit a <see cref="TraceEventKind.QueryArgs"/> record carrying the widened threshold constants for a single query execution. Should be called immediately
    /// after <see cref="BeginQueryPlan"/> when the plan has at least one evaluator; callers should skip the call when <c>evaluatorCount == 0</c> for size economy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitQueryArgs(ReadOnlySpan<byte> thresholdsBlob)
    {
        if (!TelemetryConfig.QueryActive)
        {
            return;
        }
        if (SuppressedKinds[(int)TraceEventKind.QueryArgs])
        {
            return;
        }
        if (thresholdsBlob.Length == 0)
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }

        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var size = QueryArgsEventCodec.ComputeSize(thresholdsBlob.Length / QueryArgsEventCodec.ThresholdSize);
        if (!TryReserveOnChain(slot, size, (byte)TraceEventKind.QueryArgs, out var dst, out var ring))
        {
            return;
        }

        QueryArgsEventCodec.Write(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), thresholdsBlob, out _);
        ring.Publish();
    }

    /// <summary>
    /// Emit a <see cref="TraceEventKind.QueryPlan"/> span with caller-supplied start/end timestamps. Used by the runtime to record one execution per system tick
    /// for system-input views — these views never go through the <c>PlanBuilder.BuildPlan</c> path at consumption time (pull-mode views) yet still need a per-tick
    /// span so the Workbench Execution Inspector can drill in. The (kind, localId) pair attaches the span to the view's catalog row;
    /// the <see cref="QueryDefinitionDescribeTracker"/> already emitted the descriptor once via <see cref="ViewBase.EmitDescriptorIfNeeded"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitQueryPlanExternal(long startTimestamp, long endTimestamp, byte evaluatorCount, ushort indexFieldIdx, long rangeMin, long rangeMax,
        byte queryInstanceKind, uint queryInstanceLocalId, ushort executionSourceFileId, int executionSourceLine, ushort executionSourceMethodId, ushort ownerSystemIdx)
    {
        if (!TelemetryConfig.QueryActive)
        {
            return;
        }
        if (SuppressedKinds[(int)TraceEventKind.QueryPlan])
        {
            return;
        }

        var slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        if (slotIdx < 0)
        {
            return;
        }
        var slot = ThreadSlotRegistry.GetSlot(slotIdx);
        var spanId = SpanIdGenerator.NextId(slotIdx, slot);
        // Parent linking — fall back to whatever Typhon span is open on this thread (typically the System span);
        // when nothing is open (multi-threaded mode worker threads), parentSpanId = 0 puts the QueryPlan at the
        // lane root. Round-trip from a clicked scheduler chunk to the matching execution then relies on the
        // OwnerSystemIdx + tickNumber pair instead of parent-span linkage.
        var parentSpanId = CurrentOpenSpanId;

        var evt = new QueryPlanEvent
        {
            Header = new TraceSpanHeader
            {
                ThreadSlot = (byte)slotIdx,
                StartTimestamp = startTimestamp,
                SpanId = spanId,
                ParentSpanId = parentSpanId,
            },
            EvaluatorCount = evaluatorCount,
            IndexFieldIdx = indexFieldIdx,
            RangeMin = rangeMin,
            RangeMax = rangeMax,
            QueryInstanceKind = queryInstanceKind,
            QueryInstanceLocalId = queryInstanceLocalId,
            ExecutionSourceFileId = executionSourceFileId,
            ExecutionSourceLine = executionSourceLine,
            ExecutionSourceMethodId = executionSourceMethodId,
            OwnerSystemIdx = ownerSystemIdx,
        };

        var size = evt.ComputeSize();
        if (!TryReserveOnChain(slot, size, (byte)TraceEventKind.QueryPlan, out var dst, out var ring))
        {
            return;
        }
        evt.EncodeTo(dst, endTimestamp, out _);
        ring.Publish();
    }
}
