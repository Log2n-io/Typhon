using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// An independent implementation of #872 step 10's intra-cell drift rule, used as the reference the engine is measured
/// against (<c>AC-10.1</c>, <c>AC-10.2</c>, <c>AC-10.6</c>).
/// </summary>
/// <remarks>
/// <para><b>Independent in the two ways that matter.</b> It derives each cluster's bound from entity positions it reads
/// itself rather than from <see cref="ArchetypeClusterState.ClusterAabbs"/>, and it applies the gate, the centroid and the
/// margin straight from the documented rule rather than by calling the production predicate. An oracle that asked
/// production "did you detect what you detect" would pass through any change to the rule, including a wrong one.</para>
/// <para><b>Shared by both drift fixtures on purpose.</b> <c>ClusterDriftDetectionTests</c> compares the SERIAL fence
/// against it and <c>ClusterDriftParallelTests</c> compares the PARALLEL fence against it, which makes the two arms
/// comparable through a third party. Diffing serial against parallel directly would go green whenever both are wrong the
/// same way — and they share every line of detection, so that is the likely failure, not a remote one.</para>
/// <para><b>One clause of <c>CR-03</c> is deliberately NOT modelled: the exclusion of slots the outlier guard has already
/// claimed.</b> Production computes its drifter set as <c>centres.ValidMask &amp; ~guardClaimedSlots</c>; this oracle has no
/// equivalent, so it is faithful only while the outlier guard fires nothing. Every population that drives it today is
/// entirely intra-cell, which is exactly the condition under which the guard cannot fire — so the two agree, and the
/// differential means what it claims. It stops being true the moment a fixture lets an entity leave its cell, and the
/// exclusion clause is unverified until one does. Modelling it here would mean reimplementing the guard's cell-escape test
/// in the oracle, which is a second production path to keep in sync; naming the gap is the honest trade.</para>
/// <para><b>The target region is centred on the CENTROID, not on the midpoint of the bound.</b> That is the rule, not an
/// implementation detail. An AABB midpoint sits halfway between the two extremes, so one far outlier drags it half the
/// distance to itself, and the whole core of the cluster then reads as drifting away from a point where nothing lives.
/// This oracle had it wrong first and reported 99 against the engine's 102.</para>
/// </remarks>
static class ClusterDriftOracle
{
    /// <summary>The drifters and the absorbed, keyed by the slot they live in — the same identity the queue records.</summary>
    internal readonly struct Verdict
    {
        internal Verdict(int scanned, List<(int Chunk, int Slot)> drifters, int absorbed)
        {
            ClustersScanned = scanned;
            Drifters = drifters;
            Absorbed = absorbed;
        }

        /// <summary>Clusters the rule considered — every cluster with a live cell mapping and at least one entity.</summary>
        internal int ClustersScanned { get; }

        /// <summary>Sorted by (chunk, slot), so it is an order-independent value that <c>Is.EqualTo</c> can compare.</summary>
        internal List<(int Chunk, int Slot)> Drifters { get; }

        /// <summary>Entities outside the target region by less than the margin.</summary>
        internal int Absorbed { get; }
    }

    /// <summary>
    /// Applies the rule to every <c>ClMigUnit</c> cluster in <paramref name="dbe"/>, reading positions through a read-only
    /// transaction.
    /// </summary>
    /// <remarks>
    /// Bound to <c>ClMigUnit</c> rather than generic: the body has to name a spatial component to read (<c>ClMigUnit.Pos</c>),
    /// and a type parameter that every call site must pair with that same hard-coded field would be decoration, not reuse.
    /// </remarks>
    /// <param name="dbe">The engine to read. Must not be ticking — the caller is responsible for quiescence.</param>
    /// <param name="clusterState">The archetype's cluster state, for its <c>ClusterCellMap</c>.</param>
    /// <param name="targetExtent">P4's target region side, in world units: <c>CellSize * ClusterTargetExtentRatio</c>.</param>
    /// <param name="driftMargin">The intra-cell dead zone, in world units: <c>CellSize * ClusterDriftMarginRatio</c>.</param>
    internal static unsafe Verdict Evaluate(DatabaseEngine dbe, ArchetypeClusterState clusterState, float targetExtent, float driftMargin)
    {
        int scanned = 0, absorbed = 0;
        var drifters = new List<(int, int)>();

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                int chunkId = cluster.ChunkId;
                if (clusterState.ClusterCellMap[chunkId] < 0)
                {
                    continue;
                }

                scanned++;

#pragma warning disable TYPHON009 // Read-only: this is the oracle.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009

                // The cluster's true bound, from its entities — not from ClusterAabbs.
                float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
                float sumX = 0f, sumY = 0f;
                int counted = 0;

                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref readonly var b = ref positions[slot].Bounds;
                    float cx = 0.5f * (b.MinX + b.MaxX);
                    float cy = 0.5f * (b.MinY + b.MaxY);
                    minX = MathF.Min(minX, cx); maxX = MathF.Max(maxX, cx);
                    minY = MathF.Min(minY, cy); maxY = MathF.Max(maxY, cy);
                    sumX += cx; sumY += cy;
                    counted++;
                }

                if (counted == 0)
                {
                    continue;
                }

                // Level 1 — the gate. A cluster already inside the target extent cannot be improved by moving anything out of it.
                if ((maxX - minX) <= targetExtent && (maxY - minY) <= targetExtent)
                {
                    continue;
                }

                float centreX = sumX / counted;
                float centreY = sumY / counted;
                float half = targetExtent * 0.5f;

                bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    ref readonly var b = ref positions[slot].Bounds;
                    float cx = 0.5f * (b.MinX + b.MaxX);
                    float cy = 0.5f * (b.MinY + b.MaxY);

                    float overshoot = MathF.Max(MathF.Abs(cx - centreX) - half, MathF.Abs(cy - centreY) - half);
                    if (overshoot <= 0f)
                    {
                        continue;
                    }
                    if (overshoot <= driftMargin)
                    {
                        absorbed++;
                        continue;
                    }

                    drifters.Add((chunkId, slot));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        drifters.Sort(static (a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
        return new Verdict(scanned, drifters, absorbed);
    }
}
