using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Profiler;

/// <summary>
/// Incremental, instance-based variant of <see cref="TraceFileCacheBuilder"/>. The replay path constructs an instance,
/// pumps the trace file's record bytes through <see cref="FeedRawRecords"/>, and disposes (which writes the trailer when the sink
/// supports it). The live path constructs an instance from the engine's Init-frame metadata, pumps each Block frame's
/// decompressed records through <see cref="FeedRawRecords"/>, calls <see cref="FlushCurrentChunk"/> on a 200 ms timer for
/// in-flight chunk visibility, and disposes when the engine sends Shutdown.
/// </summary>
/// <remarks>
/// <para>
/// Behaviorally identical to <see cref="TraceFileCacheBuilder"/> for replay traces — <see cref="Build"/> is the static façade
/// that pumps a file through an instance and disposes. Parity tests assert byte-identical sidecar output across the old
/// static builder, the new static façade, and arbitrary record-span splits driven manually.
/// </para>
/// <para>
/// All record-walking state is held in instance fields (formerly method-locals). The <see cref="FeedRawRecords"/> entry point
/// expects raw record bytes back-to-back exactly matching the inner-loop format consumed by <see cref="TraceFileCacheBuilder"/>:
/// each record begins with a u16 size prefix followed by a u8 kind byte. Multiple records may be passed in a single span and
/// are walked sequentially. Partial records ARE NOT supported across calls — the caller must pass complete records (the engine's
/// block frames already align on record boundaries).
/// </para>
/// </remarks>
public sealed class IncrementalCacheBuilder : IDisposable
{
    private const int CommonHeaderSize = 12;
    private const int SpanHeaderExtSize = 25;
    private const int TraceContextSize = 16;

    /// <summary>
    /// Cap on the pre-tick buffer's size. Pre-tick events are records that arrive before the first <c>TickStart</c>
    /// (engine-startup memory events, GC suspensions, ThreadInfo records). Past this cap, additional pre-tick events are
    /// dropped and counted in <see cref="PreTickDroppedBytes"/>.
    /// </summary>
    public const int PreTickBufferCap = 16 * 1024 * 1024;

    private readonly ICacheChunkSink _sink;
    private readonly bool _ownsSink;
    private readonly double _ticksPerUs;
    private readonly ProfilerHeader _header;
    private readonly byte[] _fingerprint;
    private readonly IReadOnlyDictionary<int, string> _spanNames;

    // Tick + chunk accumulation state (formerly method-locals in TraceFileCacheBuilder.Build).
    private readonly List<TickSummary> _tickSummaries = new(capacity: 4096);
    private readonly Dictionary<int, (uint InvocationCount, double TotalDurationUs)> _systemAggregates = new();
    private readonly List<ChunkManifestEntry> _chunkManifest = new(capacity: 256);

    private bool _tickActive;
    private readonly MemoryStream _preTickBuffer = new(capacity: 4096);
    private uint _preTickEventCount;
    private long _preTickDroppedBytes;
    /// <summary>
    /// MIN startTs across all buffered pre-tick records. Initialized to <c>long.MaxValue</c>; updated only when a
    /// strictly-smaller startTs is seen. Pre-tick records can arrive in non-monotonic timestamp order across consumer
    /// drain passes (each pass sorts internally; cross-pass ordering isn't preserved), so tracking min ≠ tracking
    /// "first record processed".
    /// </summary>
    private long _preTickFirstTs = long.MaxValue;
    /// <summary>MAX startTs across all buffered pre-tick records. Initialized to <c>long.MinValue</c>.</summary>
    private long _preTickLastTs = long.MinValue;
    private uint _currentTickNumber;
    private long _currentTickFirstTs;
    private long _currentTickLastTs;
    private uint _currentEventCount;
    private long _currentMaxSystemDurationTicks;
    private ulong _currentActiveSystemsBitmask;
    // ── v9 (issue #289 follow-up) ──
    /// <summary>OverloadDetector level captured from the current tick's <c>TickEnd</c> payload — written into this tick's summary.</summary>
    private byte _currentOverloadLevel;
    /// <summary>Effective tick multiplier captured from the current tick's <c>TickEnd</c> payload.</summary>
    private byte _currentTickMultiplier;
    /// <summary>
    /// Metronome wait duration (µs, saturating at <see cref="ushort.MaxValue"/>) that ended just before the current tick's <c>TickStart</c>.
    /// Copied from <see cref="_pendingMetronomeWaitUs"/> at TickStart time.
    /// </summary>
    private ushort _currentMetronomeWaitUs;
    /// <summary>Intent class of the wait above (0=CatchUp, 1=Throttled, 2=Headroom).</summary>
    private byte _currentMetronomeIntentClass;
    /// <summary>
    /// Pending wait duration captured when the builder observes a <see cref="TraceEventKind.SchedulerMetronomeWait"/> span; consumed
    /// at the NEXT <c>TickStart</c> by copying into <see cref="_currentMetronomeWaitUs"/> + clearing back to zero.
    /// </summary>
    private ushort _pendingMetronomeWaitUs;
    /// <summary>Intent class of the pending wait above. Same lifecycle as <see cref="_pendingMetronomeWaitUs"/>.</summary>
    private byte _pendingMetronomeIntentClass;

    /// <summary>OverloadDetector consecutive-overrun counter from the latest kind-242 instant observed in this tick. Written into the summary at finalize.</summary>
    private ushort _currentConsecutiveOverrun;
    /// <summary>OverloadDetector consecutive-underrun counter from the latest kind-242 instant. Same lifecycle.</summary>
    private ushort _currentConsecutiveUnderrun;
    /// <summary>
    /// Set when <c>TickEnd</c> or <c>SchedulerMetronomeWait</c> fires. Blocks any subsequent events from updating
    /// <c>_currentTickLastTs</c>. Prevents next-tick worker spans (e.g. <c>ConcurrencyEpochScopeEnter</c>) from
    /// bleeding into the current tick's duration window when they arrive in the stream before the next TickStart.
    /// Reset to <c>false</c> at each TickStart.
    /// </summary>
    private bool _tickEndSeen;

    private readonly MemoryStream _chunkBuffer = new(capacity: TraceFileCacheConstants.ByteCap);
    private uint _chunkFromTick;
    private uint _chunkEventCount;
    private uint _chunkFlags;
    private long _tickBytesInChunk;
    private uint _tickEventsInChunk;

    private readonly Dictionary<ulong, long> _openKickoffs = new();
    private long _foldedCount;

    private double _globalStartUs;
    private bool _globalStartSet;
    private double _globalMaxTickDurationUs;
    private double _globalMaxSystemDurationUs;
    private long _globalTotalEvents;

    private bool _disposed;

    /// <summary>Read-only view of finalized tick summaries. Grows over time as the builder finalizes ticks.</summary>
    public IReadOnlyList<TickSummary> TickSummaries => _tickSummaries;

    /// <summary>Read-only view of flushed chunks. Grows as the builder flushes chunks.</summary>
    public IReadOnlyList<ChunkManifestEntry> ChunkManifest => _chunkManifest;

    /// <summary>
    /// Snapshot of accumulated per-system aggregate durations as a sorted-by-system-index array. Recomputed on each access — caller should
    /// cache for hot paths. Used by the live save flow to capture aggregates before disposal.
    /// </summary>
    public SystemAggregateDuration[] GetSystemAggregatesSnapshot()
    {
        var arr = new SystemAggregateDuration[_systemAggregates.Count];
        var i = 0;
        foreach (var kv in _systemAggregates)
        {
            arr[i++] = new SystemAggregateDuration
            {
                SystemIndex = (ushort)kv.Key,
                Padding = 0,
                InvocationCount = kv.Value.InvocationCount,
                TotalDurationUs = kv.Value.TotalDurationUs,
            };
        }
        Array.Sort(arr, static (a, b) => a.SystemIndex.CompareTo(b.SystemIndex));
        return arr;
    }

    /// <summary>
    /// Read-only view of the span-name intern table. Replay traces have this pre-populated; live attaches grow it lazily as records arrive.
    /// </summary>
    public IReadOnlyDictionary<int, string> SpanNames => _spanNames;

    /// <summary>Folded-completion count (kickoff/completion async fold). Mirrors <see cref="TraceFileCacheBuilder.BuildResult.FoldedCount"/>.</summary>
    public long FoldedCount => _foldedCount;

    /// <summary>Total event count across all chunks (matches the value reported in <see cref="GlobalMetricsFixed.TotalEvents"/>).</summary>
    public long TotalEvents => _globalTotalEvents;

    /// <summary>Bytes that were dropped because the pre-tick buffer was at <see cref="PreTickBufferCap"/>. Diagnostic only.</summary>
    public long PreTickDroppedBytes => _preTickDroppedBytes;

    /// <summary>
    /// Snapshot of the global metrics computed from the current state. Recomputed on each access — caller should cache for
    /// per-frame use. Live SSE delta emission throttles to ~1 Hz.
    /// </summary>
    public GlobalMetricsFixed CurrentGlobalMetrics => BuildGlobalMetricsSnapshot();

    /// <summary>
    /// Build a <see cref="GlobalMetricsFixed"/> from the current state — shared by the live <see cref="CurrentGlobalMetrics"/>
    /// property (1 Hz SSE delta) and the trailer-write path in <see cref="Dispose"/>. Recomputes p95 on every call.
    /// </summary>
    private GlobalMetricsFixed BuildGlobalMetricsSnapshot()
    {
        double p95 = 0;
        if (_tickSummaries.Count > 0)
        {
            var durations = new double[_tickSummaries.Count];
            for (var i = 0; i < _tickSummaries.Count; i++)
            {
                durations[i] = _tickSummaries[i].DurationUs;
            }
            Array.Sort(durations);
            var p95Idx = (int)(durations.Length * 0.95);
            p95 = durations[Math.Min(p95Idx, durations.Length - 1)];
        }
        return new GlobalMetricsFixed
        {
            GlobalStartUs = _globalStartUs,
            GlobalEndUs = _tickSummaries.Count > 0 ? _currentTickLastTs / _ticksPerUs : _globalStartUs,
            MaxTickDurationUs = _globalMaxTickDurationUs,
            MaxSystemDurationUs = _globalMaxSystemDurationUs,
            P95TickDurationUs = p95,
            TotalEvents = _globalTotalEvents,
            TotalTicks = (uint)_tickSummaries.Count,
            SystemAggregateCount = (uint)_systemAggregates.Count,
        };
    }

    /// <summary>Raised every time a tick is finalized (added to <see cref="TickSummaries"/>). Live mode subscribes; replay ignores.</summary>
    public event Action<TickSummary> TickFinalized;

    /// <summary>Raised every time a chunk is flushed (added to <see cref="ChunkManifest"/>). Live mode subscribes; replay ignores.</summary>
    public event Action<ChunkManifestEntry> ChunkFlushed;

    /// <summary>
    /// Construct a builder. Caller is responsible for parsing the source's header / system tables / archetypes / component types
    /// up front (replay: via <see cref="TraceFileReader"/>; live: via the engine's Init frame which carries the same data).
    /// </summary>
    /// <param name="sink">Where flushed chunks land. <see cref="FileCacheSink"/> for replay, <c>AppendOnlyChunkSink</c> for live.</param>
    /// <param name="ownsSink">If <c>true</c>, the builder disposes the sink in its own <see cref="Dispose"/>.</param>
    /// <param name="header">Source profiler header (used for <c>TimestampFrequency</c> and version stamping).</param>
    /// <param name="sourceFingerprint">32-byte source fingerprint to embed in the cache header. Live: arbitrary 32-byte session ID.</param>
    /// <param name="spanNames">Span-name intern table from the source. Pre-populated for replay traces; empty / lazily-grown for live (live appends spans through the same buffer).</param>
    public IncrementalCacheBuilder(
        ICacheChunkSink sink,
        bool ownsSink,
        in ProfilerHeader header,
        ReadOnlySpan<byte> sourceFingerprint,
        IReadOnlyDictionary<int, string> spanNames)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (sourceFingerprint.Length < 32)
        {
            throw new ArgumentException("Fingerprint must be at least 32 bytes.", nameof(sourceFingerprint));
        }
        _sink = sink;
        _ownsSink = ownsSink;
        _header = header;
        _ticksPerUs = header.TimestampFrequency / 1_000_000.0;
        _fingerprint = sourceFingerprint[..32].ToArray();
        _spanNames = spanNames ?? new Dictionary<int, string>();
    }

    /// <summary>
    /// Feed one or more raw records (back-to-back, each prefixed with a u16 size) through the builder. Records must be complete
    /// — partial records across calls are NOT supported. The format matches the engine's block-frame payload exactly.
    /// </summary>
    public void FeedRawRecords(ReadOnlySpan<byte> records)
    {
        ThrowIfDisposed();
        Span<byte> foldDurationBuf = stackalloc byte[8];
        var pos = 0;
        while (pos + CommonHeaderSize <= records.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size < CommonHeaderSize || pos + size > records.Length)
            {
                break;
            }

            var kind = (TraceEventKind)records[pos + 2];
            var startTs = BinaryPrimitives.ReadInt64LittleEndian(records[(pos + 4)..]);

            if (kind == TraceEventKind.TickStart)
            {
                if (_tickActive)
                {
                    FinalizeCurrentTick();
                }

                var nextTickNumber = _currentTickNumber + 1;
                if (_chunkFromTick == 0)
                {
                    if (_preTickBuffer.Length > 0)
                    {
                        // Pre-tick records (events emitted before the first TickStart — typical AntHill case where
                        // _bridge.Initialize spawns 200K entities before the runtime's DAG scheduler emits its first
                        // TickStart) get flushed as a SYNTHETIC TICK 0 chunk: own [FromTick=0, ToTick=1) range, own
                        // TickSummary with the pre-tick event window, IsContinuation=true so the client decoder seeds
                        // currentTick=0 (no TickStart record at chunk head). This makes the events selectable from
                        // TickOverview — without this they'd get prepended into chunk 1 with timestamps outside any
                        // selectable tick's range and never render.
                        _chunkBuffer.Write(_preTickBuffer.GetBuffer(), 0, (int)_preTickBuffer.Length);
                        _chunkEventCount += _preTickEventCount;
                        _globalTotalEvents += _preTickEventCount;

                        // Synthesize tick-0 summary BEFORE flushing so it lands in TickSummaries in tick-number order.
                        SynthesizeTickZeroSummary();

                        _chunkFromTick = 0;
                        _chunkFlags = TraceFileCacheConstants.FlagIsContinuation;
                        FlushChunkInternal(toTick: 1);
                        _chunkFlags = 0;

                        _preTickBuffer.SetLength(0);
                        _preTickBuffer.Position = 0;
                        _preTickEventCount = 0;
                    }
                    _chunkFromTick = nextTickNumber;
                }
                else
                {
                    var ticksInChunk = nextTickNumber - _chunkFromTick;
                    if (ticksInChunk >= TraceFileCacheConstants.TickCap
                        || _chunkBuffer.Length >= TraceFileCacheConstants.ByteCap
                        || _chunkEventCount >= TraceFileCacheConstants.EventCap)
                    {
                        FlushChunkInternal(nextTickNumber);
                        _chunkFromTick = nextTickNumber;
                        _chunkFlags = 0;
                    }
                }
                _tickBytesInChunk = 0;
                _tickEventsInChunk = 0;

                _currentTickNumber = nextTickNumber;
                _currentTickFirstTs = startTs;
                _currentTickLastTs = startTs;
                _currentEventCount = 0;
                _currentMaxSystemDurationTicks = 0;
                _currentActiveSystemsBitmask = 0;
                _tickActive = true;

                // v9 (#289): consume pending metronome-wait values captured between the previous tick's TickEnd and this TickStart.
                // The wait that ENDED just before this tick's TickStart conceptually belongs to *this* tick (it's the gap THIS
                // tick was waiting through). overloadLevel + multiplier are populated separately by the TickEnd handler when this
                // tick eventually ends — they describe *this* tick's overload state, not the previous one.
                _currentMetronomeWaitUs = _pendingMetronomeWaitUs;
                _currentMetronomeIntentClass = _pendingMetronomeIntentClass;
                _pendingMetronomeWaitUs = 0;
                _pendingMetronomeIntentClass = 0;
                _currentOverloadLevel = 0;
                _currentTickMultiplier = 0;
                _currentConsecutiveOverrun = 0;
                _currentConsecutiveUnderrun = 0;
                _tickEndSeen = false;

                if (!_globalStartSet)
                {
                    _globalStartUs = startTs / _ticksPerUs;
                    _globalStartSet = true;
                }
            }

            if (_tickActive)
            {
                // Root cause of DurationUs inflation: next-tick worker spans (e.g. ConcurrencyEpochScopeEnter) can
                // arrive in the stream BEFORE the next TickStart marker, while _tickActive is still true for the current
                // tick. Freezing _currentTickLastTs at TickEnd/SchedulerMetronomeWait prevents their timestamps
                // (≈ next tick's start) from inflating DurationUs of the current tick.
                if (kind == TraceEventKind.TickEnd)
                {
                    // TickEnd always updates (it's the canonical end marker), then seals.
                    _currentTickLastTs = startTs;
                    _tickEndSeen = true;
                }
                else if (kind == TraceEventKind.SchedulerMetronomeWait)
                {
                    // SchedulerMetronomeWait fires right after TickEnd+GaugeSnapshot. Its startTs = waitStart ≈ T+4ms.
                    // Allow it to update (it nudges DurationUs to include post-TickEnd setup time), then seal so
                    // nothing after the wait-start is counted.
                    if (startTs > _currentTickLastTs)
                        _currentTickLastTs = startTs;
                    _tickEndSeen = true;
                }
                else if (!_tickEndSeen && startTs > _currentTickLastTs)
                {
                    _currentTickLastTs = startTs;
                }

                // v9 (#289): capture per-tick overload state from TickEnd payload (overloadLevel u8 at offset 0, tickMultiplier u8 at offset 1).
                if (kind == TraceEventKind.TickEnd && size >= CommonHeaderSize + 2)
                {
                    _currentOverloadLevel = records[pos + CommonHeaderSize];
                    _currentTickMultiplier = records[pos + CommonHeaderSize + 1];
                }

                // v9 (#289): observe the metronome wait span — capture wait duration (saturating u16) + intent class for the *next*
                // TickStart to consume into _currentMetronomeWait*. SchedulerMetronomeWait (kind 241) has no trace context flag set
                // by the producer, so the payload starts at SpanHeaderExtSize (no TraceContextSize offset). Layout: scheduledTs i64,
                // multiplier u8, intentClass u8, phaseFlags u8.
                if (kind == TraceEventKind.SchedulerMetronomeWait && size >= CommonHeaderSize + SpanHeaderExtSize + 11)
                {
                    var durationTicks = BinaryPrimitives.ReadInt64LittleEndian(records[(pos + 12)..]);
                    var spanFlags = records[pos + 36];
                    var hasTraceContext = (spanFlags & 0x01) != 0;
                    var payloadOffset = pos + CommonHeaderSize + SpanHeaderExtSize + (hasTraceContext ? TraceContextSize : 0);
                    if (payloadOffset + 11 <= pos + size)
                    {
                        var waitUs = durationTicks / _ticksPerUs;
                        // Saturate at u16.MaxValue ≈ 65 ms — far above any realistic metronome wait (worst case ≈ 100 ms at multiplier=6 + 60Hz).
                        _pendingMetronomeWaitUs = waitUs <= 0 ? (ushort)0 : (waitUs >= ushort.MaxValue ? ushort.MaxValue : (ushort)waitUs);
                        // intentClass at scheduledTs(8) + multiplier(1) → offset +9.
                        _pendingMetronomeIntentClass = records[payloadOffset + 9];
                    }
                }

                // v11 (#289 follow-up): capture OverloadDetector consecutive counters from kind 242. Instant payload layout (after
                // CommonHeaderSize): tick i64, overrunRatio f32, consecutiveOverrun u16, consecutiveUnderrun u16,
                // consecutiveQueueGrowth u16, queueDepth i32, level u8, multiplier u8 (24 B). We need the two consecutive counters
                // at offsets +12 and +14 to surface in the OverloadStrip tooltip.
                if (kind == TraceEventKind.SchedulerOverloadDetector && size >= CommonHeaderSize + 24)
                {
                    var p = pos + CommonHeaderSize;
                    _currentConsecutiveOverrun = BinaryPrimitives.ReadUInt16LittleEndian(records[(p + 12)..]);
                    _currentConsecutiveUnderrun = BinaryPrimitives.ReadUInt16LittleEndian(records[(p + 14)..]);
                }

                if (IsCompletionKind(kind) && size >= CommonHeaderSize + SpanHeaderExtSize)
                {
                    var completionSpanId = BinaryPrimitives.ReadUInt64LittleEndian(records[(pos + 20)..]);
                    if (_openKickoffs.Remove(completionSpanId, out var kickoffOffset))
                    {
                        var completionDurationTicks = BinaryPrimitives.ReadInt64LittleEndian(records[(pos + 12)..]);
                        var savedLength = _chunkBuffer.Length;
                        _chunkBuffer.Position = kickoffOffset + CommonHeaderSize;
                        BinaryPrimitives.WriteInt64LittleEndian(foldDurationBuf, completionDurationTicks);
                        _chunkBuffer.Write(foldDurationBuf);
                        _chunkBuffer.Position = savedLength;

                        _foldedCount++;
                        pos += size;
                        continue;
                    }
                }

                if (_tickBytesInChunk + size > TraceFileCacheConstants.IntraTickByteCap
                    || _tickEventsInChunk >= TraceFileCacheConstants.IntraTickEventCap)
                {
                    FlushChunkInternal(_currentTickNumber + 1);
                    _chunkFromTick = _currentTickNumber;
                    _chunkFlags = TraceFileCacheConstants.FlagIsContinuation;
                    _tickBytesInChunk = 0;
                    _tickEventsInChunk = 0;
                }

                _currentEventCount++;
                _globalTotalEvents++;
                if (IsKickoffKind(kind) && size >= CommonHeaderSize + SpanHeaderExtSize)
                {
                    var kickoffSpanId = BinaryPrimitives.ReadUInt64LittleEndian(records[(pos + 20)..]);
                    _openKickoffs[kickoffSpanId] = _chunkBuffer.Length;
                }
                _chunkBuffer.Write(records.Slice(pos, size));
                _chunkEventCount++;
                _tickBytesInChunk += size;
                _tickEventsInChunk++;
            }
            else
            {
                // Pre-tick: any record that arrives before the first TickStart gets buffered and emitted as a
                // synthetic tick 0 when the first tick begins. This catches the AntHill case where the engine's
                // setup phase emits events (ThreadInfo, MemoryAllocEvent, EcsSpawn for 200K entities, etc.) BEFORE
                // the runtime's DAG scheduler emits its first TickStart. The original whitelist (only
                // Memory/GC/ThreadInfo) silently dropped EcsSpawn and other "real" event kinds emitted pre-tick —
                // fixing #289 also fixes that latent bug for replay traces of the same pattern. The 16 MB cap
                // protects against runaway buffering.
                if (_preTickBuffer.Length + size <= PreTickBufferCap)
                {
                    _preTickBuffer.Write(records.Slice(pos, size));
                    if (startTs < _preTickFirstTs) _preTickFirstTs = startTs;
                    if (startTs > _preTickLastTs) _preTickLastTs = startTs;
                    _preTickEventCount++;
                }
                else
                {
                    _preTickDroppedBytes += size;
                }
            }

            if (kind == TraceEventKind.SchedulerChunk && size >= CommonHeaderSize + SpanHeaderExtSize + 2)
            {
                var durationTicks = BinaryPrimitives.ReadInt64LittleEndian(records[(pos + 12)..]);
                var spanFlags = records[pos + 36];
                var hasTraceContext = (spanFlags & 0x01) != 0;
                var payloadOffset = pos + CommonHeaderSize + SpanHeaderExtSize + (hasTraceContext ? TraceContextSize : 0);
                if (payloadOffset + 2 <= pos + size)
                {
                    var systemIdx = BinaryPrimitives.ReadUInt16LittleEndian(records[payloadOffset..]);

                    if (durationTicks > _currentMaxSystemDurationTicks)
                    {
                        _currentMaxSystemDurationTicks = durationTicks;
                    }
                    if (systemIdx < 64)
                    {
                        _currentActiveSystemsBitmask |= 1UL << systemIdx;
                    }

                    var durationUs = durationTicks / _ticksPerUs;
                    if (!_systemAggregates.TryGetValue(systemIdx, out var agg))
                    {
                        agg = (0u, 0.0);
                    }
                    _systemAggregates[systemIdx] = (agg.InvocationCount + 1, agg.TotalDurationUs + durationUs);
                }
            }

            pos += size;
        }
    }

    /// <summary>
    /// Force-flush the current in-progress chunk. Live mode calls this on a 200 ms timer so partial chunks become visible to
    /// clients without waiting for the chunk-cap trigger. Mid-tick: opens a new continuation chunk on the next emit.
    /// </summary>
    public bool FlushCurrentChunk()
    {
        ThrowIfDisposed();
        if (_chunkBuffer.Length == 0 || _chunkEventCount == 0)
        {
            return false;
        }
        if (_tickActive)
        {
            FlushChunkInternal(_currentTickNumber + 1);
            _chunkFromTick = _currentTickNumber;
            _chunkFlags = TraceFileCacheConstants.FlagIsContinuation;
            _tickBytesInChunk = 0;
            _tickEventsInChunk = 0;
        }
        else
        {
            FlushChunkInternal(_currentTickNumber + 1);
            _chunkFromTick = 0;
            _chunkFlags = 0;
        }
        return true;
    }

    /// <summary>
    /// Finalize the open tick (uses event timestamps to derive durationUs if no <c>TickEnd</c> arrived). Idempotent — safe to
    /// call multiple times. Live mode calls this from a 250 ms fallback timer so the latest bar settles even when the next
    /// <c>TickStart</c> hasn't arrived yet.
    /// </summary>
    public void FlushTrailingTick()
    {
        ThrowIfDisposed();
        if (!_tickActive)
        {
            return;
        }
        FinalizeCurrentTick();
        _tickActive = false;
    }

    /// <summary>
    /// Disposes the builder. Flushes the trailing tick and any in-progress chunk, then writes the trailer if the sink supports it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        FinalizePendingState();

        if (_sink.SupportsTrailer)
        {
            // Source-derived close path: no embedded SourceMetadata, no IsSelfContained flag, fingerprint from constructor.
            // (The live save-as-replay flow does NOT go through this; it builds + writes its own trailer against a fresh sink with a
            //  RELOCATED manifest — see AttachSessionRuntime.SaveSessionCore.)
            var metrics = BuildGlobalMetricsSnapshot();
            var aggArr = GetSystemAggregatesSnapshot();
            var cacheHeader = new CacheHeader
            {
                Flags = 0,
                SourceVersion = _header.Version,
                ChunkerVersion = TraceFileCacheConstants.CurrentChunkerVersion,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
            };
            CacheHeader.SetIdentifier(ref cacheHeader, _fingerprint);
            _sink.WriteTrailer(_tickSummaries, metrics, aggArr, _chunkManifest, _spanNames, sourceMetadataBytes: default, cacheHeader);
        }

        if (_ownsSink)
        {
            _sink.Dispose();
        }
    }

    /// <summary>
    /// Flush trailing tick / in-progress chunk so subsequent trailer writes see a consistent snapshot. Idempotent — safe to call before
    /// the live save-as-replay flow takes a snapshot of <see cref="ChunkManifest"/> + <see cref="TickSummaries"/>.
    /// </summary>
    public void FinalizePendingState()
    {
        if (_tickActive)
        {
            FinalizeCurrentTick();
            _tickActive = false;
        }
        if (_chunkBuffer.Length > 0)
        {
            FlushChunkInternal(_currentTickNumber + 1);
        }
    }

    /// <summary>
    /// Static convenience: build a sidecar cache from a trace file in one call. Behavior matches
    /// <see cref="TraceFileCacheBuilder.Build"/>; this overload is the implementation used by the static façade.
    /// </summary>
    public static TraceFileCacheBuilder.BuildResult Build(string sourcePath, string cachePath, IProgress<TraceFileCacheBuilder.BuildProgress> progress = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(cachePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source trace file not found.", sourcePath);
        }

        var fingerprint = new byte[32];
        TraceFileCacheReader.ComputeSourceFingerprint(sourcePath, fingerprint);

        var started = DateTime.UtcNow;
        var lastProgressAt = started;

        using var sourceStream = File.OpenRead(sourcePath);
        var totalBytes = sourceStream.Length;
        using var reader = new TraceFileReader(sourceStream);
        progress?.Report(new TraceFileCacheBuilder.BuildProgress(0, totalBytes, 0, 0));

        var fileHeader = reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();

        var profilerHeader = new ProfilerHeader
        {
            Version = fileHeader.Version,
            TimestampFrequency = fileHeader.TimestampFrequency,
        };

        var sink = FileCacheSink.Create(cachePath);
        // ownsSink: true so that disposing the builder closes the underlying TraceFileCacheWriter.
        var builder = new IncrementalCacheBuilder(sink, ownsSink: true, profilerHeader, fingerprint, reader.SpanNames);
        try
        {
            while (reader.ReadNextBlock(out var recordBytes, out _))
            {
                builder.FeedRawRecords(recordBytes.Span);

                if (progress != null)
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastProgressAt).TotalMilliseconds >= ProgressIntervalMs)
                    {
                        progress.Report(new TraceFileCacheBuilder.BuildProgress(sourceStream.Position, totalBytes, builder.TickSummaries.Count, builder.TotalEvents));
                        lastProgressAt = now;
                    }
                }
            }

            // Builder.Dispose() finalizes the trailing tick + flushes the last chunk + writes the trailer.

            // Capture stats before disposal — Dispose closes the sink and we want them for BuildResult.
            // We pre-materialize them here, then dispose.
            builder.Dispose();
            var summaryCount = builder._tickSummaries.Count;
            var eventCount = builder._globalTotalEvents;
            var foldedCount = builder._foldedCount;
            var systemCount = builder._systemAggregates.Count;

            progress?.Report(new TraceFileCacheBuilder.BuildProgress(totalBytes, totalBytes, summaryCount, eventCount));

            return new TraceFileCacheBuilder.BuildResult(summaryCount, eventCount, foldedCount, systemCount, DateTime.UtcNow - started, cachePath);
        }
        catch
        {
            builder.Dispose();
            throw;
        }
    }

    private const int ProgressIntervalMs = 200;

    /// <summary>
    /// Emit a synthetic <c>tick 0</c> <see cref="TickSummary"/> covering the pre-tick event window — the time between
    /// the earliest pre-tick record and the latest. Called once on first <c>TickStart</c> when pre-tick records are
    /// present. Pairs with the same call's chunk-0 flush so the client can click tick 0 in <c>TickOverview</c> and
    /// have its viewRange land on the right time slice.
    /// </summary>
    private void SynthesizeTickZeroSummary()
    {
        if (_preTickEventCount == 0 || _preTickFirstTs == long.MaxValue)
        {
            return;
        }
        var startUs = _preTickFirstTs / _ticksPerUs;
        var lastUs = _preTickLastTs / _ticksPerUs;
        var durationUs = lastUs > startUs ? lastUs - startUs : 0;
        var summary = new TickSummary
        {
            TickNumber = 0,
            DurationUs = (float)durationUs,
            EventCount = _preTickEventCount,
            MaxSystemDurationUs = 0,
            ActiveSystemsBitmask = 0,
            StartUs = startUs,
        };
        _tickSummaries.Add(summary);
        if (!_globalStartSet)
        {
            _globalStartUs = startUs;
            _globalStartSet = true;
        }
        if (durationUs > _globalMaxTickDurationUs)
        {
            _globalMaxTickDurationUs = durationUs;
        }
        TickFinalized?.Invoke(summary);
    }

    private void FinalizeCurrentTick()
    {
        if (_currentTickFirstTs <= 0)
        {
            return;
        }
        if (_tickSummaries.Count > 0)
        {
            var prevFirstTs = (long)(_tickSummaries[^1].StartUs * _ticksPerUs);
            if (_currentTickFirstTs < prevFirstTs)
            {
                return;
            }
        }

        var durationUs = (_currentTickLastTs - _currentTickFirstTs) / _ticksPerUs;
        if (durationUs < 0) durationUs = 0;
        var maxSysUs = _currentMaxSystemDurationTicks / _ticksPerUs;
        var startUs = _currentTickFirstTs / _ticksPerUs;

        var summary = new TickSummary
        {
            TickNumber = _currentTickNumber,
            DurationUs = (float)durationUs,
            EventCount = _currentEventCount,
            MaxSystemDurationUs = (float)maxSysUs,
            ActiveSystemsBitmask = _currentActiveSystemsBitmask,
            StartUs = startUs,
            // v9 fields (#289 follow-up). Wait values describe the metronome gap immediately PRECEDING this tick;
            // overload/multiplier come from this tick's TickEnd payload. Either set may be zero on a v8 trace replay.
            OverloadLevel = _currentOverloadLevel,
            TickMultiplier = _currentTickMultiplier,
            MetronomeWaitUs = _currentMetronomeWaitUs,
            MetronomeIntentClass = _currentMetronomeIntentClass,
            // v11 fields — OverloadDetector consecutive streak counters from the kind-242 instant.
            ConsecutiveOverrun = _currentConsecutiveOverrun,
            ConsecutiveUnderrun = _currentConsecutiveUnderrun,
        };
        _tickSummaries.Add(summary);

        if (durationUs > _globalMaxTickDurationUs)
        {
            _globalMaxTickDurationUs = durationUs;
        }
        if (maxSysUs > _globalMaxSystemDurationUs)
        {
            _globalMaxSystemDurationUs = maxSysUs;
        }

        TickFinalized?.Invoke(summary);
    }

    private void FlushChunkInternal(uint toTick)
    {
        if (_chunkBuffer.Length == 0 || _chunkEventCount == 0)
        {
            return;
        }

        var payload = _chunkBuffer.GetBuffer().AsSpan(0, (int)_chunkBuffer.Length);
        var (cacheOffset, compressedLength, uncompressedLength) = _sink.AppendChunk(payload);

        var entry = new ChunkManifestEntry
        {
            FromTick = _chunkFromTick,
            ToTick = toTick,
            CacheByteOffset = cacheOffset,
            CacheByteLength = compressedLength,
            EventCount = _chunkEventCount,
            UncompressedBytes = uncompressedLength,
            Flags = _chunkFlags,
        };
        _chunkManifest.Add(entry);

        _chunkBuffer.SetLength(0);
        _chunkBuffer.Position = 0;
        _chunkEventCount = 0;
        _openKickoffs.Clear();

        ChunkFlushed?.Invoke(entry);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(IncrementalCacheBuilder));
        }
    }

    private static bool IsKickoffKind(TraceEventKind kind) =>
        kind == TraceEventKind.PageCacheDiskRead ||
        kind == TraceEventKind.PageCacheDiskWrite ||
        kind == TraceEventKind.PageCacheFlush;

    private static bool IsCompletionKind(TraceEventKind kind) =>
        kind == TraceEventKind.PageCacheDiskReadCompleted ||
        kind == TraceEventKind.PageCacheDiskWriteCompleted ||
        kind == TraceEventKind.PageCacheFlushCompleted;
}

/// <summary>
/// Minimal subset of <see cref="TraceFileHeader"/> needed by <see cref="IncrementalCacheBuilder"/> — kept abstract so the live path
/// can construct one from the engine's Init frame without owning a full <see cref="TraceFileHeader"/>.
/// </summary>
public readonly struct ProfilerHeader
{
    /// <summary>Source format version (mirrors <see cref="TraceFileHeader.Version"/>).</summary>
    public ushort Version { get; init; }

    /// <summary>Stopwatch frequency (Hz) used to convert tick durations to microseconds.</summary>
    public long TimestampFrequency { get; init; }
}
