using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Typhon.Profiler;

/// <summary>
/// Walks a raw record block (as produced by <c>TraceRecordRing.Drain</c> and decompressed from a file or TCP block frame) and converts each
/// size-prefixed record into a <see cref="LiveTraceEvent"/> DTO ready for JSON serialization.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateful tick-number derivation:</b> the decoder holds a single <c>_currentTick</c> counter that advances on every
/// <see cref="TraceEventKind.TickStart"/> record. Every subsequent record is tagged with the latest tick value. The first <c>TickStart</c> becomes
/// tick <c>1</c> — the server has no way to recover the real scheduler tick index because the wire format doesn't carry it on the record itself.
/// Mid-session reconnects therefore display tick numbers that differ from what the engine sees; for a fresh trace-from-start this is accurate.
/// </para>
/// <para>
/// <b>Supported kinds:</b> all instant and span kinds defined in <see cref="TraceEventKind"/>. Each span kind is decoded via its matching codec
/// (SchedulerChunkEventCodec, BTreeEventCodec, TransactionEventCodec, EcsSpawn/Destroy/Query/ViewRefreshEventCodec, PageCacheEventCodec,
/// ClusterMigrationEventCodec). <see cref="TraceEventKind.NamedSpan"/> is currently surfaced as a generic span with no name decoding.
/// </para>
/// </remarks>
public sealed class RecordDecoder
{
    private readonly double _ticksPerUs;
    private int _currentTick;

    public RecordDecoder(long timestampFrequency)
    {
        if (timestampFrequency <= 0)
        {
            throw new ArgumentException("Timestamp frequency must be positive", nameof(timestampFrequency));
        }
        _ticksPerUs = timestampFrequency / 1_000_000.0;
    }

    /// <summary>Current tick number (last TickStart seen). Exposed for diagnostics.</summary>
    public int CurrentTick => _currentTick;

    /// <summary>Reset the tick counter. Call when a new TCP session starts or when loading a fresh file.</summary>
    public void Reset() => _currentTick = 0;

    /// <summary>
    /// Seed the tick counter before decoding a chunk that doesn't start from tick 1. For NORMAL chunks (those starting with a TickStart
    /// record), pass <c>(fromTick - 1)</c> so that the first TickStart increments the counter to <c>fromTick</c> and subsequent events
    /// get the correct tick numbers. For CONTINUATION chunks (no TickStart at the head — the chunk is mid-tick from a previous
    /// splitting builder flush), use <see cref="SetCurrentTickForContinuation"/> instead.
    /// </summary>
    public void SetCurrentTick(int value) => _currentTick = value;

    /// <summary>
    /// Seed the tick counter for a CONTINUATION chunk — one whose manifest entry carries
    /// <see cref="TraceFileCacheConstants.FlagIsContinuation"/>. Continuation chunks have no leading <see cref="TraceEventKind.TickStart"/>
    /// record (the previous chunk already consumed it), so we seed at <paramref name="fromTick"/> directly rather than <c>fromTick - 1</c>.
    /// Every subsequent record in the block is then correctly tagged with <paramref name="fromTick"/> until the next TickStart (if any)
    /// increments the counter.
    /// </summary>
    public void SetCurrentTickForContinuation(int fromTick) => _currentTick = fromTick;

    /// <summary>
    /// Walks <paramref name="recordBytes"/> as a sequence of size-prefixed records and appends one DTO per record to <paramref name="output"/>.
    /// Malformed records (implausible size, unknown kind) stop the walk early — partial results are still useful to the client.
    /// </summary>
    public void DecodeBlock(ReadOnlySpan<byte> recordBytes, List<LiveTraceEvent> output)
    {
        var savedTick = _currentTick;
        var savedOutputCount = output.Count;
        var pos = 0;

        while (pos + TraceRecordHeader.CommonHeaderSize <= recordBytes.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(recordBytes[pos..]);
            if (size < TraceRecordHeader.CommonHeaderSize || pos + size > recordBytes.Length)
            {
                _currentTick = savedTick;
                if (output.Count > savedOutputCount)
                {
                    output.RemoveRange(savedOutputCount, output.Count - savedOutputCount);
                }
                return;
            }

            var record = recordBytes.Slice(pos, size);
            var kind = (TraceEventKind)record[2];

            if (kind == TraceEventKind.TickStart)
            {
                _currentTick++;
            }

            var dto = DecodeRecord(kind, record);
            if (dto != null)
            {
                output.Add(dto);
            }

            pos += size;
        }
    }

    private LiveTraceEvent DecodeRecord(TraceEventKind kind, ReadOnlySpan<byte> record)
    {
        if (!kind.IsSpan())
        {
            return DecodeInstant(kind, record);
        }

        return kind switch
        {
            TraceEventKind.SchedulerChunk => DecodeSchedulerChunk(record),

            TraceEventKind.SchedulerSystemArchetype => DecodeSchedulerSystemArchetype(record),

            TraceEventKind.BTreeInsert or TraceEventKind.BTreeDelete
                or TraceEventKind.BTreeNodeSplit or TraceEventKind.BTreeNodeMerge => DecodeBTree(record),

            TraceEventKind.TransactionCommit or TraceEventKind.TransactionRollback
                or TraceEventKind.TransactionCommitComponent => DecodeTransaction(record),

            TraceEventKind.TransactionPersist => DecodeTransactionPersist(record),

            TraceEventKind.EcsSpawn => DecodeEcsSpawn(record),
            TraceEventKind.EcsDestroy => DecodeEcsDestroy(record),

            TraceEventKind.EcsQueryExecute or TraceEventKind.EcsQueryCount
                or TraceEventKind.EcsQueryAny => DecodeEcsQuery(record),

            TraceEventKind.EcsViewRefresh => DecodeEcsViewRefresh(record),

            TraceEventKind.PageCacheFetch or TraceEventKind.PageCacheDiskRead
                or TraceEventKind.PageCacheDiskWrite or TraceEventKind.PageCacheAllocatePage
                or TraceEventKind.PageCacheFlush or TraceEventKind.PageEvicted
                or TraceEventKind.PageCacheDiskReadCompleted or TraceEventKind.PageCacheDiskWriteCompleted
                or TraceEventKind.PageCacheFlushCompleted => DecodePageCache(record),

            TraceEventKind.PageCacheBackpressure => DecodePageCacheBackpressure(record),

            TraceEventKind.ClusterMigration => DecodeClusterMigration(record),

            TraceEventKind.RuntimePhaseSpan => DecodeRuntimePhaseSpan(record),

            TraceEventKind.WalFlush or TraceEventKind.WalSegmentRotate
                or TraceEventKind.WalWait => DecodeWal(record),

            TraceEventKind.CheckpointCycle or TraceEventKind.CheckpointCollect
                or TraceEventKind.CheckpointWrite or TraceEventKind.CheckpointFsync
                or TraceEventKind.CheckpointTransition or TraceEventKind.CheckpointRecycle => DecodeCheckpoint(record),

            TraceEventKind.StatisticsRebuild => DecodeStatisticsRebuild(record),

            TraceEventKind.SpatialQueryAabb => DecodeSpatialQueryAabb(record),
            TraceEventKind.SpatialQueryRadius => DecodeSpatialQueryRadius(record),
            TraceEventKind.SpatialQueryRay => DecodeSpatialQueryRay(record),
            TraceEventKind.SpatialQueryFrustum => DecodeSpatialQueryFrustum(record),
            TraceEventKind.SpatialQueryKnn => DecodeSpatialQueryKnn(record),
            TraceEventKind.SpatialQueryCount => DecodeSpatialQueryCount(record),

            TraceEventKind.SpatialRTreeInsert => DecodeSpatialRTreeInsert(record),
            TraceEventKind.SpatialRTreeRemove => DecodeSpatialRTreeRemove(record),
            TraceEventKind.SpatialRTreeNodeSplit => DecodeSpatialRTreeNodeSplit(record),
            TraceEventKind.SpatialRTreeBulkLoad => DecodeSpatialRTreeBulkLoad(record),

            TraceEventKind.SpatialMaintainInsert => DecodeSpatialMaintainInsert(record),
            TraceEventKind.SpatialMaintainUpdateSlowPath => DecodeSpatialMaintainUpdateSlowPath(record),
            TraceEventKind.SpatialTriggerEval => DecodeSpatialTriggerEval(record),
            TraceEventKind.SpatialTierIndexRebuild => DecodeSpatialTierIndexRebuild(record),

            TraceEventKind.SchedulerSystemSingleThreaded => DecodeSchedulerSystemSingleThreaded(record),
            TraceEventKind.SchedulerWorkerIdle => DecodeSchedulerWorkerIdle(record),
            TraceEventKind.SchedulerWorkerBetweenTick => DecodeSchedulerWorkerBetweenTick(record),
            TraceEventKind.SchedulerDependencyFanOut => DecodeSchedulerDependencyFanOut(record),
            TraceEventKind.SchedulerGraphBuild => DecodeSchedulerGraphBuild(record),
            TraceEventKind.SchedulerGraphRebuild => DecodeSchedulerGraphRebuild(record),

            TraceEventKind.RuntimeTransactionLifecycle => DecodeRuntimeTransactionLifecycle(record),
            TraceEventKind.RuntimeSubscriptionOutputExecute => DecodeRuntimeSubscriptionOutputExecute(record),
            TraceEventKind.StoragePageCacheDirtyWalk => DecodeStoragePageCacheDirtyWalk(record),

            TraceEventKind.DataTransactionInit => DecodeDataTransactionInit(record),
            TraceEventKind.DataTransactionPrepare => DecodeDataTransactionPrepare(record),
            TraceEventKind.DataTransactionValidate => DecodeDataTransactionValidate(record),
            TraceEventKind.DataTransactionCleanup => DecodeDataTransactionCleanup(record),
            TraceEventKind.DataMvccVersionCleanup => DecodeDataMvccVersionCleanup(record),
            TraceEventKind.DataIndexBTreeRangeScan => DecodeDataIndexBTreeRangeScan(record),
            TraceEventKind.DataIndexBTreeBulkInsert => DecodeDataIndexBTreeBulkInsert(record),

            TraceEventKind.QueryParse => DecodeQueryParse(record),
            TraceEventKind.QueryParseDnf => DecodeQueryParseDnf(record),
            TraceEventKind.QueryPlan => DecodeQueryPlan(record),
            TraceEventKind.QueryEstimate => DecodeQueryEstimate(record),
            TraceEventKind.QueryPlanSort => DecodeQueryPlanSort(record),
            TraceEventKind.QueryExecuteIndexScan => DecodeQueryExecuteIndexScan(record),
            TraceEventKind.QueryExecuteIterate => DecodeQueryExecuteIterate(record),
            TraceEventKind.QueryExecuteFilter => DecodeQueryExecuteFilter(record),
            TraceEventKind.QueryExecutePagination => DecodeQueryExecutePagination(record),
            TraceEventKind.QueryCount => DecodeQueryCount(record),

            TraceEventKind.EcsQueryConstruct => DecodeEcsQueryConstruct(record),
            TraceEventKind.EcsQuerySubtreeExpand => DecodeEcsQuerySubtreeExpand(record),
            TraceEventKind.EcsViewRefreshPull => DecodeEcsViewRefreshPull(record),
            TraceEventKind.EcsViewIncrementalDrain => DecodeEcsViewIncrementalDrain(record),
            TraceEventKind.EcsViewRefreshFull => DecodeEcsViewRefreshFull(record),
            TraceEventKind.EcsViewRefreshFullOr => DecodeEcsViewRefreshFullOr(record),

            TraceEventKind.DurabilityRecoveryDiscover => DecodeDurabilityRecoveryDiscover(record),
            TraceEventKind.DurabilityRecoverySegment => DecodeDurabilityRecoverySegment(record),
            TraceEventKind.DurabilityRecoveryFpi => DecodeDurabilityRecoveryFpi(record),
            TraceEventKind.DurabilityRecoveryRedo => DecodeDurabilityRecoveryRedo(record),
            TraceEventKind.DurabilityRecoveryUndo => DecodeDurabilityRecoveryUndo(record),
            TraceEventKind.DurabilityRecoveryTickFence => DecodeDurabilityRecoveryTickFence(record),

            TraceEventKind.DurabilityWalQueueDrain => DecodeDurabilityWalQueueDrain(record),
            TraceEventKind.DurabilityWalOsWrite => DecodeDurabilityWalOsWrite(record),
            TraceEventKind.DurabilityWalSignal => DecodeDurabilityWalSignal(record),
            TraceEventKind.DurabilityWalBuffer => DecodeDurabilityWalBuffer(record),
            TraceEventKind.DurabilityWalBackpressure => DecodeDurabilityWalBackpressure(record),

            TraceEventKind.DurabilityCheckpointWriteBatch => DecodeDurabilityCheckpointWriteBatch(record),
            TraceEventKind.DurabilityCheckpointBackpressure => DecodeDurabilityCheckpointBackpressure(record),
            TraceEventKind.DurabilityCheckpointSleep => DecodeDurabilityCheckpointSleep(record),

            _ => DecodeGenericSpan(kind, record),
        };
    }

    private LiveTraceEvent DecodeInstant(TraceEventKind kind, ReadOnlySpan<byte> record)
    {
        switch (kind)
        {
            case TraceEventKind.MemoryAllocEvent:
                return DecodeMemoryAllocEvent(record);
            case TraceEventKind.PerTickSnapshot:
                return DecodePerTickSnapshot(record);
            case TraceEventKind.GcStart:
                return DecodeGcStart(record);
            case TraceEventKind.GcEnd:
                return DecodeGcEnd(record);
            case TraceEventKind.ThreadInfo:
                return DecodeThreadInfo(record);
        }

        var data = InstantEventCodec.Decode(record);
        var timestampUs = data.Timestamp / _ticksPerUs;

        return kind switch
        {
            TraceEventKind.TickStart => new LiveTraceEvent
            {
                Kind = (int)kind,
                ThreadSlot = data.ThreadSlot,
                TickNumber = _currentTick,
                TimestampUs = timestampUs,
            },
            TraceEventKind.TickEnd => new LiveTraceEvent
            {
                Kind = (int)kind,
                ThreadSlot = data.ThreadSlot,
                TickNumber = _currentTick,
                TimestampUs = timestampUs,
                OverloadLevel = data.P1,
                TickMultiplier = data.P2,
            },
            TraceEventKind.PhaseStart or TraceEventKind.PhaseEnd => new LiveTraceEvent
            {
                Kind = (int)kind,
                ThreadSlot = data.ThreadSlot,
                TickNumber = _currentTick,
                TimestampUs = timestampUs,
                Phase = data.P1,
            },
            TraceEventKind.SystemReady => new LiveTraceEvent
            {
                Kind = (int)kind,
                ThreadSlot = data.ThreadSlot,
                TickNumber = _currentTick,
                TimestampUs = timestampUs,
                SystemIndex = data.P1,
            },
            TraceEventKind.SystemSkipped => new LiveTraceEvent
            {
                Kind = (int)kind,
                ThreadSlot = data.ThreadSlot,
                TickNumber = _currentTick,
                TimestampUs = timestampUs,
                SystemIndex = data.P1,
                SkipReason = data.P2,
            },
            _ => null,
        };
    }

    private LiveTraceEvent DecodeGcStart(ReadOnlySpan<byte> record)
    {
        var data = GcInstantEventCodec.DecodeGcStart(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.GcStart,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.Timestamp / _ticksPerUs,
            Generation = data.Generation,
            GcReason = (int)data.Reason,
            GcType = (int)data.Type,
            GcCount = data.Count,
        };
    }

    private LiveTraceEvent DecodeGcEnd(ReadOnlySpan<byte> record)
    {
        var data = GcInstantEventCodec.DecodeGcEnd(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.GcEnd,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.Timestamp / _ticksPerUs,
            Generation = data.Generation,
            GcCount = data.Count,
            GcPauseDurationUs = data.PauseDurationTicks / _ticksPerUs,
            GcPromotedBytes = data.PromotedBytes,
        };
    }

    private LiveTraceEvent DecodeThreadInfo(ReadOnlySpan<byte> record)
    {
        TraceRecordHeader.ReadCommonHeader(record, out var size, out _, out var threadSlot, out var timestamp);
        var p = record[TraceRecordHeader.CommonHeaderSize..];
        var managedThreadId = BinaryPrimitives.ReadInt32LittleEndian(p);
        var nameByteCount = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]);
        string name = null;
        if (nameByteCount > 0 && nameByteCount <= 4096 && p.Length >= 6 + nameByteCount)
        {
            try
            {
                var nameSlice = p.Slice(6, nameByteCount);
                name = System.Text.Encoding.UTF8.GetString(nameSlice);
            }
            catch (System.Text.DecoderFallbackException)
            {
                name = null;
            }
        }
        // Trailing ThreadKind byte (added in cache v4 / wire v4). Pre-bump traces don't carry it; size guard.
        ThreadKind? threadKind = null;
        var kindOffset = TraceRecordHeader.CommonHeaderSize + 4 + 2 + nameByteCount;
        if (size > kindOffset && record.Length > kindOffset)
        {
            threadKind = (ThreadKind)record[kindOffset];
        }

        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.ThreadInfo,
            ThreadSlot = threadSlot,
            TickNumber = _currentTick,
            TimestampUs = timestamp / _ticksPerUs,
            ManagedThreadId = managedThreadId,
            ThreadName = name,
            ThreadKind = threadKind,
        };
    }

    private LiveTraceEvent DecodeMemoryAllocEvent(ReadOnlySpan<byte> record)
    {
        var data = MemoryAllocEventCodec.DecodeMemoryAllocEvent(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.MemoryAllocEvent,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.Timestamp / _ticksPerUs,
            Direction = (int)data.Direction,
            SourceTag = data.SourceTag,
            SizeBytes = data.SizeBytes,
            TotalAfterBytes = data.TotalAfterBytes,
        };
    }

    private LiveTraceEvent DecodePerTickSnapshot(ReadOnlySpan<byte> record)
    {
        var data = PerTickSnapshotEventCodec.DecodePerTickSnapshot(record);

        var gauges = new Dictionary<int, double>(data.Values.Length);
        for (var i = 0; i < data.Values.Length; i++)
        {
            var v = data.Values[i];
            double value = v.Kind switch
            {
                GaugeValueKind.I64Signed => unchecked((long)v.RawValue),
                GaugeValueKind.U32Count or GaugeValueKind.U32PercentHundredths => (uint)v.RawValue,
                _ => v.RawValue,
            };
            gauges[(int)v.Id] = value;
        }

        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.PerTickSnapshot,
            ThreadSlot = data.ThreadSlot,
            TickNumber = (int)data.TickNumber,
            TimestampUs = data.Timestamp / _ticksPerUs,
            Flags = data.Flags,
            Gauges = gauges,
        };
    }

    private LiveTraceEvent DecodeSchedulerChunk(ReadOnlySpan<byte> record)
    {
        var data = SchedulerChunkEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SchedulerChunk,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            SystemIndex = data.SystemIndex,
            ChunkIndex = data.ChunkIndex,
            TotalChunks = data.TotalChunks,
            EntitiesProcessed = data.EntitiesProcessed,
        };
    }

    private LiveTraceEvent DecodeSchedulerSystemArchetype(ReadOnlySpan<byte> record)
    {
        var data = SchedulerSystemArchetypeEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SchedulerSystemArchetype,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            SystemIndex = data.SystemIndex,
            ArchetypeId = data.ArchetypeId,
            EntityCount = data.EntityCount,
            TotalChunks = (ushort)Math.Min(data.ChunkCount, ushort.MaxValue),
        };
    }

    private LiveTraceEvent DecodeBTree(ReadOnlySpan<byte> record)
    {
        var data = BTreeEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
        };
    }

    private LiveTraceEvent DecodeTransaction(ReadOnlySpan<byte> record)
    {
        var data = TransactionEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            Tsn = SignedId(data.Tsn),
            ComponentTypeId = data.Kind == TraceEventKind.TransactionCommitComponent ? data.ComponentTypeId : null,
            ComponentCount = data.HasComponentCount ? data.ComponentCount : null,
            ConflictDetected = data.HasConflictDetected ? data.ConflictDetected : null,
        };
    }

    private LiveTraceEvent DecodeEcsSpawn(ReadOnlySpan<byte> record)
    {
        var data = EcsSpawnEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.EcsSpawn,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            ArchetypeId = data.ArchetypeId,
            EntityId = data.HasEntityId ? Id(data.EntityId) : null,
            Tsn = data.HasTsn ? SignedId(data.Tsn) : null,
        };
    }

    private LiveTraceEvent DecodeEcsDestroy(ReadOnlySpan<byte> record)
    {
        var data = EcsDestroyEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.EcsDestroy,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            EntityId = Id(data.EntityId),
            CascadeCount = data.HasCascadeCount ? data.CascadeCount : null,
            Tsn = data.HasTsn ? SignedId(data.Tsn) : null,
        };
    }

    private LiveTraceEvent DecodeEcsQuery(ReadOnlySpan<byte> record)
    {
        var data = EcsQueryEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            ArchetypeId = data.ArchetypeTypeId,
            ResultCount = data.HasResultCount ? data.ResultCount : null,
            ScanMode = data.HasScanMode ? (int)data.ScanMode : null,
            Found = data.HasFound ? data.Found : null,
        };
    }

    private LiveTraceEvent DecodeEcsViewRefresh(ReadOnlySpan<byte> record)
    {
        var data = EcsViewRefreshEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.EcsViewRefresh,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            ArchetypeId = data.ArchetypeTypeId,
            Mode = data.HasMode ? (int)data.Mode : null,
            ResultCount = data.HasResultCount ? data.ResultCount : null,
            DeltaCount = data.HasDeltaCount ? data.DeltaCount : null,
        };
    }

    private LiveTraceEvent DecodePageCache(ReadOnlySpan<byte> record)
    {
        var data = PageCacheEventCodec.Decode(record);

        var isFlush = data.Kind == TraceEventKind.PageCacheFlush || data.Kind == TraceEventKind.PageCacheFlushCompleted;
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            FilePageIndex = isFlush ? null : data.FilePageIndex,
            PageCount = isFlush ? data.FilePageIndex : (data.HasPageCount ? data.PageCount : null),
        };
    }

    private LiveTraceEvent DecodeClusterMigration(ReadOnlySpan<byte> record)
    {
        var data = ClusterMigrationEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.ClusterMigration,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            ArchetypeId = data.ArchetypeId,
            MigrationCount = data.MigrationCount,
            ComponentCount = data.ComponentCount,
        };
    }

    private LiveTraceEvent DecodeRuntimePhaseSpan(ReadOnlySpan<byte> record)
    {
        var data = RuntimePhaseSpanEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.RuntimePhaseSpan,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            Phase = data.Phase,
        };
    }

    private LiveTraceEvent DecodeGenericSpan(TraceEventKind kind, ReadOnlySpan<byte> record)
    {
        TraceRecordHeader.ReadCommonHeader(record, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(record[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        ulong traceIdHi = 0, traceIdLo = 0;
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(record[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }

        return new LiveTraceEvent
        {
            Kind = (int)kind,
            ThreadSlot = threadSlot,
            TickNumber = _currentTick,
            TimestampUs = startTimestamp / _ticksPerUs,
            DurationUs = durationTicks / _ticksPerUs,
            SpanId = Id(spanId),
            ParentSpanId = Id(parentSpanId),
            TraceIdHi = hasTraceContext ? Id(traceIdHi) : null,
            TraceIdLo = hasTraceContext ? Id(traceIdLo) : null,
        };
    }

    private LiveTraceEvent DecodeTransactionPersist(ReadOnlySpan<byte> record)
    {
        var data = TransactionEventCodec.DecodePersist(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.TransactionPersist,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            Tsn = SignedId(data.Tsn),
            WalLsn = data.HasWalLsn ? SignedId(data.WalLsn) : null,
        };
    }

    private LiveTraceEvent DecodePageCacheBackpressure(ReadOnlySpan<byte> record)
    {
        var data = PageCacheBackpressureCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.PageCacheBackpressure,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            RetryCount = data.RetryCount,
            DirtyCount = data.DirtyCount,
            EpochCount = data.EpochCount,
        };
    }

    private LiveTraceEvent DecodeWal(ReadOnlySpan<byte> record)
    {
        var data = WalEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            BatchByteCount = data.Kind == TraceEventKind.WalFlush ? data.BatchByteCount : null,
            FrameCount = data.Kind == TraceEventKind.WalFlush ? data.FrameCount : null,
            HighLsn = data.Kind == TraceEventKind.WalFlush ? SignedId(data.HighLsn) : null,
            NewSegmentIndex = data.Kind == TraceEventKind.WalSegmentRotate ? data.NewSegmentIndex : null,
            TargetLsn = data.Kind == TraceEventKind.WalWait ? SignedId(data.TargetLsn) : null,
        };
    }

    private LiveTraceEvent DecodeCheckpoint(ReadOnlySpan<byte> record)
    {
        var data = CheckpointEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)data.Kind,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            TargetLsn = data.Kind == TraceEventKind.CheckpointCycle ? SignedId(data.TargetLsn) : null,
            Reason = data.Kind == TraceEventKind.CheckpointCycle ? (int)data.Reason : null,
            DirtyPageCount = data.HasDirtyPageCount ? data.DirtyPageCount : null,
            WrittenCount = data.HasWrittenCount ? data.WrittenCount : null,
            TransitionedCount = data.HasTransitionedCount ? data.TransitionedCount : null,
            RecycledCount = data.HasRecycledCount ? data.RecycledCount : null,
        };
    }

    private LiveTraceEvent DecodeStatisticsRebuild(ReadOnlySpan<byte> record)
    {
        var data = StatisticsRebuildEventCodec.Decode(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.StatisticsRebuild,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            EntityCount = data.EntityCount,
            MutationCount = data.MutationCount,
            SamplingInterval = data.SamplingInterval,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // Spatial query (kinds 117–122)
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeSpatialQueryAabb(ReadOnlySpan<byte> record)
    {
        var data = SpatialQueryEventCodec.DecodeAabb(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialQueryAabb,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            NodesVisited = data.NodesVisited,
            LeavesEntered = data.LeavesEntered,
            ResultCount = data.ResultCount,
            RestartCount = data.RestartCount,
            CategoryMask = data.CategoryMask,
        };
    }

    private LiveTraceEvent DecodeSpatialQueryRadius(ReadOnlySpan<byte> record)
    {
        var data = SpatialQueryEventCodec.DecodeRadius(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialQueryRadius,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            NodesVisited = data.NodesVisited,
            ResultCount = data.ResultCount,
            Radius = data.Radius,
            RestartCount = data.RestartCount,
        };
    }

    private LiveTraceEvent DecodeSpatialQueryRay(ReadOnlySpan<byte> record)
    {
        var data = SpatialQueryEventCodec.DecodeRay(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialQueryRay,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            NodesVisited = data.NodesVisited,
            ResultCount = data.ResultCount,
            MaxDist = data.MaxDist,
            RestartCount = data.RestartCount,
        };
    }

    private LiveTraceEvent DecodeSpatialQueryFrustum(ReadOnlySpan<byte> record)
    {
        var data = SpatialQueryEventCodec.DecodeFrustum(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialQueryFrustum,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            NodesVisited = data.NodesVisited,
            ResultCount = data.ResultCount,
            PlaneCount = data.PlaneCount,
            RestartCount = data.RestartCount,
        };
    }

    private LiveTraceEvent DecodeSpatialQueryKnn(ReadOnlySpan<byte> record)
    {
        var data = SpatialQueryEventCodec.DecodeKnn(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialQueryKnn,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            K = data.K,
            IterCount = data.IterCount,
            FinalRadius = data.FinalRadius,
            ResultCount = data.ResultCount,
        };
    }

    private LiveTraceEvent DecodeSpatialQueryCount(ReadOnlySpan<byte> record)
    {
        var data = SpatialQueryEventCodec.DecodeCount(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialQueryCount,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            Variant = data.Variant,
            NodesVisited = data.NodesVisited,
            ResultCount = data.ResultCount,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // Spatial R-tree (kinds 123–126)
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeSpatialRTreeInsert(ReadOnlySpan<byte> record)
    {
        var data = SpatialRTreeEventCodec.DecodeInsert(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialRTreeInsert,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            EntityId = SignedId(data.EntityId),
            Depth = data.Depth,
            DidSplit = data.DidSplit != 0,
            RestartCount = data.RestartCount,
        };
    }

    private LiveTraceEvent DecodeSpatialRTreeRemove(ReadOnlySpan<byte> record)
    {
        var data = SpatialRTreeEventCodec.DecodeRemove(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialRTreeRemove,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            EntityId = SignedId(data.EntityId),
            LeafCollapse = data.LeafCollapse != 0,
        };
    }

    private LiveTraceEvent DecodeSpatialRTreeNodeSplit(ReadOnlySpan<byte> record)
    {
        var data = SpatialRTreeEventCodec.DecodeNodeSplit(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialRTreeNodeSplit,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            Depth = data.Depth,
            SplitAxis = data.SplitAxis,
            LeftCount = data.LeftCount,
            RightCount = data.RightCount,
        };
    }

    private LiveTraceEvent DecodeSpatialRTreeBulkLoad(ReadOnlySpan<byte> record)
    {
        var data = SpatialRTreeEventCodec.DecodeBulkLoad(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialRTreeBulkLoad,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            EntityCount = data.EntityCount,
            LeafCount = data.LeafCount,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // Spatial maintain + trigger eval + tier-index rebuild
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeSpatialMaintainInsert(ReadOnlySpan<byte> record)
    {
        var data = SpatialMaintainEventCodec.DecodeInsert(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialMaintainInsert,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            EntityId = SignedId(data.EntityPK),
            ComponentTypeId = data.ComponentTypeId,
            DidDegenerate = data.DidDegenerate != 0,
        };
    }

    private LiveTraceEvent DecodeSpatialMaintainUpdateSlowPath(ReadOnlySpan<byte> record)
    {
        var data = SpatialMaintainEventCodec.DecodeUpdateSlowPath(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialMaintainUpdateSlowPath,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            EntityId = SignedId(data.EntityPK),
            ComponentTypeId = data.ComponentTypeId,
            EscapeDistSq = data.EscapeDistSq,
        };
    }

    private LiveTraceEvent DecodeSpatialTriggerEval(ReadOnlySpan<byte> record)
    {
        var data = SpatialTriggerEventCodec.DecodeEval(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialTriggerEval,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            RegionId = data.RegionId,
            OccupantCount = data.OccupantCount,
            EnterCount = data.EnterCount,
            LeaveCount = data.LeaveCount,
        };
    }

    private LiveTraceEvent DecodeSpatialTierIndexRebuild(ReadOnlySpan<byte> record)
    {
        var data = SpatialTierIndexEventCodec.DecodeRebuild(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SpatialTierIndexRebuild,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            ArchetypeId = data.ArchetypeId,
            ClusterCount = data.ClusterCount,
            OldVersion = data.OldVersion,
            NewVersion = data.NewVersion,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // Scheduler spans (Phase 4 batch — kinds 149/150/152/155/159/160)
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeSchedulerSystemSingleThreaded(ReadOnlySpan<byte> record)
    {
        var data = SchedulerSystemEventCodec.DecodeSingleThreaded(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SchedulerSystemSingleThreaded,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SysIdx = data.SysIdx,
            IsParallelQuery = data.IsParallelQuery != 0,
            ChunkCount = data.ChunkCount,
        };
    }

    private LiveTraceEvent DecodeSchedulerWorkerIdle(ReadOnlySpan<byte> record)
    {
        var data = SchedulerWorkerEventCodec.DecodeIdle(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SchedulerWorkerIdle,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            WorkerId = data.WorkerId,
            SpinCount = data.SpinCount,
            IdleUs = data.IdleUs,
        };
    }

    private LiveTraceEvent DecodeSchedulerWorkerBetweenTick(ReadOnlySpan<byte> record)
    {
        var data = SchedulerWorkerEventCodec.DecodeBetweenTick(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SchedulerWorkerBetweenTick,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            WorkerId = data.WorkerId,
            WaitUs = data.WaitUs,
            WakeReason = data.WakeReason,
        };
    }

    private LiveTraceEvent DecodeSchedulerDependencyFanOut(ReadOnlySpan<byte> record)
    {
        var data = SchedulerDependencyEventCodec.DecodeFanOut(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SchedulerDependencyFanOut,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            CompletingSysIdx = data.CompletingSysIdx,
            SuccCount = data.SuccCount,
            SkippedCount = data.SkippedCount,
        };
    }

    private LiveTraceEvent DecodeSchedulerGraphBuild(ReadOnlySpan<byte> record)
    {
        var data = SchedulerGraphEventCodec.DecodeBuild(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SchedulerGraphBuild,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SysCount = data.SysCount,
            EdgeCount = data.EdgeCount,
            TopoLen = data.TopoLen,
        };
    }

    private LiveTraceEvent DecodeSchedulerGraphRebuild(ReadOnlySpan<byte> record)
    {
        var data = SchedulerGraphEventCodec.DecodeRebuild(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.SchedulerGraphRebuild,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            OldSysCount = data.OldSysCount,
            NewSysCount = data.NewSysCount,
            Reason = data.Reason,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // Runtime spans
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeRuntimeTransactionLifecycle(ReadOnlySpan<byte> record)
    {
        var data = RuntimeEventCodec.DecodeLifecycle(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.RuntimeTransactionLifecycle,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SysIdx = data.SysIdx,
            TxDurUs = data.TxDurUs,
            Success = data.Success != 0,
        };
    }

    private LiveTraceEvent DecodeRuntimeSubscriptionOutputExecute(ReadOnlySpan<byte> record)
    {
        var data = RuntimeEventCodec.DecodeOutputExecute(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.RuntimeSubscriptionOutputExecute,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            Tick = data.Tick,
            Level = data.Level,
            ClientCount = data.ClientCount,
            ViewsRefreshed = data.ViewsRefreshed,
            DeltasPushed = data.DeltasPushed,
            OverflowCount = data.OverflowCount,
        };
    }

    private LiveTraceEvent DecodeStoragePageCacheDirtyWalk(ReadOnlySpan<byte> record)
    {
        var data = StorageMiscEventCodec.DecodeDirtyWalk(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.StoragePageCacheDirtyWalk,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            RangeStart = data.RangeStart,
            RangeLen = data.RangeLen,
            DirtyMs = data.DirtyMs,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // Data transactions / MVCC / index
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeDataTransactionInit(ReadOnlySpan<byte> record)
    {
        var data = DataTransactionEventCodec.DecodeInit(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.DataTransactionInit,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            Tsn = SignedId(data.Tsn),
            UowId = data.UowId,
        };
    }

    private LiveTraceEvent DecodeDataTransactionPrepare(ReadOnlySpan<byte> record)
    {
        var data = DataTransactionEventCodec.DecodePrepare(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.DataTransactionPrepare,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            Tsn = SignedId(data.Tsn),
        };
    }

    private LiveTraceEvent DecodeDataTransactionValidate(ReadOnlySpan<byte> record)
    {
        var data = DataTransactionEventCodec.DecodeValidate(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.DataTransactionValidate,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            Tsn = SignedId(data.Tsn),
            EntryCount = data.EntryCount,
        };
    }

    private LiveTraceEvent DecodeDataTransactionCleanup(ReadOnlySpan<byte> record)
    {
        var data = DataTransactionEventCodec.DecodeCleanup(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.DataTransactionCleanup,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            Tsn = SignedId(data.Tsn),
            EntityCount = data.EntityCount,
        };
    }

    private LiveTraceEvent DecodeDataMvccVersionCleanup(ReadOnlySpan<byte> record)
    {
        var data = DataMvccEventCodec.DecodeVersionCleanup(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.DataMvccVersionCleanup,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            Pk = SignedId(data.Pk),
            EntriesFreed = data.EntriesFreed,
        };
    }

    private LiveTraceEvent DecodeDataIndexBTreeRangeScan(ReadOnlySpan<byte> record)
    {
        var data = DataIndexBTreeEventCodec.DecodeRangeScan(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.DataIndexBTreeRangeScan,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            ResultCount = data.ResultCount,
            RestartCount = data.RestartCount,
        };
    }

    private LiveTraceEvent DecodeDataIndexBTreeBulkInsert(ReadOnlySpan<byte> record)
    {
        var data = DataIndexBTreeEventCodec.DecodeBulkInsert(record);
        return new LiveTraceEvent
        {
            Kind = (int)TraceEventKind.DataIndexBTreeBulkInsert,
            ThreadSlot = data.ThreadSlot,
            TickNumber = _currentTick,
            TimestampUs = data.StartTimestamp / _ticksPerUs,
            DurationUs = data.DurationTicks / _ticksPerUs,
            SpanId = Id(data.SpanId),
            ParentSpanId = Id(data.ParentSpanId),
            TraceIdHi = data.HasTraceContext ? Id(data.TraceIdHi) : null,
            TraceIdLo = data.HasTraceContext ? Id(data.TraceIdLo) : null,
            BufferId = data.BufferId,
            EntryCount = data.EntryCount,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // Query pipeline (10 kinds)
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeQueryParse(ReadOnlySpan<byte> record)
    {
        var data = QueryEventCodec.DecodeParse(record);
        return BaseSpan(TraceEventKind.QueryParse, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            PredicateCount = data.PredicateCount,
            BranchCount = data.BranchCount,
        };
    }

    private LiveTraceEvent DecodeQueryParseDnf(ReadOnlySpan<byte> record)
    {
        var data = QueryEventCodec.DecodeParseDnf(record);
        return BaseSpan(TraceEventKind.QueryParseDnf, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            InBranches = data.InBranches,
            OutBranches = data.OutBranches,
        };
    }

    private LiveTraceEvent DecodeQueryPlan(ReadOnlySpan<byte> record)
    {
        var data = QueryEventCodec.DecodePlan(record);
        return BaseSpan(TraceEventKind.QueryPlan, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            EvaluatorCount = data.EvaluatorCount,
            IndexFieldIdx = data.IndexFieldIdx,
            RangeMin = SignedId(data.RangeMin),
            RangeMax = SignedId(data.RangeMax),
        };
    }

    private LiveTraceEvent DecodeQueryEstimate(ReadOnlySpan<byte> record)
    {
        var data = QueryEventCodec.DecodeEstimate(record);
        return BaseSpan(TraceEventKind.QueryEstimate, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            FieldIdx = data.FieldIdx,
            Cardinality = SignedId(data.Cardinality),
        };
    }

    private LiveTraceEvent DecodeQueryPlanSort(ReadOnlySpan<byte> record)
    {
        var data = QueryEventCodec.DecodePlanSort(record);
        return BaseSpan(TraceEventKind.QueryPlanSort, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            EvaluatorCount = data.EvaluatorCount,
            SortNs = data.SortNs,
        };
    }

    private LiveTraceEvent DecodeQueryExecuteIndexScan(ReadOnlySpan<byte> record)
    {
        var data = QueryEventCodec.DecodeIndexScan(record);
        return BaseSpan(TraceEventKind.QueryExecuteIndexScan, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            PrimaryFieldIdx = data.PrimaryFieldIdx,
            Mode = data.Mode,
        };
    }

    private LiveTraceEvent DecodeQueryExecuteIterate(ReadOnlySpan<byte> record)
    {
        var data = QueryEventCodec.DecodeIterate(record);
        return BaseSpan(TraceEventKind.QueryExecuteIterate, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            ChunkCount = data.ChunkCount,
            EntryCount = data.EntryCount,
        };
    }

    private LiveTraceEvent DecodeQueryExecuteFilter(ReadOnlySpan<byte> record)
    {
        var data = QueryEventCodec.DecodeFilter(record);
        return BaseSpan(TraceEventKind.QueryExecuteFilter, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            FilterCount = data.FilterCount,
            RejectedCount = data.RejectedCount,
        };
    }

    private LiveTraceEvent DecodeQueryExecutePagination(ReadOnlySpan<byte> record)
    {
        var data = QueryEventCodec.DecodePagination(record);
        return BaseSpan(TraceEventKind.QueryExecutePagination, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            Skip = data.Skip,
            Take = data.Take,
            EarlyTerm = data.EarlyTerm != 0,
        };
    }

    private LiveTraceEvent DecodeQueryCount(ReadOnlySpan<byte> record)
    {
        var data = QueryEventCodec.DecodeCount(record);
        return BaseSpan(TraceEventKind.QueryCount, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            ResultCount = data.ResultCount,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // ECS query construct + view refresh
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeEcsQueryConstruct(ReadOnlySpan<byte> record)
    {
        var data = EcsQueryDepthEventCodec.DecodeConstruct(record);
        return BaseSpan(TraceEventKind.EcsQueryConstruct, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            TargetArchId = data.TargetArchId,
            Polymorphic = data.Polymorphic != 0,
            MaskSize = data.MaskSize,
        };
    }

    private LiveTraceEvent DecodeEcsQuerySubtreeExpand(ReadOnlySpan<byte> record)
    {
        var data = EcsQueryDepthEventCodec.DecodeSubtreeExpand(record);
        return BaseSpan(TraceEventKind.EcsQuerySubtreeExpand, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            SubtreeCount = data.SubtreeCount,
            RootId = data.RootId,
        };
    }

    private LiveTraceEvent DecodeEcsViewRefreshPull(ReadOnlySpan<byte> record)
    {
        var data = EcsViewEventCodec.DecodeRefreshPull(record);
        return BaseSpan(TraceEventKind.EcsViewRefreshPull, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            QueryNs = data.QueryNs,
            ArchetypeMaskBits = data.ArchetypeMaskBits,
        };
    }

    private LiveTraceEvent DecodeEcsViewIncrementalDrain(ReadOnlySpan<byte> record)
    {
        var data = EcsViewEventCodec.DecodeIncrementalDrain(record);
        return BaseSpan(TraceEventKind.EcsViewIncrementalDrain, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            DeltaCount = data.DeltaCount,
            Overflow = data.Overflow != 0,
        };
    }

    private LiveTraceEvent DecodeEcsViewRefreshFull(ReadOnlySpan<byte> record)
    {
        var data = EcsViewEventCodec.DecodeRefreshFull(record);
        return BaseSpan(TraceEventKind.EcsViewRefreshFull, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            OldCount = data.OldCount,
            NewCount = data.NewCount,
            RequeryNs = data.RequeryNs,
        };
    }

    private LiveTraceEvent DecodeEcsViewRefreshFullOr(ReadOnlySpan<byte> record)
    {
        var data = EcsViewEventCodec.DecodeRefreshFullOr(record);
        return BaseSpan(TraceEventKind.EcsViewRefreshFullOr, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            OldCount = data.OldCount,
            NewCount = data.NewCount,
            BranchCount = data.BranchCount,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // Durability recovery (Phase 8 — kinds 230, 232, 235, 232..)
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeDurabilityRecoveryDiscover(ReadOnlySpan<byte> record)
    {
        var data = DurabilityRecoveryEventCodec.DecodeDiscover(record);
        return BaseSpan(TraceEventKind.DurabilityRecoveryDiscover, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            SegCount = data.SegCount,
            TotalBytes = data.TotalBytes,
            FirstSegId = data.FirstSegId,
        };
    }

    private LiveTraceEvent DecodeDurabilityRecoverySegment(ReadOnlySpan<byte> record)
    {
        var data = DurabilityRecoveryEventCodec.DecodeSegment(record);
        return BaseSpan(TraceEventKind.DurabilityRecoverySegment, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            SegId = data.SegId,
            RecCount = data.RecCount,
            Bytes = data.Bytes,
            Truncated = data.Truncated != 0,
        };
    }

    private LiveTraceEvent DecodeDurabilityRecoveryFpi(ReadOnlySpan<byte> record)
    {
        var data = DurabilityRecoveryEventCodec.DecodeFpi(record);
        return BaseSpan(TraceEventKind.DurabilityRecoveryFpi, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            FpiCount = data.FpiCount,
            RepairedCount = data.RepairedCount,
            Mismatches = data.Mismatches,
        };
    }

    private LiveTraceEvent DecodeDurabilityRecoveryRedo(ReadOnlySpan<byte> record)
    {
        var data = DurabilityRecoveryEventCodec.DecodeRedo(record);
        return BaseSpan(TraceEventKind.DurabilityRecoveryRedo, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            RecordsReplayed = data.RecordsReplayed,
            UowsReplayed = data.UowsReplayed,
            DurUs = data.DurUs,
        };
    }

    private LiveTraceEvent DecodeDurabilityRecoveryUndo(ReadOnlySpan<byte> record)
    {
        var data = DurabilityRecoveryEventCodec.DecodeUndo(record);
        return BaseSpan(TraceEventKind.DurabilityRecoveryUndo, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            VoidedUowCount = data.VoidedUowCount,
        };
    }

    private LiveTraceEvent DecodeDurabilityRecoveryTickFence(ReadOnlySpan<byte> record)
    {
        var data = DurabilityRecoveryEventCodec.DecodeTickFence(record);
        return BaseSpan(TraceEventKind.DurabilityRecoveryTickFence, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            TickFenceCount = data.TickFenceCount,
            Entries = data.Entries,
            // Recovery TickFence carries its own tick number (separate from the global tick); reuse Tick.
            Tick = data.TickNumber,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // Durability WAL depth spans (kinds 220–224 — codec authored 2026-05-10)
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeDurabilityWalQueueDrain(ReadOnlySpan<byte> record)
    {
        var data = DurabilityWalEventCodec.DecodeQueueDrain(record);
        return BaseSpan(TraceEventKind.DurabilityWalQueueDrain, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            BytesAligned = data.BytesAligned,
            FrameCount = data.FrameCount,
        };
    }

    private LiveTraceEvent DecodeDurabilityWalOsWrite(ReadOnlySpan<byte> record)
    {
        var data = DurabilityWalEventCodec.DecodeOsWrite(record);
        return BaseSpan(TraceEventKind.DurabilityWalOsWrite, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            BytesAligned = data.BytesAligned,
            FrameCount = data.FrameCount,
            HighLsn = SignedId(data.HighLsn),
        };
    }

    private LiveTraceEvent DecodeDurabilityWalSignal(ReadOnlySpan<byte> record)
    {
        var data = DurabilityWalEventCodec.DecodeSignal(record);
        return BaseSpan(TraceEventKind.DurabilityWalSignal, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            HighLsn = SignedId(data.HighLsn),
        };
    }

    private LiveTraceEvent DecodeDurabilityWalBuffer(ReadOnlySpan<byte> record)
    {
        var data = DurabilityWalEventCodec.DecodeBuffer(record);
        return BaseSpan(TraceEventKind.DurabilityWalBuffer, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            BytesAligned = data.BytesAligned,
        };
    }

    private LiveTraceEvent DecodeDurabilityWalBackpressure(ReadOnlySpan<byte> record)
    {
        var data = DurabilityWalEventCodec.DecodeBackpressure(record);
        return BaseSpan(TraceEventKind.DurabilityWalBackpressure, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            WaitUs = data.WaitUs,
            ProducerThread = data.ProducerThread,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // Durability Checkpoint depth spans (kinds 225–227 — codec authored 2026-05-10)
    // ───────────────────────────────────────────────────────────────────────

    private LiveTraceEvent DecodeDurabilityCheckpointWriteBatch(ReadOnlySpan<byte> record)
    {
        var data = DurabilityCheckpointEventCodec.DecodeWriteBatch(record);
        return BaseSpan(TraceEventKind.DurabilityCheckpointWriteBatch, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            WriteBatchSize = data.WriteBatchSize,
            StagingAllocated = data.StagingAllocated,
        };
    }

    private LiveTraceEvent DecodeDurabilityCheckpointBackpressure(ReadOnlySpan<byte> record)
    {
        var data = DurabilityCheckpointEventCodec.DecodeBackpressure(record);
        return BaseSpan(TraceEventKind.DurabilityCheckpointBackpressure, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            WaitMs = data.WaitMs,
            Exhausted = data.Exhausted != 0,
        };
    }

    private LiveTraceEvent DecodeDurabilityCheckpointSleep(ReadOnlySpan<byte> record)
    {
        var data = DurabilityCheckpointEventCodec.DecodeSleep(record);
        return BaseSpan(TraceEventKind.DurabilityCheckpointSleep, data.ThreadSlot, data.StartTimestamp, data.DurationTicks,
            data.SpanId, data.ParentSpanId, data.HasTraceContext, data.TraceIdHi, data.TraceIdLo) with
        {
            SleepMs = data.SleepMs,
            WakeReason = data.WakeReason,
        };
    }

    /// <summary>Common-shape span event helper used by the typed-decoder methods above. Captures all the boilerplate fields shared by every span record.</summary>
    private LiveTraceEvent BaseSpan(TraceEventKind kind, byte threadSlot, long startTimestamp, long durationTicks,
        ulong spanId, ulong parentSpanId, bool hasTraceContext, ulong traceIdHi, ulong traceIdLo) => new LiveTraceEvent
    {
        Kind = (int)kind,
        ThreadSlot = threadSlot,
        TickNumber = _currentTick,
        TimestampUs = startTimestamp / _ticksPerUs,
        DurationUs = durationTicks / _ticksPerUs,
        SpanId = Id(spanId),
        ParentSpanId = Id(parentSpanId),
        TraceIdHi = hasTraceContext ? Id(traceIdHi) : null,
        TraceIdLo = hasTraceContext ? Id(traceIdLo) : null,
    };

    // ID fields emitted as decimal strings — preserves full 64-bit precision for JS clients (Number tops at 2^53).
    private static string Id(ulong value) => value.ToString();
    private static string SignedId(long value) => value.ToString();
}
