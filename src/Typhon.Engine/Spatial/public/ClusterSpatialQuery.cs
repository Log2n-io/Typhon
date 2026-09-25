using System;
using System.Diagnostics.CodeAnalysis;
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
/// <b>Current scope.</b> All four tiers since #914 — 2D and 3D, f32 and f64 (AABB2F/AABB3F/AABB2D/AABB3D and the matching BSphere variants). Queries
/// traverse both the per-cell dynamic and static indexes. No overflow R-Tree — the broadphase is a linear scan over all clusters in each cell, which is
/// optimal for typical AntHill cell populations (≤80 clusters).
/// </para>
/// <para>
/// <b>Implementation.</b> This generic entry point exists to provide the tier-aware public API surface with a JIT-specialized dispatch path per concrete box
/// type. The actual state machine lives on <see cref="AabbClusterEnumerator"/>, which is also consumed directly by engine-internal non-generic consumers via
/// <see cref="ArchetypeClusterState.QueryAabb"/>. Both entry points drive the same iterator — the generic layer adds tier validation; the non-generic layer
/// is used by consumers that iterate cluster archetypes at runtime (<c>SpatialTriggerSystem</c>, <c>EcsQuery</c>).
/// </para>
/// <para>
/// <b>Epoch scope: call this from a system body and there is nothing to do.</b> The enumerator creates a <see cref="ChunkAccessor{TStore}"/> on the cluster
/// segment to read entity bounds during the narrowphase, so the cluster pages must not be reclaimed under it — and rule RT-01 makes the framework
/// responsible for that: every system body, of every shape, runs inside an epoch scope the dispatcher opened. That is why this no longer names a precondition
/// the caller has no public way to express (#909).
/// </para>
/// <para>
/// The obligation that comes with it: <b>do not block inside a system body</b> (PS-09). The scope pins an epoch for as long as the body runs, and page
/// eviction and view-buffer reclamation wait on the oldest live epoch. A caller that is <i>not</i> a system body — a tool, or a diagnostic run outside the
/// tick — has no scope and cannot open one through the public API; such a caller belongs inside a system.
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
    /// Thrown only for an <see cref="ISpatialBox"/> implementer with no dispatch branch here. All four box types that exist are supported since #914.
    /// </exception>
    /// <remarks>
    /// <b><see cref="MethodImplOptions.AggressiveInlining"/> is load-bearing and was measured, not assumed (#919 AC-6).</b> Adding the two f64 dispatch
    /// branches took this method's IL from 716 to 814 bytes. The JIT decides inlining from IL size <i>before</i> it folds the
    /// <c>typeof(TBox) == typeof(...)</c> tests, so the specialised body that actually runs stayed small while the method stopped being inlined — and
    /// because it RETURNS a <see cref="AabbClusterEnumerator"/> by value, that cost every query one extra copy of a 1.6 KB <c>ref struct</c>. Measured
    /// interleaved against the pre-#914 build: <b>+44 ns on every query</b>, fixed rather than per-cell, which at a 3×3-cell query is a tenth of the whole
    /// thing. The attribute takes it back to +6 ns.
    /// <para>So: <b>if another box variant is added here, re-measure.</b> The branches are free once specialised, but each one spends IL budget that the
    /// inliner counts before it can know that.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AabbClusterEnumerator AABB<TBox>(in TBox box, uint categoryMask = uint.MaxValue) where TBox : struct, ISpatialBox
    {
        if (_state == null || !_state.SpatialSlot.HasSpatialIndex)
        {
            ThrowNoSpatialIndex();
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
            return _state.QueryAabb(_grid, b.MinX, b.MinY, double.NegativeInfinity, b.MaxX, b.MaxY, double.PositiveInfinity, categoryMask);
        }
        if (typeof(TBox) == typeof(AABB3F))
        {
            ref var b = ref Unsafe.As<TBox, AABB3F>(ref Unsafe.AsRef(in box));
            return _state.QueryAabb(_grid, b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ, categoryMask);
        }
        if (typeof(TBox) == typeof(AABB2D))
        {
            // The f64 branches are the f32 ones with no conversion at all, which is what #914 phase C actually amounted to here: the enumerator's query box
            // is f64, so an AABB2D's doubles are simply passed through where an AABB2F's floats are widened. The tier check above has already established
            // that the archetype's storage matches, so a caller cannot reach this branch against an f32 archetype.
            ref var b = ref Unsafe.As<TBox, AABB2D>(ref Unsafe.AsRef(in box));
            return _state.QueryAabb(_grid, b.MinX, b.MinY, double.NegativeInfinity, b.MaxX, b.MaxY, double.PositiveInfinity, categoryMask);
        }
        if (typeof(TBox) == typeof(AABB3D))
        {
            ref var b = ref Unsafe.As<TBox, AABB3D>(ref Unsafe.AsRef(in box));
            return _state.QueryAabb(_grid, b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ, categoryMask);
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
            ThrowNoSpatialIndex();
        }

        var storageTier = _state.SpatialSlot.FieldInfo.FieldType.ToTier();
        if (storageTier != SpatialTier.Tier2F)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>.Radius(BSphere2F): " +
                $"query tier Tier2F does not match archetype storage tier {storageTier}. " +
                "Use the overload matching your archetype's dimensionality and precision (BSphere2F / BSphere3F / BSphere2D / BSphere3D).");
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
            ThrowNoSpatialIndex();
        }

        var storageTier = _state.SpatialSlot.FieldInfo.FieldType.ToTier();
        if (storageTier != SpatialTier.Tier3F)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>.Radius(BSphere3F): " +
                $"query tier Tier3F does not match archetype storage tier {storageTier}. " +
                "Use the overload matching your archetype's dimensionality and precision (BSphere2F / BSphere3F / BSphere2D / BSphere3D).");
        }

        return _state.QueryRadius(_grid, sphere.CenterX, sphere.CenterY, sphere.CenterZ, sphere.Radius, categoryMask);
    }

    /// <summary>
    /// f64 counterpart of <see cref="Radius(in BSphere2F, uint)"/> (#914). The archetype must be 2D f64 (tier <c>Tier2D</c>).
    /// </summary>
    public AabbClusterEnumerator Radius(in BSphere2D sphere, uint categoryMask = uint.MaxValue)
    {
        if (_state == null || !_state.SpatialSlot.HasSpatialIndex)
        {
            ThrowNoSpatialIndex();
        }

        var storageTier = _state.SpatialSlot.FieldInfo.FieldType.ToTier();
        if (storageTier != SpatialTier.Tier2D)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>.Radius(BSphere2D): " +
                $"query tier Tier2D does not match archetype storage tier {storageTier}. " +
                "Use the overload matching your archetype's dimensionality and precision (BSphere2F / BSphere3F / BSphere2D / BSphere3D).");
        }

        return _state.QueryRadius(_grid, sphere.CenterX, sphere.CenterY, 0d, sphere.Radius, categoryMask);
    }

    /// <summary>
    /// f64 counterpart of <see cref="Radius(in BSphere3F, uint)"/> (#914). The archetype must be 3D f64 (tier <c>Tier3D</c>).
    /// </summary>
    public AabbClusterEnumerator Radius(in BSphere3D sphere, uint categoryMask = uint.MaxValue)
    {
        if (_state == null || !_state.SpatialSlot.HasSpatialIndex)
        {
            ThrowNoSpatialIndex();
        }

        var storageTier = _state.SpatialSlot.FieldInfo.FieldType.ToTier();
        if (storageTier != SpatialTier.Tier3D)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>.Radius(BSphere3D): " +
                $"query tier Tier3D does not match archetype storage tier {storageTier}. " +
                "Use the overload matching your archetype's dimensionality and precision (BSphere2F / BSphere3F / BSphere2D / BSphere3D).");
        }

        return _state.QueryRadius(_grid, sphere.CenterX, sphere.CenterY, sphere.CenterZ, sphere.Radius, categoryMask);
    }

    /// <summary>
    /// For each sphere in <paramref name="members"/>, how many entities <see cref="Radius(in BSphere2F, uint)"/> returns for it:
    /// <c>counts[j] == Radius(members[j], categoryMask).Count()</c>. The cells, cell halves and clusters the members share are walked and opened once for
    /// the whole set instead of once per member. The archetype must be 2D (tier <c>Tier2F</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>When it pays.</b> When the members are close together — one source cluster's entities, a squad, a crowd's observers — their own queries
    /// walk nearly the same cells and open the same clusters, which the batch then does once; each member is still tested only against the clusters its
    /// own box overlaps. A member far from the others costs what its own query costs: no cell outside a member's own range is visited.</para>
    /// <para>At most 64 members. The caller must be inside an <see cref="EpochGuard"/> scope, as for a single query.</para>
    /// </remarks>
    /// <param name="members">The query spheres, in world units.</param>
    /// <param name="counts">Receives one count per member, in <paramref name="members"/>' order; entries past its length are left alone.</param>
    /// <param name="categoryMask">As for <see cref="Radius(in BSphere2F, uint)"/>, for every member.</param>
    /// <exception cref="ArgumentException">More than 64 members, a member that is not finite, or <paramref name="counts"/> shorter than
    /// <paramref name="members"/>. Checked before anything is written: <paramref name="counts"/> is then left as it was.</exception>
    /// <exception cref="InvalidOperationException">The archetype has no spatial index, or is not 2D f32.</exception>
    public void CountRadius(ReadOnlySpan<BSphere2F> members, Span<int> counts, uint categoryMask = uint.MaxValue)
    {
        CheckRadiusBatch(members);
        if (counts.Length < members.Length)
        {
            throw new ArgumentException($"CountRadius: counts holds {counts.Length} entries for {members.Length} members.", nameof(counts));
        }

        counts[..members.Length].Clear();
        var drain = new ClusterRadiusBatch.CountDrain(counts);
        ClusterRadiusBatch.Run(_state, _grid, members, categoryMask, ref drain);
    }

    /// <summary>
    /// For each sphere in <paramref name="members"/>, the entities <see cref="Radius(in BSphere2F, uint)"/> returns for it, handed to
    /// <paramref name="sink"/> as (member index, hit): each member's hits in its own query's <c>MoveNext</c> order, with the same bounds and squared
    /// distance. The cells, cell halves and clusters the members share are walked and opened once for the whole set. The archetype must be 2D (tier
    /// <c>Tier2F</c>).
    /// </summary>
    /// <remarks>
    /// <para>The sink returns false to retire a member: it receives nothing further, and the others carry on. Members' hits interleave cluster by cluster;
    /// only each member's own sequence is ordered.</para>
    /// <para>The sink may run its own spatial queries: they take a page window of their own. If it throws, the batch hands its window back and the
    /// exception propagates; <paramref name="sink"/> then holds the state it had reached.</para>
    /// <para>At most 64 members. The caller must be inside an <see cref="EpochGuard"/> scope, as for a single query. See <see cref="CountRadius"/> for when
    /// a batch pays.</para>
    /// </remarks>
    /// <param name="members">The query spheres, in world units.</param>
    /// <param name="sink">Receives the hits. Copied into the batch and copied back when it returns or throws, so read its state after the call, not
    /// from inside a <see cref="IRadiusBatchSink.Hit"/> through another reference to the caller's variable.</param>
    /// <param name="categoryMask">As for <see cref="Radius(in BSphere2F, uint)"/>, for every member.</param>
    /// <exception cref="ArgumentException">More than 64 members, or a member that is not finite; checked before any hit is delivered.</exception>
    /// <exception cref="InvalidOperationException">The archetype has no spatial index, or is not 2D f32.</exception>
    public void ForEachInRadius<TSink>(ReadOnlySpan<BSphere2F> members, ref TSink sink, uint categoryMask = uint.MaxValue)
        where TSink : struct, IRadiusBatchSink, allows ref struct
    {
        CheckRadiusBatch(members);
        var drain = new ClusterRadiusBatch.SinkDrain<TSink>(sink);
        try
        {
            ClusterRadiusBatch.Run(_state, _grid, members, categoryMask, ref drain);
        }
        finally
        {
            sink = drain.Sink;
        }
    }

    /// <summary>A batch's preconditions, all checked before anything is written: a spatial index, a 2D f32 archetype, at most 64 members, each finite.</summary>
    private void CheckRadiusBatch(ReadOnlySpan<BSphere2F> members)
    {
        if (_state == null || !_state.SpatialSlot.HasSpatialIndex)
        {
            ThrowNoSpatialIndex();
        }

        var storageTier = _state.SpatialSlot.FieldInfo.FieldType.ToTier();
        if (storageTier != SpatialTier.Tier2F)
        {
            throw new InvalidOperationException(
                $"ClusterSpatialQuery<{typeof(TArch).Name}>: a radius batch takes BSphere2F members, which need storage tier Tier2F; this archetype's is " +
                $"{storageTier}.");
        }

        if (members.Length > ClusterRadiusBatch.MaxMembers)
        {
            throw new ArgumentException($"A radius batch takes at most {ClusterRadiusBatch.MaxMembers} members; got {members.Length}.", nameof(members));
        }

        // A single query rejects a non-finite box (WorldToCellRange). Rejected here instead, the batch fails before it has written a count or delivered a
        // hit, rather than halfway through its members.
        for (var j = 0; j < members.Length; j++)
        {
            ref readonly var m = ref members[j];
            if (!float.IsFinite(m.CenterX) || !float.IsFinite(m.CenterY) || !float.IsFinite(m.Radius))
            {
                throw new ArgumentException($"Radius batch member {j} is not finite: ({m.CenterX}, {m.CenterY}) r {m.Radius}.", nameof(members));
            }
        }
    }

    /// <summary>Out of line, so the message is not built into every inlined query construction.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNoSpatialIndex() =>
        throw new InvalidOperationException(
            $"ClusterSpatialQuery<{typeof(TArch).Name}>: archetype has no spatial index. " +
            "Ensure the archetype has a SpatialIndex field and that ConfigureSpatialGrid was called " +
            "on the engine before InitializeArchetypes.");
}

/// <summary>
/// Receives a batched radius query's hits (<see cref="ClusterSpatialQuery{TArch}.ForEachInRadius{TSink}"/>): one call per (member, hit), each member's
/// hits in the order its own query's <c>MoveNext</c> yields them.
/// </summary>
public interface IRadiusBatchSink
{
    /// <summary>One hit for member <paramref name="member"/>, an index into the batch's members. Return false to retire that member: it receives no
    /// further hits.</summary>
    bool Hit(int member, in ClusterSpatialQueryResult hit);
}

/// <summary>
/// Result of a cluster spatial query match. Holds the matched <see cref="Typhon.Engine.EntityId"/>, its location inside the cluster storage (chunk id and slot
/// index), the entity's tight bounds as read by the narrowphase, and — for Radius queries — the squared distance from the query center to the closest point on
/// the entity's AABB. For AABB queries, <see cref="DistanceSq"/> is <c>0</c> and should be ignored.
/// </summary>
/// <remarks>
/// <b>The bounds are f64 since #914</b>, because they are WORLD coordinates and the world frame is f64. For an f32 tier the values are the stored floats
/// widened — exact, so an <c>AABB2F</c> archetype reads back precisely what it stored — and for an f64 tier they are the only way the caller can see the
/// coordinate the component actually holds. This is what "the query supports f64" has to mean beyond the query box: a result that narrowed the answer back
/// to f32 would hand an <c>AABB3D</c> archetype at 10⁹ a bound quantised to ~128-unit steps, and the tier would be internal-only in the way #914 exists to
/// stop. It also costs the narrowphase nothing: <c>ReadAndValidateBoundsFromPtr</c> already produced doubles, so the six per-entity conversions that used to
/// narrow them are gone rather than added.
/// </remarks>
public readonly struct ClusterSpatialQueryResult
{
    /// <summary>The matched entity, ready to <c>Open</c>, <c>Destroy</c> or store. Eight bytes, the same size as the packed id it replaced (#909).</summary>
    public readonly EntityId Entity;

    /// <summary>Chunk id of the cluster holding the matched entity, within the archetype's cluster storage segment.</summary>
    public readonly int ClusterChunkId;

    /// <summary>Slot index of the matched entity within its cluster.</summary>
    public readonly int SlotIndex;

    /// <summary>Squared distance from the query center to the closest point on the entity's AABB. Populated by Radius queries; always <c>0</c> for AABB
    /// queries. Used by <c>ArchetypeClusterState.QueryNearest</c> for top-k sorting. Issue #230 Phase 3.</summary>
    public readonly double DistanceSq;

    /// <summary>
    /// Minimum X of the entity's tight AABB, as read by the narrowphase. Reading these bounds off the result lets callers skip a second component-table read.
    /// </summary>
    public readonly double MinX;

    /// <summary>Minimum Y of the entity's tight AABB.</summary>
    public readonly double MinY;

    /// <summary>Minimum Z of the entity's tight AABB. For 2D archetypes this reflects the query's Z range (typically an infinity sentinel) and should be ignored.</summary>
    public readonly double MinZ;

    /// <summary>Maximum X of the entity's tight AABB.</summary>
    public readonly double MaxX;

    /// <summary>Maximum Y of the entity's tight AABB.</summary>
    public readonly double MaxY;

    /// <summary>Maximum Z of the entity's tight AABB. For 2D archetypes this reflects the query's Z range (typically an infinity sentinel) and should be ignored.</summary>
    public readonly double MaxZ;

    internal ClusterSpatialQueryResult(EntityId entity, int clusterChunkId, int slotIndex, double minX, double minY, double minZ, double maxX, double maxY,
        double maxZ, double distanceSq = 0d)
    {
        Entity = entity;
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
/// Construction helpers for <see cref="T:Typhon.Engine.ClusterSpatialQuery`1"/>. Exposed on <see cref="DatabaseEngine"/> so callers can write
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
        where TArch : Archetype<TArch>, new() => ClusterSpatialQuery<TArch>(engine, RealmId.Default);

    /// <summary>
    /// Create a per-cell cluster query for the given archetype in realm <paramref name="realm"/>: it sees only that realm's entities, however their
    /// coordinates compare with another realm's.
    /// </summary>
    /// <typeparam name="TArch">The archetype type.</typeparam>
    /// <param name="engine">The database engine.</param>
    /// <param name="realm">The realm to query. Must be registered.</param>
    /// <returns>A zero-allocation query handle.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the archetype is not cluster-eligible, has no spatial component, or the realm is not registered.
    /// </exception>
    public static ClusterSpatialQuery<TArch> ClusterSpatialQuery<TArch>(this DatabaseEngine engine, RealmId realm)
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
        return new ClusterSpatialQuery<TArch>(state, engine.RealmGridForQuery(realm));
    }
}
