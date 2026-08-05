// unset

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Rebuilds HLL, MCV, and Histogram statistics for all indexed fields of a ComponentTable in a single chunk-based scan using page-granularity sampling.
/// </summary>
/// <remarks>
/// <para>
/// The scan iterates pages of the ComponentSegment directly, reading the L0 bitmap to find occupied chunks and extracting field values via pointer arithmetic.
/// This avoids B+Tree traversal overhead and processes all indexed fields per entity in one pass.
/// </para>
/// <para>
/// After building new statistics structures, references are atomic-swapped on the IndexStatistics array, ensuring concurrent query threads never see torn data.
/// </para>
/// </remarks>
internal static class StatisticsRebuilder
{
    /// <summary>
    /// The cluster counterpart of <see cref="RebuildClusterAll"/> (#665): rebuilds statistics for every per-archetype index home by walking the archetype's active
    /// clusters instead of a ComponentSegment's occupancy bitmaps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because a cluster-backed archetype's entities are not in <see cref="ComponentTable.ComponentSegment"/> at all: pointing
    /// <see cref="RebuildClusterAll"/> at it would sample nothing and publish statistics built from an empty scan — worse than leaving them stale, which is why
    /// <see cref="StatisticsWorker"/> must route these archetypes here rather than merely counting their mutations.
    /// </para>
    /// <para>
    /// One <see cref="Accumulators"/> per index SLOT, over that slot's own <see cref="ClusterIndexSlot{TStore}.Stats"/>: statistics describe one component's
    /// key distribution within one archetype, which is the granularity the planner asks about.
    /// </para>
    /// </remarks>
    /// <param name="clusterState">The archetype's cluster state; both index homes are walked.</param>
    /// <param name="epochManager">Epoch manager for page access protection.</param>
    /// <param name="clusterInterval">Cluster sampling interval: 1 = every active cluster, N = every Nth.</param>
    internal static void RebuildClusterAll(ArchetypeClusterState clusterState, EpochManager epochManager, int clusterInterval = 1)
    {
        ArgumentNullException.ThrowIfNull(clusterState);
        using var guard = EpochGuard.Enter(epochManager);

        if (clusterState.IndexSlots != null && clusterState.IndexSlots.Length > 0)
        {
            var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
            try
            {
                RebuildClusterHome(clusterState, clusterState.IndexSlots, ref clusterAccessor, ref clusterAccessor, clusterInterval);
            }
            finally
            {
                clusterAccessor.Dispose();
            }
        }

        if (clusterState.TransientIndexSlots != null && clusterState.TransientIndexSlots.Length > 0)
        {
            var transientAccessor = clusterState.TransientSegment.CreateChunkAccessor();
            try
            {
                if (clusterState.ClusterSegment == null)
                {
                    RebuildClusterHome(clusterState, clusterState.TransientIndexSlots, ref transientAccessor, ref transientAccessor, clusterInterval);
                }
                else
                {
                    var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
                    try
                    {
                        RebuildClusterHome(clusterState, clusterState.TransientIndexSlots, ref clusterAccessor, ref transientAccessor, clusterInterval);
                    }
                    finally
                    {
                        clusterAccessor.Dispose();
                    }
                }
            }
            finally
            {
                transientAccessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Walks one index home's active clusters, accumulating each indexed field's key distribution. Same three-store split as
    /// <c>DatabaseEngine.ProcessClusterShadowEntries</c>: occupancy from <paramref name="primaryAccessor"/>, the component column from
    /// <paramref name="dataAccessor"/>, and the same accessor twice for every home except a Transient slot on a mixed archetype.
    /// </summary>
    private static unsafe void RebuildClusterHome<TIdx, TPrimary, TData>(ArchetypeClusterState clusterState, ClusterIndexSlot<TIdx>[] ixSlots,
        ref ChunkAccessor<TPrimary> primaryAccessor, ref ChunkAccessor<TData> dataAccessor, int clusterInterval)
        where TIdx : struct, IPageStore
        where TPrimary : struct, IPageStore
        where TData : struct, IPageStore
    {
        var layout = clusterState.Layout;
        int interval = Math.Max(1, clusterInterval);
        // Upper bound rather than an exact count — it only scales sampled counts back up, and the same bound already backs EcsQuery's cluster selectivity.
        int estimatedTotal = clusterState.ActiveClusterCount * layout.ClusterSize;

        for (int s = 0; s < ixSlots.Length; s++)
        {
            ref var ixSlot = ref ixSlots[s];
            var stats = ixSlot.Stats;
            if (stats == null || stats.Length == 0)
            {
                continue;
            }

            var acc = new Accumulators(stats);
            int compSize = layout.ComponentSize(ixSlot.Slot);
            int compOffset = layout.ComponentOffset(ixSlot.Slot);

            for (int c = 0; c < clusterState.ActiveClusterCount; c += interval)
            {
                int clusterChunkId = clusterState.ActiveClusterIds[c];
                ulong occupancy = *(ulong*)primaryAccessor.GetChunkAddress(clusterChunkId);
                if (occupancy == 0)
                {
                    continue;
                }

                byte* compBase = dataAccessor.GetChunkAddress(clusterChunkId) + compOffset;
                while (occupancy != 0)
                {
                    int slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

                    byte* entityComp = compBase + slotIndex * compSize;
                    acc.CountEntity();
                    for (int f = 0; f < ixSlot.Fields.Length; f++)
                    {
                        if (!acc.Supports(f))
                        {
                            continue;
                        }

                        acc.Observe(ExtractKeyAsLong(entityComp, ixSlot.Fields[f].FieldOffset, stats[f].KeyType), f);
                    }
                }
            }

            acc.Finish(stats, estimatedTotal, interval);
        }
    }

    /// <summary>
    /// Transforms IEEE 754 float/double bit patterns into order-preserving integer representations.
    /// Positive floats: flip the sign bit (so they sort above negatives as integers).
    /// Negative floats: flip ALL bits (reverses their magnitude order and moves them below positives).
    /// Identity for non-floating-point types.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ToOrderPreserving(long rawBits, KeyType keyType)
    {
        if (keyType == KeyType.Float)
        {
            int bits = (int)rawBits;
            // Cast through uint to prevent sign extension when widening to long.
            // Without this, a positive float (sign bit flipped to 1 in int) sign-extends to a negative long, breaking the ordering invariant.
            return (uint)(bits < 0 ? ~bits : bits ^ unchecked((int)0x80000000));
        }

        if (keyType == KeyType.Double)
        {
            // Negative double: XOR with long.MaxValue flips all bits except sign → maps to [long.MinValue+ε, -1] in signed space, preserving magnitude ordering.
            // Positive double: already in [0, long.MaxValue], no transform needed.
            return rawBits < 0 ? rawBits ^ long.MaxValue : rawBits;
        }

        return rawBits;
    }

    /// <summary>
    /// Extracts the key value from raw chunk bytes at the given offset, encoded as a long
    /// using the same convention as B+Tree key encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long ExtractKeyAsLong(byte* chunkAddr, int offset, KeyType keyType)
    {
        byte* ptr = chunkAddr + offset;
        return keyType switch
        {
            KeyType.Bool => *(bool*)ptr ? 1L : 0L,
            KeyType.Byte => *ptr,
            KeyType.SByte => *(sbyte*)ptr,
            KeyType.Short => *(short*)ptr,
            KeyType.UShort => *(ushort*)ptr,
            KeyType.Int => *(int*)ptr,
            KeyType.UInt => *(uint*)ptr,
            KeyType.Long => *(long*)ptr,
            KeyType.ULong => (long)*(ulong*)ptr,
            KeyType.Float => *(int*)ptr,       // IEEE 754 bit pattern
            KeyType.Double => *(long*)ptr,      // IEEE 754 bit pattern
            _ => *(long*)ptr
        };
    }

    /// <summary>
    /// Per-indexed-field HLL / MCV / histogram accumulators for one statistics rebuild, plus the scaling and atomic-swap that finishes it.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="RebuildClusterAll"/> (#665) so the cluster scan below can reuse it. Only the SCAN differs between the two homes — a page walk over
    /// the ComponentSegment's occupancy bitmaps versus a walk of the archetype's active clusters — while the ~150 lines of accumulation, sample scaling and
    /// publication are identical, and a second copy of them is a second thing to keep in step.
    /// </remarks>
    private sealed class Accumulators
    {
        private readonly bool[] _supported;
        private readonly HyperLogLog[] _hlls;
        private readonly Dictionary<long, int>[] _freqs;
        private readonly int[][] _bucketCounts;
        private readonly long[] _mins;
        private readonly long[] _maxes;
        private readonly long[] _bucketWidths;
        private readonly KeyType[] _keyTypes;
        private int _sampledEntities;

        internal Accumulators(IndexStatistics[] indexStats)
        {
            int fieldCount = indexStats.Length;
            _supported = new bool[fieldCount];
            _hlls = new HyperLogLog[fieldCount];
            _freqs = new Dictionary<long, int>[fieldCount];
            _bucketCounts = new int[fieldCount][];
            _mins = new long[fieldCount];
            _maxes = new long[fieldCount];
            _bucketWidths = new long[fieldCount];
            _keyTypes = new KeyType[fieldCount];

            for (int i = 0; i < fieldCount; i++)
            {
                _supported[i] = indexStats[i].SupportsStatistics;
                _keyTypes[i] = indexStats[i].KeyType;
                if (!_supported[i])
                {
                    continue;
                }

                _hlls[i] = new HyperLogLog();
                _freqs[i] = new Dictionary<long, int>();
                _bucketCounts[i] = new int[Histogram.BucketCount];
                // Live min/max from the B+Tree, so bucketing is accurate even when the scan samples. Order-preserving encoding makes the integer
                // arithmetic below correct for float/double.
                _mins[i] = ToOrderPreserving(indexStats[i].MinValue, _keyTypes[i]);
                _maxes[i] = ToOrderPreserving(indexStats[i].MaxValue, _keyTypes[i]);
                // Unsigned subtraction: handles OP-encoded ranges spanning the signed long boundary.
                _bucketWidths[i] = (_maxes[i] == _mins[i]) ? 0 : Math.Max(1L, (long)(((ulong)_maxes[i] - (ulong)_mins[i]) / Histogram.BucketCount));
            }
        }

        /// <summary>Whether field <paramref name="f"/> has a key type statistics can summarise (everything but String64).</summary>
        internal bool Supports(int f) => _supported[f];

        /// <summary>Number of entities visited so far — the denominator for sample scaling.</summary>
        internal int SampledEntities => _sampledEntities;

        /// <summary>Records that one entity was visited. Called once per entity, not once per field.</summary>
        internal void CountEntity() => _sampledEntities++;

        /// <summary>Folds one entity's value for field <paramref name="f"/> into that field's accumulators.</summary>
        internal void Observe(long key, int f)
        {
            _hlls[f].Add(key);

            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_freqs[f], key, out _);
            count++;

            long opKey = ToOrderPreserving(key, _keyTypes[f]);
            int bucket;
            if (_bucketWidths[f] == 0)
            {
                bucket = 0;
            }
            else
            {
                // Unsigned subtraction for OP-encoded cross-zero ranges
                var b = (long)(((ulong)opKey - (ulong)_mins[f]) / (ulong)_bucketWidths[f]);
                bucket = (int)Math.Min(b, Histogram.BucketCount - 1);
            }
            _bucketCounts[f][bucket]++;
        }

        /// <summary>
        /// Scales the accumulated counts back up to <paramref name="estimatedTotalEntities"/> when the scan sampled, then publishes each field's HLL, MCV and
        /// histogram onto <paramref name="indexStats"/>. No-op when nothing was visited — stale statistics beat statistics built from an empty scan.
        /// </summary>
        internal void Finish(IndexStatistics[] indexStats, int estimatedTotalEntities, int samplingInterval)
        {
            if (_sampledEntities == 0)
            {
                return;
            }

            double scaleFactor = (samplingInterval > 1) ? (double)estimatedTotalEntities / _sampledEntities : 1.0;
            long scaledTotal = (long)(_sampledEntities * scaleFactor);

            for (int f = 0; f < indexStats.Length; f++)
            {
                if (!_supported[f])
                {
                    continue;
                }

                var mcv = MostCommonValues.Build(_freqs[f], scaledTotal, scaleFactor);

                int[] scaledBuckets;
                int histogramTotal = 0;
                if (scaleFactor > 1.0)
                {
                    scaledBuckets = new int[Histogram.BucketCount];
                    for (int b = 0; b < Histogram.BucketCount; b++)
                    {
                        scaledBuckets[b] = Math.Max(0, (int)(_bucketCounts[f][b] * scaleFactor));
                        histogramTotal += scaledBuckets[b];
                    }
                }
                else
                {
                    scaledBuckets = _bucketCounts[f];
                    for (int b = 0; b < Histogram.BucketCount; b++)
                    {
                        histogramTotal += scaledBuckets[b];
                    }
                }

                // Atomic swap: volatile writes ensure visibility to concurrent readers
                indexStats[f].HyperLogLog = _hlls[f];
                indexStats[f].MostCommonValues = mcv;
                indexStats[f].Histogram = new Histogram(_mins[f], _maxes[f], scaledBuckets, histogramTotal);
            }
        }
    }
}
