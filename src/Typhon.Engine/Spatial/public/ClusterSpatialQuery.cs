using System;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Zero-allocation per-cell cluster AABB query for a single archetype (issue #230). The query expands the requested AABB into the overlapping grid cells,
/// iterates each cell's <see cref="CellSpatialIndex"/> as a linear broadphase, and for each broadphase-hit cluster performs a narrowphase scan over its
/// occupied entity slots.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in.</b> Requires the game to have called <see cref="DatabaseEngine.ConfigureSpatialGrid"/> before <see cref="DatabaseEngine.InitializeArchetypes"/>.
/// Without it, the per-cell index is never populated and querying throws <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// <b>Current scope.</b> 2D and 3D f32 bounds (AABB2F/AABB3F and BSphere2F/BSphere3F); f64 tiers (AABB2D/AABB3D) throw <see cref="NotSupportedException"/>
/// pending a follow-up sub-issue of #228. Queries traverse both the per-cell dynamic and static indexes. No overflow R-Tree — the broadphase is a linear
/// scan over all clusters in each cell, which is optimal for typical AntHill cell populations (≤80 clusters).
/// </para>
/// <para>
/// <b>Implementation.</b> This generic entry point exists to provide the tier-aware public API surface with a JIT-specialized dispatch path per concrete box
/// type. The actual state machine lives on <see cref="AabbClusterEnumerator"/>, which is also consumed directly by engine-internal non-generic consumers via
/// <see cref="ArchetypeClusterState.QueryAabb"/>. Both entry points drive the same iterator — the generic layer adds tier validation; the non-generic layer
/// is used by consumers that iterate cluster archetypes at runtime (<c>SpatialTriggerSystem</c>, <c>SpatialInterestSystem</c>, <c>EcsQuery</c>).
/// </para>
/// <para>
/// <b>Epoch scope.</b> The caller must be inside an <see cref="EpochGuard"/> scope; the enumerator creates a <see cref="ChunkAccessor{TStore}"/> on the
/// cluster segment to read entity bounds during the narrowphase pass.
/// </para>
/// </remarks>
public readonly ref struct ClusterSpatialQuery<TArch> where TArch : Archetype<TArch>, new()
{
    private readonly ArchetypeClusterState _state;
    private readonly SpatialGrid _grid;

    internal ClusterSpatialQuery(ArchetypeClusterState state, SpatialGrid grid)
    {
        _state = state;
        _grid = grid;
    }

    /// <summary>
    /// Query all entities in this archetype whose spatial bounds intersect the axis-aligned box carried by <paramref name="box"/>. The generic type parameter
    /// <typeparamref name="TBox"/> determines the dimensionality and precision of the query region; it must match the archetype's cluster storage tier or
    /// an <see cref="InvalidOperationException"/> is thrown (issue #230 Phase 2.5).
    /// </summary>
    /// <typeparam name="TBox">
    /// One of <see cref="AABB2F"/>, <see cref="AABB3F"/>, <see cref="AABB2D"/>, <see cref="AABB3D"/>. The generic constraint narrows
    /// to <see cref="ISpatialBox"/>, and the JIT specializes this method per concrete <typeparamref name="TBox"/> so each monomorphized version contains only
    /// the code path for its concrete type (all other dispatch branches are dead-code-eliminated at specialization time).
    /// </typeparam>
    /// <param name="box">Query region, passed by <c>in</c> to avoid a defensive copy.</param>
    /// <param name="categoryMask">
    /// Category bitmask; a cluster is skipped if its union mask does not intersect. Pass <see cref="uint.MaxValue"/> (default) to accept every cluster.
    /// </param>
    /// <returns>A zero-allocation enumerator suitable for <c>foreach</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the archetype has no spatial index, or when <typeparamref name="TBox"/>'s tier does not match the archetype's cluster storage tier.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown for f64 <typeparamref name="TBox"/> variants (AABB2D, AABB3D). f32 variants (AABB2F, AABB3F) are fully supported. f64 support is deferred to
    /// a follow-up sub-issue of #228 and will fill in mechanically.
    /// </exception>
    public AabbClusterEnumerator AABB<TBox>(in TBox box, uint categoryMask = uint.MaxValue) where TBox : struct, ISpatialBox
    {
        if (_state == null || !_state.SpatialSlot.HasSpatialIndex)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>: archetype has no spatial index. " +
                "Ensure the archetype has a SpatialIndex field and that ConfigureSpatialGrid was called " +
                "on the engine before InitializeArchetypes.");
        }

        // Tier match: the generic TBox must live in the same (dimensionality × precision) tier as the archetype's storage. This is enforced before we even
        // look at the box contents so error messages are uniform regardless of which concrete TBox was used. Both ToTier and TBoxToTier<TBox> are JIT-folded
        // to constants at specialization time, so this check is effectively a single byte compare.
        var storageTier = _state.SpatialSlot.FieldInfo.FieldType.ToTier();
        var queryTier = SpatialTierExtensions.TBoxToTier<TBox>();
        if (queryTier != storageTier)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>.AABB<{typeof(TBox).Name}>: " +
                $"query tier {queryTier} does not match archetype storage tier {storageTier}. " +
                $"Use the AABB variant matching your archetype's dimensionality and precision " +
                $"(AABB2F / AABB3F / AABB2D / AABB3D).");
        }

        // Dispatch to the concrete read path. Each branch uses Unsafe.As to get a native-precision reference to the underlying struct and reads fields directly.
        // JIT folds all but one branch at specialization time, so each monomorphized version of this method only contains the code for its concrete TBox.
        if (typeof(TBox) == typeof(AABB2F))
        {
            ref var b = ref Unsafe.As<TBox, AABB2F>(ref Unsafe.AsRef(in box));
            // 2D queries against 2D cluster storage: set the Z range to +/- infinity so the Z overlap test trivially passes against any stored Z bounds
            // (2D archetypes leave Z at the Empty sentinel). The unified AabbClusterEnumerator always runs a 3D overlap check, and infinite Z bounds make
            // it a no-op for 2D queries without needing a separate code path.
            return _state.QueryAabb(_grid, b.MinX, b.MinY, float.NegativeInfinity, b.MaxX, b.MaxY, float.PositiveInfinity, categoryMask);
        }
        if (typeof(TBox) == typeof(AABB3F))
        {
            ref var b = ref Unsafe.As<TBox, AABB3F>(ref Unsafe.AsRef(in box));
            return _state.QueryAabb(_grid, b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ, categoryMask);
        }
        if (typeof(TBox) == typeof(AABB2D))
        {
            throw new NotSupportedException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>.AABB<AABB2D>: 2D f64 cluster queries are not yet " +
                "implemented. Deferred to a follow-up sub-issue of #228.");
        }
        if (typeof(TBox) == typeof(AABB3D))
        {
            throw new NotSupportedException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>.AABB<AABB3D>: 3D f64 cluster queries are not yet " +
                "implemented. Deferred to a follow-up sub-issue of #228.");
        }

        // Unreachable under the ISpatialBox constraint + the 4 concrete implementers that exist today. Kept as a safety net: if a future box variant is added
        // to Schema.Definition without updating the dispatch here, the caller gets a targeted error identifying the missing branch.
        throw new NotSupportedException(
            $"ClusterSpatialQuery<{typeof(TArch).Name}>.AABB<{typeof(TBox).Name}>: unknown ISpatialBox type. " +
            "Add a dispatch branch here and update SpatialTierExtensions.TBoxToTier<TBox>().");
    }

    /// <summary>
    /// Query all entities in this archetype whose spatial bounds are within <paramref name="sphere"/>'s radius of its center, using the
    /// closest-point-on-AABB distance semantic. <see cref="ClusterSpatialQueryResult.DistanceSq"/> is populated for each hit, letting callers sort
    /// by distance without re-reading bounds. The archetype must be 2D (tier <c>Tier2F</c>); 3D archetypes use the <see cref="BSphere3F"/> overload.
    /// </summary>
    /// <param name="sphere">Query sphere (center + radius), in world units.</param>
    /// <param name="categoryMask">Category bitmask; a cluster is skipped if its union mask does not intersect. Pass <see cref="uint.MaxValue"/> (default) to accept every cluster.</param>
    public AabbClusterEnumerator Radius(in BSphere2F sphere, uint categoryMask = uint.MaxValue)
    {
        if (_state == null || !_state.SpatialSlot.HasSpatialIndex)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>: archetype has no spatial index. " +
                "Ensure the archetype has a SpatialIndex field and that ConfigureSpatialGrid was called " +
                "on the engine before InitializeArchetypes.");
        }

        var storageTier = _state.SpatialSlot.FieldInfo.FieldType.ToTier();
        if (storageTier != SpatialTier.Tier2F)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>.Radius(BSphere2F): " +
                $"query tier Tier2F does not match archetype storage tier {storageTier}. " +
                "Use the BSphere3F overload for 3D archetypes (or AABB2D/AABB3D follow-ups once f64 cluster queries ship).");
        }

        return _state.QueryRadius(_grid, sphere.CenterX, sphere.CenterY, 0f, sphere.Radius, categoryMask);
    }

    /// <summary>
    /// 3D variant of <see cref="Radius(in BSphere2F, uint)"/>. The archetype must be 3D (tier <c>Tier3F</c>).
    /// </summary>
    public AabbClusterEnumerator Radius(in BSphere3F sphere, uint categoryMask = uint.MaxValue)
    {
        if (_state == null || !_state.SpatialSlot.HasSpatialIndex)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>: archetype has no spatial index. " +
                "Ensure the archetype has a SpatialIndex field and that ConfigureSpatialGrid was called " +
                "on the engine before InitializeArchetypes.");
        }

        var storageTier = _state.SpatialSlot.FieldInfo.FieldType.ToTier();
        if (storageTier != SpatialTier.Tier3F)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>.Radius(BSphere3F): " +
                $"query tier Tier3F does not match archetype storage tier {storageTier}. " +
                "Use the BSphere2F overload for 2D archetypes (or AABB2D/AABB3D follow-ups once f64 cluster queries ship).");
        }

        return _state.QueryRadius(_grid, sphere.CenterX, sphere.CenterY, sphere.CenterZ, sphere.Radius, categoryMask);
    }
}

/// <summary>
/// Result of a cluster spatial query match. Holds the entity id, its location inside the cluster storage (chunk id and slot index), the entity's tight
/// bounds as read by the narrowphase, and — for Radius queries — the squared distance from the query center to the closest point on the entity's AABB.
/// For AABB queries, <see cref="DistanceSq"/> is <c>0</c> and should be ignored.
/// </summary>
public readonly struct ClusterSpatialQueryResult
{
    /// <summary>Entity id of the matched entity.</summary>
    public readonly long EntityId;

    /// <summary>Chunk id of the cluster holding the matched entity, within the archetype's cluster storage segment.</summary>
    public readonly int ClusterChunkId;

    /// <summary>Slot index of the matched entity within its cluster.</summary>
    public readonly int SlotIndex;

    /// <summary>Squared distance from the query center to the closest point on the entity's AABB. Populated by Radius queries; always <c>0</c> for AABB
    /// queries. Used by <see cref="ArchetypeClusterState.QueryNearest"/> for top-k sorting. Issue #230 Phase 3.</summary>
    public readonly float DistanceSq;

    /// <summary>
    /// Minimum X of the entity's tight AABB, as read by the narrowphase. Reading these bounds off the result lets callers skip a second component-table read.
    /// </summary>
    public readonly float MinX;

    /// <summary>Minimum Y of the entity's tight AABB.</summary>
    public readonly float MinY;

    /// <summary>Minimum Z of the entity's tight AABB. For 2D archetypes this reflects the query's Z range (typically an infinity sentinel) and should be ignored.</summary>
    public readonly float MinZ;

    /// <summary>Maximum X of the entity's tight AABB.</summary>
    public readonly float MaxX;

    /// <summary>Maximum Y of the entity's tight AABB.</summary>
    public readonly float MaxY;

    /// <summary>Maximum Z of the entity's tight AABB. For 2D archetypes this reflects the query's Z range (typically an infinity sentinel) and should be ignored.</summary>
    public readonly float MaxZ;

    internal ClusterSpatialQueryResult(long entityId, int clusterChunkId, int slotIndex, float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
        float distanceSq = 0f)
    {
        EntityId = entityId;
        ClusterChunkId = clusterChunkId;
        SlotIndex = slotIndex;
        MinX = minX;
        MinY = minY;
        MinZ = minZ;
        MaxX = maxX;
        MaxY = maxY;
        MaxZ = maxZ;
        DistanceSq = distanceSq;
    }
}

/// <summary>
/// Construction helpers for <see cref="ClusterSpatialQuery{TArch}"/>. Exposed on <see cref="DatabaseEngine"/> so callers can write
/// <c>dbe.ClusterSpatialQuery{TArch}().AABB(...)</c>.
/// </summary>
public static class ClusterSpatialQueryExtensions
{
    /// <summary>
    /// Create a per-cell cluster AABB query for the given archetype.
    /// </summary>
    /// <typeparam name="TArch">The archetype type.</typeparam>
    /// <param name="engine">The database engine.</param>
    /// <returns>A zero-allocation query handle.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the archetype is not cluster-eligible or has no spatial component.
    /// </exception>
    public static ClusterSpatialQuery<TArch> ClusterSpatialQuery<TArch>(this DatabaseEngine engine)
        where TArch : Archetype<TArch>, new()
    {
        var meta = Archetype<TArch>.Metadata;
        if (!meta.IsClusterEligible)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>: archetype is not cluster-eligible.");
        }
        var state = engine._archetypeStates[meta.ArchetypeId].ClusterState;
        if (state == null)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>: archetype has no cluster state.");
        }
        return new ClusterSpatialQuery<TArch>(state, engine.SpatialGrid);
    }
}
