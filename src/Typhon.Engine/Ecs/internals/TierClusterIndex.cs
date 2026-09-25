using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-archetype index of active cluster chunk IDs grouped by the <see cref="SimTier"/> of their cell (issue #231).
/// Rebuilt from <see cref="ArchetypeClusterState.ActiveClusterIds"/> + <see cref="ArchetypeClusterState.ClusterCellMap"/>
/// + <see cref="SpatialGrid"/> cell tier bytes.
/// </summary>
/// <remarks>
/// <para><b>Dual invalidation</b>: the rebuild is skipped when both (a) the engine-wide <see cref="RealmTable.TierVersion"/> (every realm grid bumps it)
/// hasn't changed since the last rebuild
/// (no <c>SetCellTier</c> call actually flipped a cell tier) AND (b) the archetype's own cluster-set version hasn't changed (no spawn, destroy, or migration
/// added/removed clusters from the active list). In steady state (camera stationary, no cell-crossing migrations) the rebuild is a no-op and the tier query
/// returns the cached result.</para>
/// <para><b>Memory layout</b>: per-tier <c>int[]</c> buffers are kept alive between rebuilds and reused. Buffers are allocated lazily on first insertion, so
/// empty tiers cost zero memory. Growth is doubling. The total footprint is proportional to the archetype's active cluster count, not the grid size. For a
/// 10K-cluster archetype with all clusters in Tier 0: ~12KB. For a uniform distribution: 4 × ~3KB ≈ 12KB. Both fit comfortably in L1.</para>
/// <para><b>Amortization</b>: NOT handled here. Cell-level amortization is per-system data — different systems on the same archetype can request different
/// denominators. <c>TyphonRuntime</c> computes the active amortization bucket per-system in <c>OnParallelQueryPrepare</c> by striding the appropriate per-tier
/// list (with index-modulo bucketing for uniform distribution regardless of cell-key encoding), so no shared mutable state is involved.</para>
/// <para><b>Thread-safety</b>: NOT thread-safe. The runtime calls <see cref="RebuildIfStale"/> from a single dedicated tick-start phase
/// (<c>TyphonRuntime.BuildTierIndexesAtTickStart</c>) before any parallel system dispatch begins. After tick-start completes, all readers (parallel system
/// prepare callbacks, change-filter scans, view materialization) only READ the prepared per-tier arrays — they never trigger a rebuild.</para>
/// </remarks>
internal sealed class TierClusterIndex
{
    // Per-tier cluster lists. _tierClusters[tierIdx] holds [_tierClusterCounts[tierIdx]] valid entries. Lazy-allocated per-tier so empty tiers cost zero memory.
    private readonly int[][] _tierClusters = new int[TierExtensions.TierCount][];
    private readonly int[] _tierClusterCounts = new int[TierExtensions.TierCount];

    // Multi-tier merge cache, keyed by the combined SimTier flag value (0..15). Lazily allocated on first multi-tier query.
    // Counts use -1 as "not yet built" sentinel; 0 is a valid cached result meaning "empty". Backing arrays survive across rebuilds and grow on demand.
    private readonly int[][] _mergedCache = new int[16][];
    private readonly int[] _mergedCounts = new int[16];

    // Invalidation counters captured at the last rebuild. -1 forces a rebuild on the first call.
    private int _lastGridTierVersion = -1;
    private int _lastClusterSetVersion = -1;

    // Realms D1: the runnable-set stamp (RealmDispatchIndex.Stamp) at the last rebuild; clusters of non-runnable realms are left out of every tier list.
    private int _lastDispatchStamp = -1;

    // Telemetry — incremented each time a full rebuild runs. Tests read this to assert the version-skip fast path works.
    internal int RebuildCount { get; private set; }

    // Thread-safety canary: detects concurrent Rebuild calls (which would corrupt internal buffers). The runtime hoists all Rebuild calls to single-threaded
    // TickStart; this assert catches regressions that re-introduce concurrent paths.
    private int _rebuildInProgress;

    /// <summary>Full rebuild from the archetype's active clusters — unconditional. Used by tests and by
    /// <see cref="RebuildIfStale"/> after the version check.</summary>
    /// <param name="state">The archetype whose clusters are grouped.</param>
    /// <param name="tierVersion">The engine-wide tier version (<see cref="RealmTable.TierVersion"/>) read by the caller before this rebuild: every realm's
    /// tier changes move it, and it is stamped as the version this list reflects.</param>
    /// <remarks>Each cluster's tier is its cell's in the cluster's OWN realm (Realms C1): a cell key names a cell in every realm.</remarks>
    public void Rebuild(ArchetypeClusterState state, int tierVersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        Debug.Assert(Interlocked.CompareExchange(ref _rebuildInProgress, 1, 0) == 0,
            "TierClusterIndex.Rebuild called concurrently — this must run single-threaded from BuildTierIndexesAtTickStart.");

        // Phase 3: Spatial:TierIndex:Rebuild span. ClusterCount/NewVersion filled at exit.
        var rebuildScope = TyphonEvent.BeginSpatialTierIndexRebuild((ushort)Math.Min(state.ArchetypeId, ushort.MaxValue));
        rebuildScope.OldVersion = _lastClusterSetVersion;
        try
        {
            // Reset counts (but keep buffers).
            for (int t = 0; t < TierExtensions.TierCount; t++)
            {
                _tierClusterCounts[t] = 0;
            }

            // Invalidate the multi-tier merge cache. Counts set to -1 ("not built"); backing arrays are reused on next query.
            Array.Fill(_mergedCounts, -1);

            var cellMap = state.ClusterCellMap;
            var realmMap = state.ClusterRealmMap;
            var realmSpatial = state.RealmSpatial;
            var gridRealm = -1;
            SpatialGrid grid = null;

            // Read the version BEFORE the walk, and record THIS value at the end rather than re-reading it there. Recorded after, a spawn landing mid-walk
            // bumps the version, is absent from the list this rebuild produced, and is nonetheless covered by the version stamped against it — so
            // RebuildIfStale sees "unchanged" and skips the rebuild that would have picked it up, serving a tier list missing that cluster for the rest of
            // the run. Captured first, the same interleaving leaves the stamp behind the live version, which merely costs one redundant rebuild. Making
            // the counter Interlocked fixes lost updates; it does nothing about this, and the two were easy to mistake for one problem.
            var versionAtWalk = state.ClusterSetVersion;

            // CLUSTERWALK-02: count first, then the array, through the one reader. Loading the array first — which this did — pairs an old array with a
            // count a concurrent spawn has already grown past.
            var activeIds = state.ReadActiveClusterList(out var active);
            var dispatch = state.RealmDispatch;
            var excludeDormant = dispatch is { Filtering: true };
            for (int i = 0; i < active; i++)
            {
                int chunkId = activeIds[i];
                if (cellMap == null || chunkId >= cellMap.Length)
                {
                    continue;
                }

                // Realms D1: a cluster of a non-runnable realm is in no tier list, so no tier system dispatches it.
                if (excludeDormant && dispatch.IsExcluded(chunkId))
                {
                    continue;
                }
                int cellKey = cellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }

                // The realm map is published before the cell map and grown with it, so a chunk id bounded by the latter indexes the former.
                var realm = realmMap[chunkId];
                if (realm != gridRealm)
                {
                    gridRealm = realm;
                    grid = realmSpatial[realm]?.Grid;
                }

                if (grid == null)
                {
                    continue;
                }

                byte tierByte = grid.GetCell(cellKey).Tier;
                if (tierByte == 0)
                {
                    // Cell tier was never set (or was explicitly cleared via SetCellTier(SimTier.None)). Skip — game code
                    // and tests using tier dispatch must set cell tiers explicitly.
                    continue;
                }

                // tierByte is a SimTier flag value. SetCellTier enforces the single-bit invariant, so TZCNT maps directly
                // to the array index.
                Debug.Assert(BitOperations.PopCount((uint)tierByte) == 1,
                    $"Cell tier byte has multiple bits set ({tierByte:X2}) — SetCellTier should enforce single-bit invariant.");
                int tierIdx = BitOperations.TrailingZeroCount((uint)tierByte);
                ref var buf = ref _tierClusters[tierIdx];
                int cnt = _tierClusterCounts[tierIdx];
                if (buf == null)
                {
                    // Lazy per-tier allocation: only pay for tiers that actually have clusters.
                    buf = new int[16];
                }
                else if (cnt >= buf.Length)
                {
                    Array.Resize(ref buf, buf.Length * 2);
                }
                buf[cnt] = chunkId;
                _tierClusterCounts[tierIdx] = cnt + 1;
            }

            _lastGridTierVersion = tierVersion;
            _lastClusterSetVersion = versionAtWalk;
            _lastDispatchStamp = dispatch?.Stamp ?? -1;
            RebuildCount++;

            // Sum cluster counts across all tiers for the span payload.
            int total = 0;
            for (int t = 0; t < TierExtensions.TierCount; t++)
            {
                total += _tierClusterCounts[t];
            }

            rebuildScope.ClusterCount = total;
            // The same captured value, not a second Volatile.Read: two reads of a now-volatile counter can differ, which would make the telemetry pair
            // (OldVersion, NewVersion) describe a window this rebuild did not actually cover.
            rebuildScope.NewVersion = versionAtWalk;
        }
        finally
        {
            rebuildScope.Dispose();
            Interlocked.Exchange(ref _rebuildInProgress, 0);
        }
    }

    /// <summary>Rebuild only when the grid tier version or the archetype's cluster-set version has changed since the
    /// last rebuild. In steady state this is two int compares — effectively free.</summary>
    public void RebuildIfStale(ArchetypeClusterState state, int tierVersion)
    {
        if (tierVersion == _lastGridTierVersion && state.ClusterSetVersion == _lastClusterSetVersion
            && (state.RealmDispatch?.Stamp ?? -1) == _lastDispatchStamp)
        {
            // Phase 3: Spatial:TierIndex:VersionSkip instant — fast path, no rebuild needed.
            // reason: 0=both unchanged, 1=grid only, 2=cluster set only (here both unchanged).
            TyphonEvent.EmitSpatialTierIndexVersionSkip((ushort)Math.Min(state.ArchetypeId, ushort.MaxValue), _lastClusterSetVersion, 0);
            return;
        }
        Rebuild(state, tierVersion);
    }

    /// <summary>
    /// Get the raw backing array + count for a <see cref="SimTier"/> flag. Lets callers (primarily <c>TyphonRuntime</c>) hand the array to downstream consumers
    /// that need an <c>int[]</c> (e.g. <c>TickContext.ClusterIds</c>, <c>ClusterEnumerator.CreateScoped</c>) without an intermediate copy.
    /// </summary>
    /// <remarks>
    /// <para>The returned array may have <c>Length &gt; count</c> — callers must respect <paramref name="count"/> to avoid reading stale trailing entries.
    /// The array's identity can change between rebuilds, so callers must re-fetch it after any <see cref="RebuildIfStale"/> that actually runs the rebuild.</para>
    /// </remarks>
    public int[] GetClustersArray(SimTier tier, out int count)
    {
        if (tier == SimTier.None)
        {
            count = 0;
            return Array.Empty<int>();
        }

        int popCount = BitOperations.PopCount((byte)tier);
        if (popCount == 1)
        {
            int tierIdx = tier.ToIndex();
            count = _tierClusterCounts[tierIdx];
            return _tierClusters[tierIdx] ?? Array.Empty<int>();
        }

        // Multi-tier merge: ensure the cache entry is fresh, then return it.
        int key = (byte)tier;
        if (_mergedCounts[key] < 0)
        {
            BuildMergedEntry(tier, key);
        }
        count = _mergedCounts[key];
        return _mergedCache[key] ?? Array.Empty<int>();
    }

    /// <summary>Get the cluster chunk ids for a single-bit <see cref="SimTier"/> flag. For multi-bit flags (e.g. <see cref="SimTier.Near"/>),
    /// this method merges the per-tier lists into a cached buffer.</summary>
    public ReadOnlySpan<int> GetClusters(SimTier tier)
    {
        var arr = GetClustersArray(tier, out int count);
        return new ReadOnlySpan<int>(arr, 0, count);
    }

    /// <summary>
    /// The read-only form of <see cref="GetClustersArray"/> for dispatch (RT-1): a single tier, or a multi-tier set whose merged entry tick start already
    /// built. Returns false — and fills nothing — for a multi-tier set not built since the last rebuild, so concurrent readers never write the shared cache.
    /// </summary>
    public bool TryGetPreparedClusters(SimTier tier, out int[] ids, out int count)
    {
        if (tier == SimTier.None || BitOperations.PopCount((byte)tier) == 1)
        {
            ids = GetClustersArray(tier, out count);
            return true;
        }

        var key = (byte)tier;
        count = _mergedCounts[key];
        if (count < 0)
        {
            ids = null;
            count = 0;
            return false;
        }

        ids = _mergedCache[key] ?? [];
        return true;
    }

    /// <summary>How many clusters <see cref="CopyClusters"/> writes for <paramref name="tier"/>.</summary>
    public int CountClusters(SimTier tier)
    {
        var total = 0;
        for (var t = 0; t < TierExtensions.TierCount; t++)
        {
            if ((((byte)tier >> t) & 1) != 0)
            {
                total += _tierClusterCounts[t];
            }
        }

        return total;
    }

    /// <summary>Concatenates the per-tier lists of <paramref name="tier"/> into a caller-owned <paramref name="destination"/>; returns the count.</summary>
    public int CopyClusters(SimTier tier, int[] destination)
    {
        var offset = 0;
        for (var t = 0; t < TierExtensions.TierCount; t++)
        {
            var count = _tierClusterCounts[t];
            if ((((byte)tier >> t) & 1) == 0 || count == 0)
            {
                continue;
            }

            Array.Copy(_tierClusters[t], 0, destination, offset, count);
            offset += count;
        }

        return offset;
    }

    private void BuildMergedEntry(SimTier tier, int key)
    {
        // Compute total size.
        int total = 0;
        byte mask = (byte)tier;
        for (int t = 0; t < TierExtensions.TierCount; t++)
        {
            if (((mask >> t) & 1) != 0)
            {
                total += _tierClusterCounts[t];
            }
        }

        if (total == 0)
        {
            // Mark cache hit with count=0 (empty result). GetClustersArray returns Array.Empty.
            _mergedCounts[key] = 0;
            return;
        }

        if (_mergedCache[key] == null || _mergedCache[key].Length < total)
        {
            _mergedCache[key] = new int[Math.Max(16, total)];
        }

        int offset = 0;
        for (int t = 0; t < TierExtensions.TierCount; t++)
        {
            if (((mask >> t) & 1) == 0)
            {
                continue;
            }
            int cnt = _tierClusterCounts[t];
            if (cnt == 0)
            {
                continue;
            }
            Array.Copy(_tierClusters[t], 0, _mergedCache[key], offset, cnt);
            offset += cnt;
        }
        _mergedCounts[key] = total;
    }
}
