using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-region configuration stored contiguously for cache-friendly iteration.
/// 64 bytes — fits in one x86 cache line.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SpatialRegionConfig
{
    public double MinX, MinY, MinZ;
    public double MaxX, MaxY, MaxZ;
    public uint CategoryMask;
    public byte EvaluationFrequency;
    public TargetTreeMode TargetTree;
    public byte Active;  // 0=destroyed/free, 1=active
    public byte _pad;
    public int Generation;
    public int LastEvaluatedTick;
}

/// <summary>
/// Per-region mutable occupant state. Separated from config for locality (config is read-only during eval scan).
/// </summary>
internal sealed class RegionOccupantState
{
    /// <summary>Previous occupant bitmap indexed by componentChunkId. From ArrayPool.</summary>
    internal long[] PreviousBitmap;
    internal int PreviousWordCount;

    /// <summary>Previous entity lookup: dense chunkId → entityId, for resolving "left" entity IDs.</summary>
    internal long[] PreviousEntityLookup;
    internal int PreviousEntityLookupSize;

    /// <summary>Static tree cache bitmap (null until first Both/StaticOnly eval).</summary>
    internal long[] StaticCacheBitmap;
    internal int StaticCacheWordCount;

    /// <summary>Static tree entity lookup: chunkId → entityId for cached static results.</summary>
    internal long[] StaticCacheEntityLookup;
    internal int StaticCacheEntityLookupSize;

    /// <summary>MutationVersion of the static tree when cache was built. -1 = invalidated.</summary>
    internal int StaticCacheVersion;

    /// <summary>Previous cluster occupants tracked by EntityId (separate from bitmap to avoid namespace collision).</summary>
    internal HashSet<long> PreviousClusterOccupants;

    /// <summary>Scratch set for current-tick cluster occupants. Double-buffered with <see cref="PreviousClusterOccupants"/> to avoid
    /// per-evaluation allocation. After the diff, the sets are swapped.</summary>
    internal HashSet<long> ClusterOccupantsScratch;
}

/// <summary>
/// External trigger volume system for a single ComponentTable's spatial index.
/// Detects enter/leave/stay transitions by querying the R-Tree and diffing against per-region occupant bitmaps.
/// Zero-allocation on the hot path. ~800ns per region evaluation at 50 occupants.
/// </summary>
internal sealed unsafe class SpatialTriggerSystem
{
    // Region storage — flat array with free-list
    private SpatialRegionConfig[] _configs;
    private RegionOccupantState[] _occupants;
    private int _capacity;
    private int _activeCount;
    private int _freeHead; // index of first free slot, -1 = none

    // Per-evaluation scratch buffers (shared across all regions, cleared between)
    private long[] _scratchBitmap;
    private long[] _entityLookup;     // dense: chunkId → entityId (current eval)
    private long[] _prevEntityLookup; // dense: chunkId → entityId (previous eval, swapped)

    // Result buffers (pre-allocated, sliced for SpatialTriggerResult)
    private long[] _resultEntered;
    private long[] _resultLeft;

    // Owner references
    private readonly ComponentTable _table;
    private readonly SpatialIndexState _spatialState;

    private const int InitialCapacity = 8;
    private const int InitialResultCapacity = 256;

    internal SpatialTriggerSystem(ComponentTable table, SpatialIndexState spatialState)
    {
        _table = table;
        _spatialState = spatialState;
        _configs = new SpatialRegionConfig[InitialCapacity];
        _occupants = new RegionOccupantState[InitialCapacity];
        _capacity = InitialCapacity;
        _freeHead = -1;
        _resultEntered = new long[InitialResultCapacity];
        _resultLeft = new long[InitialResultCapacity];
    }

    internal int ActiveRegionCount => _activeCount;

    // ── Region CRUD ──────────────────────────────────────────────────────

    public SpatialRegionHandle CreateRegion(ReadOnlySpan<double> bounds, uint categoryMask = 0, byte evaluationFrequency = 1, 
        TargetTreeMode targetTree = TargetTreeMode.DynamicOnly)
    {
        if (evaluationFrequency == 0)
        {
            evaluationFrequency = 1;
        }

        int index;
        if (_freeHead >= 0)
        {
            index = _freeHead;
            // Next free slot is stored in Generation field (repurposed while inactive)
            _freeHead = _configs[index].Generation;
        }
        else
        {
            if (_activeCount >= _capacity)
            {
                Grow();
            }
            index = _activeCount;
        }

        int coordCount = _spatialState.Descriptor.CoordCount;
        int halfCoord = coordCount / 2;

        ref var config = ref _configs[index];
        config.MinX = bounds.Length > 0 ? bounds[0] : 0;
        config.MinY = bounds.Length > 1 ? bounds[1] : 0;
        config.MinZ = halfCoord == 3 && bounds.Length > 2 ? bounds[2] : 0;
        config.MaxX = bounds.Length > halfCoord ? bounds[halfCoord] : 0;
        config.MaxY = bounds.Length > halfCoord + 1 ? bounds[halfCoord + 1] : 0;
        config.MaxZ = halfCoord == 3 && bounds.Length > halfCoord + 2 ? bounds[halfCoord + 2] : 0;
        config.CategoryMask = categoryMask;
        config.EvaluationFrequency = evaluationFrequency;
        config.TargetTree = targetTree;
        config.Active = 1;
        config.Generation++;
        config.LastEvaluatedTick = int.MinValue; // force evaluation on first tick

        _occupants[index] = new RegionOccupantState { StaticCacheVersion = -1 };
        _activeCount++;

        TyphonEvent.EmitSpatialTriggerRegion(0, (ushort)index, categoryMask);
        return new SpatialRegionHandle(index, config.Generation);
    }

    public void DestroyRegion(SpatialRegionHandle handle)
    {
        ValidateHandle(handle);

        ref var config = ref _configs[handle.Index];
        TyphonEvent.EmitSpatialTriggerRegion(1, (ushort)handle.Index, config.CategoryMask);
        config.Active = 0;

        // Return pooled arrays
        var occ = _occupants[handle.Index];
        if (occ.PreviousBitmap != null)             { ArrayPool<long>.Shared.Return(occ.PreviousBitmap); }  
        if (occ.PreviousEntityLookup != null)       { ArrayPool<long>.Shared.Return(occ.PreviousEntityLookup); }
        if (occ.StaticCacheBitmap != null)          { ArrayPool<long>.Shared.Return(occ.StaticCacheBitmap); }
        if (occ.StaticCacheEntityLookup != null)    { ArrayPool<long>.Shared.Return(occ.StaticCacheEntityLookup); }
        _occupants[handle.Index] = null;

        // Push to free list (store next-free in Generation field)
        config.Generation = _freeHead;
        _freeHead = handle.Index;
        _activeCount--;
    }

    public void UpdateRegionBounds(SpatialRegionHandle handle, ReadOnlySpan<double> newBounds)
    {
        ValidateHandle(handle);

        int coordCount = _spatialState.Descriptor.CoordCount;
        int halfCoord = coordCount >> 1;

        ref var config = ref _configs[handle.Index];
        config.MinX = newBounds.Length > 0 ? newBounds[0] : 0;
        config.MinY = newBounds.Length > 1 ? newBounds[1] : 0;
        config.MinZ = halfCoord == 3 && newBounds.Length > 2 ? newBounds[2] : 0;
        config.MaxX = newBounds.Length > halfCoord ? newBounds[halfCoord] : 0;
        config.MaxY = newBounds.Length > halfCoord + 1 ? newBounds[halfCoord + 1] : 0;
        config.MaxZ = halfCoord == 3 && newBounds.Length > halfCoord + 2 ? newBounds[halfCoord + 2] : 0;

        // Invalidate static cache
        var occ = _occupants[handle.Index];
        occ?.StaticCacheVersion = -1;
    }

    public void UpdateRegionCategoryMask(SpatialRegionHandle handle, uint newMask)
    {
        ValidateHandle(handle);
        _configs[handle.Index].CategoryMask = newMask;

        // Invalidate static cache (category change affects static results)
        var occ = _occupants[handle.Index];
        occ?.StaticCacheVersion = -1;
    }

    // ── Evaluation ───────────────────────────────────────────────────────

    /// <summary>
    /// Evaluate a single region. Returns enter/leave/stay results. The result spans are valid until the next EvaluateRegion call.
    /// </summary>
    public SpatialTriggerResult EvaluateRegion(SpatialRegionHandle handle, int currentTick)
    {
        ValidateHandle(handle);

        ref var config = ref _configs[handle.Index];

        // Frequency gating (LastEvaluatedTick == int.MinValue means never evaluated — always pass)
        if (config.LastEvaluatedTick != int.MinValue && currentTick - config.LastEvaluatedTick < config.EvaluationFrequency)
        {
            return SpatialTriggerResult.Skipped;
        }
        config.LastEvaluatedTick = currentTick;

        // Phase 3: Spatial:Trigger:Eval span. Stats filled at exit.
        var evalScope = TyphonEvent.BeginSpatialTriggerEval((ushort)Math.Min(handle.Index, ushort.MaxValue));
        try
        {
            var occ = _occupants[handle.Index];
            int coordCount = _spatialState.Descriptor.CoordCount;

            // Estimate max chunkId from component segment allocation count
            int maxChunkId = _table.ComponentSegment.AllocatedChunkCount;

            int wordCount = (maxChunkId + 63) >> 6;
            if (wordCount == 0)
            {
                wordCount = 1;
            }

            EnsureScratchCapacity(wordCount, maxChunkId);

            // Clear scratch bitmap
            Array.Clear(_scratchBitmap, 0, wordCount);

            // Build query coords
            Span<double> queryCoords = stackalloc double[coordCount];
            BuildQueryCoords(in config, queryCoords, coordCount);

            // Query tree(s) and populate scratch bitmap + entity lookup
            HashSet<long> clusterOccupants = null;
            var guard = EpochGuard.Enter(_table.DBE.EpochManager);
            try
            {
                // Dynamic tree
                if (config.TargetTree != TargetTreeMode.StaticOnly && _spatialState.DynamicTree != null)
                {
                    QueryAndPopulateBitmap(_spatialState.DynamicTree, queryCoords, config.CategoryMask, wordCount);
                }

                // Per-archetype cluster spatial index fan-out (issue #230 Phase 3 Option B).
                // Cluster entities are tracked by EntityId in a HashSet, NOT by bitmap, because the per-table bitmap's chunkId namespace is not meaningful for
                // cluster-archetype results (cluster storage has its own clusterChunkId namespace that would collide with the per-table ComponentChunkId namespace).
                // Under Option B, cluster spatial archetypes require a configured SpatialGrid (enforced at init time in DatabaseEngine.InitializeArchetypes). The
                // enumerator's two-pass cell walk visits DynamicIndex and StaticIndex for each cell, so the caller doesn't need to branch on mode.
                if (_spatialState.ClusterArchetypes != null)
                {
                    var grid = _table.DBE.SpatialGrid;
                    float qMinX, qMinY, qMinZ, qMaxX, qMaxY, qMaxZ;
                    if (coordCount == 4)
                    {
                        // 2D region — [minX, minY, maxX, maxY]. Use infinite Z bounds so 2D cluster archetypes (which have empty-sentinel Z) and 3D cluster
                        // archetypes (which have meaningful Z) both pass the Z overlap test trivially.
                        qMinX = (float)queryCoords[0];
                        qMinY = (float)queryCoords[1];
                        qMinZ = float.NegativeInfinity;
                        qMaxX = (float)queryCoords[2];
                        qMaxY = (float)queryCoords[3];
                        qMaxZ = float.PositiveInfinity;
                    }
                    else
                    {
                        // 3D region — [minX, minY, minZ, maxX, maxY, maxZ].
                        qMinX = (float)queryCoords[0];
                        qMinY = (float)queryCoords[1];
                        qMinZ = (float)queryCoords[2];
                        qMaxX = (float)queryCoords[3];
                        qMaxY = (float)queryCoords[4];
                        qMaxZ = (float)queryCoords[5];
                    }

                    foreach (var cs in _spatialState.ClusterArchetypes)
                    {
                        if (!cs.SpatialSlot.HasSpatialIndex)
                        {
                            continue;
                        }
                        if (clusterOccupants == null)
                        {
                            // Double-buffer: reuse the scratch set from the previous evaluation cycle to avoid per-call HashSet allocation.
                            clusterOccupants = occ.ClusterOccupantsScratch ?? new HashSet<long>();
                            clusterOccupants.Clear();
                        }
                        foreach (var hit in cs.QueryAabb(grid, qMinX, qMinY, qMinZ, qMaxX, qMaxY, qMaxZ, config.CategoryMask))
                        {
                            clusterOccupants.Add(hit.EntityId);
                        }
                    }
                }

                // Static tree
                if (config.TargetTree != TargetTreeMode.DynamicOnly && _spatialState.StaticTree != null)
                {
                    var staticTree = _spatialState.StaticTree;
                    if (occ.StaticCacheVersion != staticTree.MutationVersion)
                    {
                        // Phase 3: Spatial:Trigger:Cache:Invalidate instant — static-tree mutation observed.
                        TyphonEvent.EmitSpatialTriggerCacheInvalidate((ushort)Math.Min(handle.Index, ushort.MaxValue), occ.StaticCacheVersion, staticTree.MutationVersion);
                        RebuildStaticCache(occ, staticTree, queryCoords, config.CategoryMask, maxChunkId);
                    }
                    // OR static cache into scratch bitmap
                    int staticWords = Math.Min(occ.StaticCacheWordCount, wordCount);
                    for (int w = 0; w < staticWords; w++)
                    {
                        _scratchBitmap[w] |= occ.StaticCacheBitmap[w];
                    }
                    // Merge static entity lookups into current
                    int staticLookupSize = Math.Min(occ.StaticCacheEntityLookupSize, _entityLookup.Length);
                    for (int i = 0; i < staticLookupSize; i++)
                    {
                        if (occ.StaticCacheEntityLookup[i] != 0)
                        {
                            _entityLookup[i] = occ.StaticCacheEntityLookup[i];
                        }
                    }
                }
            }
            finally
            {
                guard.Dispose();
            }

            // Diff: XOR + AND, extract enter/leave
            int enteredCount = 0;
            int leftCount = 0;
            int stayCount = 0;

            int prevWordCount = occ.PreviousWordCount;
            long[] prevBitmap = occ.PreviousBitmap;
            int diffWords = Math.Max(wordCount, prevWordCount);

            for (int w = 0; w < diffWords; w++)
            {
                long curWord = w < wordCount ? _scratchBitmap[w] : 0L;
                long prevWord = prevBitmap != null && w < prevWordCount ? prevBitmap[w] : 0L;
                long diff = curWord ^ prevWord;

                if (diff == 0)
                {
                    stayCount += BitOperations.PopCount((ulong)curWord);
                    continue;
                }

                long entered = diff & curWord;
                long left = diff & prevWord;
                long stayed = curWord & prevWord;
                stayCount += BitOperations.PopCount((ulong)stayed);

                // Extract entered bits → resolve EntityId from current lookup
                while (entered != 0)
                {
                    int bit = BitOperations.TrailingZeroCount((ulong)entered);
                    int chunkId = w * 64 + bit;
                    EnsureResultCapacity(ref _resultEntered, enteredCount);
                    _resultEntered[enteredCount++] = _entityLookup[chunkId];
                    entered &= entered - 1;
                }

                // Extract left bits → resolve EntityId from PREVIOUS lookup
                while (left != 0)
                {
                    int bit = BitOperations.TrailingZeroCount((ulong)left);
                    int chunkId = w * 64 + bit;
                    EnsureResultCapacity(ref _resultLeft, leftCount);
                    long entityId = occ.PreviousEntityLookup != null && chunkId < occ.PreviousEntityLookupSize ? occ.PreviousEntityLookup[chunkId] : 0;
                    _resultLeft[leftCount++] = entityId;
                    left &= left - 1;
                }
            }

            // Cluster entity enter/leave via HashSet diff (avoids bitmap namespace collision with per-table ComponentChunkIds)
            if (clusterOccupants != null || occ.PreviousClusterOccupants != null)
            {
                var prev = occ.PreviousClusterOccupants;
                if (clusterOccupants != null)
                {
                    foreach (long eid in clusterOccupants)
                    {
                        if (prev == null || !prev.Contains(eid))
                        {
                            EnsureResultCapacity(ref _resultEntered, enteredCount);
                            _resultEntered[enteredCount++] = eid;
                        }
                        else
                        {
                            stayCount++;
                        }
                    }
                }
                if (prev != null)
                {
                    foreach (long eid in prev)
                    {
                        if (clusterOccupants == null || !clusterOccupants.Contains(eid))
                        {
                            EnsureResultCapacity(ref _resultLeft, leftCount);
                            _resultLeft[leftCount++] = eid;
                        }
                    }
                }
                // Double-buffer swap: current becomes previous, old previous becomes scratch for next cycle.
                occ.ClusterOccupantsScratch = occ.PreviousClusterOccupants;
                occ.PreviousClusterOccupants = clusterOccupants;
            }

            // Swap entity lookup arrays for next evaluation's "left" resolution
            SwapPreviousState(occ, wordCount, maxChunkId);

            // Phase 3: Spatial:Trigger:Occupant:Diff stats instant (no bitmap, just counts).
            // More precisely: prevCount = stayCount + leftCount; currCount = stayCount + enteredCount.
            TyphonEvent.EmitSpatialTriggerOccupantDiff(
                (ushort)Math.Min(handle.Index, ushort.MaxValue),
                (ushort)Math.Min(stayCount + leftCount, ushort.MaxValue),
                (ushort)Math.Min(stayCount + enteredCount, ushort.MaxValue),
                (ushort)Math.Min(enteredCount, ushort.MaxValue),
                (ushort)Math.Min(leftCount, ushort.MaxValue));

            evalScope.OccupantCount = (ushort)Math.Min(stayCount + enteredCount, ushort.MaxValue);
            evalScope.EnterCount = (ushort)Math.Min(enteredCount, ushort.MaxValue);
            evalScope.LeaveCount = (ushort)Math.Min(leftCount, ushort.MaxValue);

            return new SpatialTriggerResult(_resultEntered.AsSpan(0, enteredCount), _resultLeft.AsSpan(0, leftCount), stayCount);
        }
        finally
        {
            evalScope.Dispose();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateHandle(SpatialRegionHandle handle)
    {
        if ((uint)handle.Index >= (uint)_capacity || _configs[handle.Index].Generation != handle.Generation || _configs[handle.Index].Active == 0)
        {
            throw new ArgumentException($"Invalid or destroyed region handle: {handle}");
        }
    }

    private void Grow()
    {
        int newCapacity = _capacity << 1;
        Array.Resize(ref _configs, newCapacity);
        Array.Resize(ref _occupants, newCapacity);
        _capacity = newCapacity;
    }

    private void EnsureScratchCapacity(int wordCount, int maxChunkId)
    {
        if (_scratchBitmap == null || _scratchBitmap.Length < wordCount)
        {
            if (_scratchBitmap != null)
            {
                ArrayPool<long>.Shared.Return(_scratchBitmap);
            }
            _scratchBitmap = ArrayPool<long>.Shared.Rent(wordCount);
        }
        int lookupSize = maxChunkId + 1;
        if (_entityLookup == null || _entityLookup.Length < lookupSize)
        {
            if (_entityLookup != null)
            {
                ArrayPool<long>.Shared.Return(_entityLookup);
                ArrayPool<long>.Shared.Return(_prevEntityLookup);
            }
            _entityLookup = ArrayPool<long>.Shared.Rent(lookupSize);
            _prevEntityLookup = ArrayPool<long>.Shared.Rent(lookupSize);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildQueryCoords(in SpatialRegionConfig config, Span<double> coords, int coordCount)
    {
        int halfCoord = coordCount >> 1;
        coords[0] = config.MinX;
        coords[1] = config.MinY;
        if (halfCoord == 3)
        {
            coords[2] = config.MinZ;
        }
        coords[halfCoord] = config.MaxX;
        coords[halfCoord + 1] = config.MaxY;
        if (halfCoord == 3)
        {
            coords[halfCoord + 2] = config.MaxZ;
        }
    }

    private void QueryAndPopulateBitmap(SpatialRTree<PersistentStore> tree, ReadOnlySpan<double> queryCoords, uint categoryMask, int wordCount)
    {
        foreach (var hit in tree.QueryAABBOccupants(queryCoords, categoryMask: categoryMask))
        {
            int chunkId = hit.ComponentChunkId;
            int wordIdx = chunkId >> 6;
            if (wordIdx < wordCount)
            {
                _scratchBitmap[wordIdx] |= 1L << (chunkId & 63);
            }
            if (chunkId < _entityLookup.Length)
            {
                _entityLookup[chunkId] = hit.EntityId;
            }
        }
    }

    private void RebuildStaticCache(RegionOccupantState occ, SpatialRTree<PersistentStore> staticTree, ReadOnlySpan<double> queryCoords, uint categoryMask, 
        int maxChunkId)
    {
        int wordCount = (maxChunkId + 63) >> 6;
        if (wordCount == 0)
        {
            wordCount = 1;
        }

        // Ensure cache arrays
        if (occ.StaticCacheBitmap == null || occ.StaticCacheBitmap.Length < wordCount)
        {
            if (occ.StaticCacheBitmap != null)
            {
                ArrayPool<long>.Shared.Return(occ.StaticCacheBitmap);
            }
            occ.StaticCacheBitmap = ArrayPool<long>.Shared.Rent(wordCount);
        }
        Array.Clear(occ.StaticCacheBitmap, 0, wordCount);
        occ.StaticCacheWordCount = wordCount;

        int lookupSize = maxChunkId + 1;
        if (occ.StaticCacheEntityLookup == null || occ.StaticCacheEntityLookup.Length < lookupSize)
        {
            if (occ.StaticCacheEntityLookup != null)
            {
                ArrayPool<long>.Shared.Return(occ.StaticCacheEntityLookup);
            }
            occ.StaticCacheEntityLookup = ArrayPool<long>.Shared.Rent(lookupSize);
        }
        Array.Clear(occ.StaticCacheEntityLookup, 0, lookupSize);
        occ.StaticCacheEntityLookupSize = lookupSize;

        // Query static tree
        foreach (var hit in staticTree.QueryAABBOccupants(queryCoords, categoryMask: categoryMask))
        {
            int chunkId = hit.ComponentChunkId;
            int wordIdx = chunkId >> 6;
            if (wordIdx < wordCount)
            {
                occ.StaticCacheBitmap[wordIdx] |= 1L << (chunkId & 63);
            }
            if (chunkId < lookupSize)
            {
                occ.StaticCacheEntityLookup[chunkId] = hit.EntityId;
            }
        }

        occ.StaticCacheVersion = staticTree.MutationVersion;
    }

    private void SwapPreviousState(RegionOccupantState occ, int wordCount, int maxChunkId)
    {
        // Ensure previous bitmap is sized
        if (occ.PreviousBitmap == null || occ.PreviousBitmap.Length < wordCount)
        {
            if (occ.PreviousBitmap != null)
            {
                ArrayPool<long>.Shared.Return(occ.PreviousBitmap);
            }
            occ.PreviousBitmap = ArrayPool<long>.Shared.Rent(wordCount);
        }

        // Copy scratch → previous (can't swap because scratch is shared across regions)
        Array.Copy(_scratchBitmap, occ.PreviousBitmap, wordCount);
        occ.PreviousWordCount = wordCount;

        // Swap entity lookup for left resolution: current becomes previous, previous becomes current
        // But entity lookup is shared across regions, so we copy instead
        int lookupSize = maxChunkId + 1;
        if (occ.PreviousEntityLookup == null || occ.PreviousEntityLookup.Length < lookupSize)
        {
            if (occ.PreviousEntityLookup != null)
            {
                ArrayPool<long>.Shared.Return(occ.PreviousEntityLookup);
            }
            occ.PreviousEntityLookup = ArrayPool<long>.Shared.Rent(lookupSize);
        }
        Array.Copy(_entityLookup, occ.PreviousEntityLookup, lookupSize);
        occ.PreviousEntityLookupSize = lookupSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureResultCapacity(ref long[] buffer, int count)
    {
        if (count >= buffer.Length)
        {
            Array.Resize(ref buffer, buffer.Length * 2);
        }
    }
}
