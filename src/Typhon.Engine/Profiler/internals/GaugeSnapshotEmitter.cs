using System;
using System.Diagnostics;
using JetBrains.Annotations;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Public entry point for emitting a <see cref="TraceEventKind.PerTickSnapshot"/> record carrying the current-tick values of every
/// engine-side gauge. Intended callers: tick-emitting code that wants gauge snapshots aligned with its <c>TickStart</c> / <c>TickEnd</c>
/// markers. <see cref="TyphonRuntime"/> wires this through <c>DagScheduler.GaugeSnapshotCallback</c>; standalone runners (e.g.
/// <c>Typhon.IOProfileRunner</c>) call it directly after their manual <c>TyphonEvent.EmitTickEnd</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Allocation-free:</b> uses a stack-allocated <see cref="GaugeValue"/> buffer sized for the worst-case MVP gauge count
/// (32 entries — MVP uses ~14 with all gauges present). No boxing, no managed allocations.
/// </para>
/// <para>
/// <b>Gating:</b> the first check against <see cref="TelemetryConfig.ProfilerGaugesActive"/> dead-code-eliminates the entire method body
/// on Tier 1 JIT when gauges are off. Same pattern as every other producer-side entry point in the profiler.
/// </para>
/// <para>
/// <b>Fixed-value emission:</b> capacity gauges (<see cref="GaugeId.PageCacheTotalPages"/> etc.) are emitted only on the FIRST snapshot
/// of a session — subsequent snapshots omit them to trim wire bytes. The viewer caches the first-seen value. Callers track this
/// across calls via a <c>ref bool firstEmitted</c> they own; on first call pass <c>ref false</c>, the helper flips it to <c>true</c>
/// before returning.
/// </para>
/// </remarks>
[PublicAPI]
internal static class GaugeSnapshotEmitter
{
    /// <summary>
    /// Collect current values from the provided engine subsystems and publish a single <see cref="TraceEventKind.PerTickSnapshot"/>
    /// record onto the caller's own ring slot.
    /// </summary>
    /// <param name="tickNumber">
    /// The tick the snapshot belongs to — baked into the record so the viewer can group gauges by tick without relying on
    /// <c>TickStart</c>/<c>TickEnd</c> framing.
    /// </param>
    /// <param name="memoryAllocator">
    /// Optional — if provided, emits <see cref="GaugeId.MemoryUnmanagedTotalBytes"/> / <see cref="GaugeId.MemoryUnmanagedPeakBytes"/> /
    /// <see cref="GaugeId.MemoryUnmanagedLiveBlocks"/>. Pass <c>null</c> to skip these three gauges.
    /// </param>
    /// <param name="pagedMmf">
    /// Optional — if provided, emits the page-cache bucket gauges plus the total-pages capacity (first call only). Pass <c>null</c>
    /// to skip the page-cache group.
    /// </param>
    /// <param name="txChain">Optional — if provided, emits <c>TxChainActiveCount</c> + <c>TxChainPoolSize</c>.</param>
    /// <param name="uowRegistry">Optional — if provided, emits <c>UowRegistryActiveCount</c> + <c>UowRegistryVoidCount</c>.</param>
    /// <param name="walManager">
    /// Optional — if provided and WAL is enabled, emits the <see cref="WalCommitBuffer"/> occupancy gauges plus
    /// <see cref="GaugeId.WalCommitBufferCapacity"/> (first-call only). Pass <c>null</c> when WAL is disabled or the caller
    /// doesn't want WAL gauges in this snapshot.
    /// </param>
    /// <param name="stagingBufferPool">
    /// Optional — if provided, emits <see cref="GaugeId.WalStagingPoolRented"/> / <see cref="GaugeId.WalStagingPoolPeakRented"/> /
    /// <see cref="GaugeId.WalStagingTotalRentsCumulative"/>, plus <see cref="GaugeId.WalStagingPoolCapacity"/> on the first call
    /// only. Pass <c>null</c> to skip the staging-pool group.
    /// </param>
    /// <param name="transientBytesUsed">
    /// Byte count currently used across all transient stores. Emitted as <see cref="GaugeId.TransientStoreBytesUsed"/> only when
    /// strictly greater than zero; pass <c>0</c> to skip. No capacity counterpart is emitted — the transient store has no fixed
    /// ceiling (see the rationale comment inside the method body).
    /// </param>
    /// <param name="firstSnapshotEmitted">
    /// In/out. Callers initialize this to <c>false</c> and pass the same variable into every subsequent call. The helper emits
    /// fixed-at-init capacity gauges only when this is <c>false</c> on entry, then sets it to <c>true</c>.
    /// </param>
    /// <remarks>
    /// <b>Visibility:</b> internal because <see cref="TransactionChain"/> and <see cref="UowRegistry"/> are internal-visibility
    /// engine types. External callers use the <c>EmitSnapshot(uint, DatabaseEngine, ref bool)</c> convenience overload, which
    /// reaches into a <see cref="DatabaseEngine"/> handle to pull the right subsystem references and forwards to this method.
    /// </remarks>
    internal static void EmitSnapshot(uint tickNumber, MemoryAllocator memoryAllocator, PagedMMF pagedMmf, TransactionChain txChain, UowRegistry uowRegistry,
        WalManager walManager, StagingBufferPool stagingBufferPool, long transientBytesUsed, ref bool firstSnapshotEmitted)
    {
        if (!TelemetryConfig.ProfilerGaugesActive)
        {
            return;
        }

        Span<GaugeValue> values = stackalloc GaugeValue[64];
        int n = 0;

        // ── Unmanaged memory ──
        if (memoryAllocator != null)
        {
            values[n++] = GaugeValue.FromU64(GaugeId.MemoryUnmanagedTotalBytes, (ulong)memoryAllocator.PinnedBytes);
            values[n++] = GaugeValue.FromU64(GaugeId.MemoryUnmanagedPeakBytes, (ulong)memoryAllocator.PeakPinnedBytes);
            values[n++] = GaugeValue.FromU32(GaugeId.MemoryUnmanagedLiveBlocks, (uint)memoryAllocator.PinnedLiveBlocks);
        }

        // ── GC heap (always sampled — uses GC.GetGCMemoryInfo, no dependency on engine state) ──
        var gcInfo = GC.GetGCMemoryInfo();
        var gen = gcInfo.GenerationInfo;
        if (gen.Length > 0)
        {
            values[n++] = GaugeValue.FromU64(GaugeId.GcHeapGen0Bytes, (ulong)gen[0].SizeAfterBytes);
        }

        if (gen.Length > 1)
        {
            values[n++] = GaugeValue.FromU64(GaugeId.GcHeapGen1Bytes, (ulong)gen[1].SizeAfterBytes);
        }

        if (gen.Length > 2)
        {
            values[n++] = GaugeValue.FromU64(GaugeId.GcHeapGen2Bytes, (ulong)gen[2].SizeAfterBytes);
        }

        if (gen.Length > 3)
        {
            values[n++] = GaugeValue.FromU64(GaugeId.GcHeapLohBytes, (ulong)gen[3].SizeAfterBytes);
        }

        if (gen.Length > 4)
        {
            values[n++] = GaugeValue.FromU64(GaugeId.GcHeapPohBytes, (ulong)gen[4].SizeAfterBytes);
        }

        values[n++] = GaugeValue.FromU64(GaugeId.GcHeapCommittedBytes, (ulong)gcInfo.TotalCommittedBytes);

        // ── Page cache ──
        if (pagedMmf != null)
        {
            var pc = pagedMmf.GetGaugeSnapshot();
            if (!firstSnapshotEmitted)
            {
                values[n++] = GaugeValue.FromU32(GaugeId.PageCacheTotalPages, (uint)pc.TotalPages);
            }
            values[n++] = GaugeValue.FromU32(GaugeId.PageCacheFreePages, (uint)pc.FreePages);
            values[n++] = GaugeValue.FromU32(GaugeId.PageCacheCleanUsedPages, (uint)pc.CleanUsedPages);
            values[n++] = GaugeValue.FromU32(GaugeId.PageCacheDirtyUsedPages, (uint)pc.DirtyUsedPages);
            values[n++] = GaugeValue.FromU32(GaugeId.PageCacheExclusivePages, (uint)pc.ExclusivePages);
            values[n++] = GaugeValue.FromU32(GaugeId.PageCacheEpochProtectedPages, (uint)pc.EpochProtectedPages);
            values[n++] = GaugeValue.FromU32(GaugeId.PageCachePendingIoReads, (uint)pc.PendingIoReads);

            // Phase 5 — primary database FileSize gauge. Replaces the per-CAS TrackFileGrowth event spam
            // with a single end-of-tick reading; viewer plots a smooth growth curve.
            values[n++] = GaugeValue.FromU64(GaugeId.PageCacheFileSizeBytes, (ulong)pagedMmf.FileSize);
        }

        // ── Transaction chain ──
        if (txChain != null)
        {
            values[n++] = GaugeValue.FromU32(GaugeId.TxChainActiveCount, (uint)txChain.ActiveCount);
            values[n++] = GaugeValue.FromU32(GaugeId.TxChainPoolSize, (uint)txChain.PoolCount);
            // Cumulative throughput counters — viewer subtracts consecutive snapshots to get per-tick commit/rollback rate.
            values[n++] = GaugeValue.FromU64(GaugeId.TxChainCommitTotal, (ulong)txChain.CommitTotal);
            values[n++] = GaugeValue.FromU64(GaugeId.TxChainRollbackTotal, (ulong)txChain.RollbackTotal);
            values[n++] = GaugeValue.FromU64(GaugeId.TxChainCreatedTotal, (ulong)txChain.CreatedTotal);
        }

        // ── UoW registry ──
        if (uowRegistry != null)
        {
            values[n++] = GaugeValue.FromU32(GaugeId.UowRegistryActiveCount, (uint)uowRegistry.ActiveCount);
            values[n++] = GaugeValue.FromU32(GaugeId.UowRegistryVoidCount, (uint)uowRegistry.VoidEntryCount);
            values[n++] = GaugeValue.FromU64(GaugeId.UowRegistryCreatedTotal, (ulong)uowRegistry.CreatedTotal);
            values[n++] = GaugeValue.FromU64(GaugeId.UowRegistryCommittedTotal, (ulong)uowRegistry.CommittedTotal);
        }

        // ── WAL commit buffer + staging pool ──
        // WalManager is null when WAL is disabled; skip the whole block in that case. Capacity gauges (BufferCapacity, PoolCapacity)
        // are fixed-at-init — emit only on the first snapshot to keep wire volume trim; the viewer caches them from first observation.
        if (walManager != null)
        {
            var cb = walManager.CommitBuffer;
            if (cb != null)
            {
                values[n++] = GaugeValue.FromU64(GaugeId.WalCommitBufferUsedBytes, (ulong)cb.UsedBytes);
                values[n++] = GaugeValue.FromU32(GaugeId.WalInflightFrames, (uint)cb.InflightCount);
                if (!firstSnapshotEmitted)
                {
                    values[n++] = GaugeValue.FromU64(GaugeId.WalCommitBufferCapacityBytes, (ulong)cb.BufferCapacity);
                }
            }
        }
        if (stagingBufferPool != null)
        {
            values[n++] = GaugeValue.FromU32(GaugeId.WalStagingPoolRented, (uint)stagingBufferPool.CurrentRents);
            values[n++] = GaugeValue.FromU32(GaugeId.WalStagingPoolPeakRented, (uint)stagingBufferPool.PeakRents);
            values[n++] = GaugeValue.FromU64(GaugeId.WalStagingTotalRentsCumulative, (ulong)stagingBufferPool.TotalRents);
            if (!firstSnapshotEmitted)
            {
                values[n++] = GaugeValue.FromU32(GaugeId.WalStagingPoolCapacity, (uint)stagingBufferPool.PoolCapacity);
            }
        }

        // ── Transient store ──
        // Aggregated across all live TransientStore instances (component-table stores + archetype-cluster stores). A value of
        // 0 means either no transient storage is in use OR the caller didn't supply a DatabaseEngine; either way we skip the
        // gauge. The cap is intentionally NOT emitted: TransientOptions.MaxMemoryBytes is a per-store budget, and exposing it
        // as a single reference line against an aggregated "used" series would mislead (50% full in aggregate can still mean
        // a single store is saturated). The renderer auto-scales the Y axis when no capacity gauge is present.
        if (transientBytesUsed > 0)
        {
            values[n++] = GaugeValue.FromU64(GaugeId.TransientStoreBytesUsed, (ulong)transientBytesUsed);
        }

        TyphonEvent.EmitPerTickSnapshot(tickNumber, Stopwatch.GetTimestamp(), 0u, values[..n]);

        firstSnapshotEmitted = true;
    }

    /// <summary>
    /// Convenience overload for callers that already have a <see cref="DatabaseEngine"/> handle. Extracts the <see cref="MemoryAllocator"/> concrete type
    /// (if the injected allocator is that type) and the <c>MMF</c> page-cache reference, then delegates to the primary overload. Intended for standalone
    /// runners like <c>Typhon.IOProfileRunner</c> that don't use <c>TyphonRuntime</c>'s scheduler callback wiring.
    /// </summary>
    public static void EmitSnapshot(uint tickNumber, DatabaseEngine engine, ref bool firstSnapshotEmitted)
    {
        if (engine == null)
        {
            return;
        }
        var allocator = engine.MemoryAllocator as MemoryAllocator;
        EmitSnapshot(tickNumber, allocator, engine.MMF, engine.TransactionChain, engine.UowRegistry, engine.WalManager, engine.StagingBufferPool, 
            engine.GetTransientBytesTotal(), ref firstSnapshotEmitted);
    }
}
