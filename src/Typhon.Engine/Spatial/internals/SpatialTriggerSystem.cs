using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-region configuration stored contiguously for cache-friendly iteration.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SpatialRegionConfig
{
    public double MinX, MinY, MinZ;
    public double MaxX, MaxY, MaxZ;
    public uint CategoryMask;
    public byte EvaluationFrequency;
    public byte Active;  // 0=destroyed/free, 1=active
    public byte _pad0, _pad1;

    /// <summary>
    /// Monotonic per-slot handle generation. <b>Never reused for anything else</b> — see <see cref="NextFree"/>.
    /// </summary>
    public int Generation;

    /// <summary>
    /// Free-list link while <see cref="Active"/> is 0; meaningless while the slot is live.
    /// </summary>
    /// <remarks>
    /// <b>This used to be stored in <see cref="Generation"/>, and that let a destroyed handle validate.</b> Destroy wrote the next-free index over the
    /// generation and create did <c>Generation++</c> on that link, so the counter walked backwards: create 0/1/2, destroy 0 then 1, create again — the reused
    /// slot lands on generation 1, which is exactly the handle the caller was told was dead. The same arithmetic makes <c>default(SpatialRegionHandle)</c>
    /// (index 0, generation 0) reachable as a live handle. Pre-existing; #872 step 13 promoted it to public API, which is what made it worth its own field.
    /// </remarks>
    public int NextFree;

    public int LastEvaluatedTick;
}

/// <summary>
/// Per-region mutable occupant state. Separated from config for locality (config is read-only during the eval scan).
/// </summary>
/// <remarks>
/// <b>Two occupant sets, double-buffered.</b> Occupants are tracked by entity id rather than by a bitmap over component chunk ids. The bitmap form was right
/// for the entity-level R-Tree, whose payload WAS a component chunk id; cluster storage has its own chunk-id namespace that would collide with it, so the
/// cluster path always used sets and the bitmap half went with the tree in #872 step 13.
/// </remarks>
internal sealed class RegionOccupantState
{
    /// <summary>Occupants observed on the previous evaluation of this region.</summary>
    internal HashSet<long> PreviousOccupants;

    /// <summary>Scratch set for the current evaluation. Swapped with <see cref="PreviousOccupants"/> after the diff, so neither is reallocated.</summary>
    internal HashSet<long> OccupantsScratch;
}

/// <summary>
/// External trigger volume system for a single ComponentTable's spatial index.
/// </summary>
/// <remarks>
/// <para>Detects enter / leave / stay transitions by querying the per-cell cluster index for each region's box and diffing the occupant set against the
/// previous evaluation's. Allocation-free on the hot path once both sets have grown to their working size.</para>
/// <para><b>Cluster-only since #872 step 13.</b> Evaluation used to query up to two entity-level R-Trees — a dynamic one every tick and a static one behind a
/// mutation-version cache — populate an occupant bitmap indexed by component chunk id, and XOR it against the previous tick's. Every part of that
/// disappeared with the tree it read: the bitmap, the dense chunk-id-to-entity lookup, the static cache and its invalidation hooks, and the
/// <c>TargetTreeMode</c> selector that chose between the two trees. A cell's static and dynamic halves are both visited by
/// <see cref="ArchetypeClusterState.QueryAabb(SpatialGrid, double, double, double, double, double, double, uint)"/>, so there is nothing left for a caller to
/// select between.</para>
/// </remarks>
internal sealed class SpatialTriggerSystem
{
    // Region storage — flat array with free-list
    private SpatialRegionConfig[] _configs;
    private RegionOccupantState[] _occupants;
    private int _capacity;
    private int _activeCount;
    private int _freeHead; // index of first free slot, -1 = none

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

    public SpatialRegionHandle CreateRegion(ReadOnlySpan<double> bounds, uint categoryMask = 0, byte evaluationFrequency = 1)
    {
        if (evaluationFrequency == 0)
        {
            evaluationFrequency = 1;
        }

        int index;
        if (_freeHead >= 0)
        {
            index = _freeHead;
            _freeHead = _configs[index].NextFree;
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
        config.Active = 1;
        config.Generation++;   // monotonic per slot, so a handle from a previous tenancy can never validate
        config.LastEvaluatedTick = int.MinValue; // force evaluation on first tick

        _occupants[index] = new RegionOccupantState();
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
        _occupants[handle.Index] = null;

        config.NextFree = _freeHead;
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
    }

    public void UpdateRegionCategoryMask(SpatialRegionHandle handle, uint newMask)
    {
        ValidateHandle(handle);
        _configs[handle.Index].CategoryMask = newMask;
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

            Span<double> queryCoords = stackalloc double[coordCount];
            BuildQueryCoords(in config, queryCoords, coordCount);

            // Reuse the previous cycle's discarded set rather than allocating one per evaluation.
            var current = occ.OccupantsScratch ?? [];
            current.Clear();

            var guard = EpochGuard.Enter(_table.DBE.EpochManager);
            try
            {
                CollectClusterOccupants(queryCoords, coordCount, config.CategoryMask, current);
            }
            finally
            {
                guard.Dispose();
            }

            var previous = occ.PreviousOccupants;
            int enteredCount = 0;
            int leftCount = 0;
            int stayCount = 0;

            foreach (long entityId in current)
            {
                if (previous != null && previous.Contains(entityId))
                {
                    stayCount++;
                    continue;
                }

                EnsureResultCapacity(ref _resultEntered, enteredCount);
                _resultEntered[enteredCount++] = entityId;
            }

            if (previous != null)
            {
                foreach (long entityId in previous)
                {
                    if (current.Contains(entityId))
                    {
                        continue;
                    }

                    EnsureResultCapacity(ref _resultLeft, leftCount);
                    _resultLeft[leftCount++] = entityId;
                }
            }

            // Double-buffer swap: current becomes previous, old previous becomes next cycle's scratch.
            occ.OccupantsScratch = previous;
            occ.PreviousOccupants = current;

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

    /// <summary>
    /// Collect every entity of every cluster archetype sharing this spatial component whose bounds overlap the region.
    /// </summary>
    /// <remarks>
    /// A 2D region is widened to infinite Z rather than being given the plane's own coordinates, so that a 2D archetype (whose Z is an empty sentinel) and a
    /// 3D one (whose Z is meaningful) both pass the Z overlap test on a query that did not ask about Z.
    /// </remarks>
    private void CollectClusterOccupants(ReadOnlySpan<double> queryCoords, int coordCount, uint categoryMask, HashSet<long> into)
    {
        var clusterArchetypes = _spatialState.ClusterArchetypes;
        if (clusterArchetypes == null)
        {
            return;
        }

        // No narrowing: the region's coordinates arrive as doubles and QueryAabb takes doubles since #919. The (float) casts that used to sit here were a
        // round trip through a width neither end asked for, and a trigger volume is exactly where it would have been noticed last — a region quantised to
        // ~128-unit steps at 10^9 fires for entities outside it and stays silent for entities inside, with no error either way.
        double qMinX, qMinY, qMinZ, qMaxX, qMaxY, qMaxZ;
        if (coordCount == 4)
        {
            qMinX = queryCoords[0];
            qMinY = queryCoords[1];
            qMinZ = double.NegativeInfinity;
            qMaxX = queryCoords[2];
            qMaxY = queryCoords[3];
            qMaxZ = double.PositiveInfinity;
        }
        else
        {
            qMinX = queryCoords[0];
            qMinY = queryCoords[1];
            qMinZ = queryCoords[2];
            qMaxX = queryCoords[3];
            qMaxY = queryCoords[4];
            qMaxZ = queryCoords[5];
        }

        var grid = _table.DBE.SpatialGrid;
        foreach (var cs in clusterArchetypes)
        {
            if (!cs.SpatialSlot.HasSpatialIndex)
            {
                continue;
            }

            foreach (var hit in cs.QueryAabb(grid, qMinX, qMinY, qMinZ, qMaxX, qMaxY, qMaxZ, categoryMask))
            {
                into.Add(hit.EntityId);
            }
        }
    }

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

    private static void EnsureResultCapacity(ref long[] buffer, int count)
    {
        if (count >= buffer.Length)
        {
            Array.Resize(ref buffer, buffer.Length * 2);
        }
    }
}
