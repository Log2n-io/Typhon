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
    /// Rebuilds HLL, MCV, and Histogram for ALL indexed fields of a ComponentTable in a single chunk-based scan with page-granularity sampling.
    /// </summary>
    /// <param name="table">The ComponentTable to scan.</param>
    /// <param name="epochManager">Epoch manager for page access protection.</param>
    /// <param name="pageInterval">Page sampling interval: 1 = full scan, N = every Nth page.</param>
    internal static unsafe void RebuildAll(ComponentTable table, EpochManager epochManager, int pageInterval = 1)
    {
        var indexedFieldInfos = table.IndexedFieldInfos;
        var indexStats = table.IndexStats;
        int fieldCount = indexedFieldInfos.Length;
        if (fieldCount == 0)
        {
            return;
        }

        // Build skip mask for fields that don't support statistics (e.g., String64)
        var supported = new bool[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            supported[i] = indexStats[i].SupportsStatistics;
        }

        // Allocate per-field accumulators
        var hlls = new HyperLogLog[fieldCount];
        var freqs = new Dictionary<long, int>[fieldCount];
        var bucketCounts = new int[fieldCount][];
        var mins = new long[fieldCount];
        var maxes = new long[fieldCount];

        for (int i = 0; i < fieldCount; i++)
        {
            if (!supported[i])
            {
                continue;
            }

            hlls[i] = new HyperLogLog();
            freqs[i] = new Dictionary<long, int>();
            bucketCounts[i] = new int[Histogram.BucketCount];
            // Use live min/max from B+Tree for histogram bucketing (always accurate, even with sampling).
            // Convert to order-preserving encoding so integer arithmetic works correctly for float/double.
            mins[i] = ToOrderPreserving(indexStats[i].MinValue, indexStats[i].KeyType);
            maxes[i] = ToOrderPreserving(indexStats[i].MaxValue, indexStats[i].KeyType);
        }

        // Pre-compute bucket widths from B+Tree min/max (not sampled data).
        // Float/double fields use order-preserving encoding: bit patterns are transformed so
        // integer arithmetic preserves float ordering (negative → flip all, positive → flip sign).
        var bucketWidths = new long[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            if (!supported[i])
            {
                continue;
            }

            // Unsigned subtraction: handles OP-encoded float/double ranges spanning the signed long boundary
            bucketWidths[i] = (maxes[i] == mins[i]) ? 0 : Math.Max(1L, (long)(((ulong)maxes[i] - (ulong)mins[i]) / Histogram.BucketCount));
        }

        var segment = table.ComponentSegment;
        int totalPages = segment.Length;
        int stride = segment.Stride;
        int rootChunkCount = segment.ChunkCountRootPage;
        int otherChunkCount = segment.ChunkCountPerPage;
        int bitmapLongsRoot = (rootChunkCount + 63) >> 6;
        int bitmapLongsOther = (otherChunkCount + 63) >> 6;
        int rootDataOffset = segment.RootChunkDataOffset;
        int otherDataOffset = segment.OtherChunkDataOffset;

        int sampledEntities = 0;

        // Single epoch guard for the entire scan
        using var guard = EpochGuard.Enter(epochManager);
        var epoch = guard.Epoch;

        for (int pageIndex = 0; pageIndex < totalPages; pageIndex += pageInterval)
        {
            bool isRoot = (pageIndex == 0);
            int maxChunks = isRoot ? rootChunkCount : otherChunkCount;
            int bitmapLongs = isRoot ? bitmapLongsRoot : bitmapLongsOther;
            int dataOffset = isRoot ? rootDataOffset : otherDataOffset;

            var page = segment.GetPage(pageIndex, epoch, out _);
            var bitmap = page.MetadataReadOnly<long>();

            for (int w = 0; w < bitmapLongs; w++)
            {
                long word = bitmap[w];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    int chunkInPage = w * 64 + bit;
                    word &= word - 1; // Clear lowest set bit

                    if (chunkInPage >= maxChunks)
                    {
                        break;
                    }

                    // Skip chunk 0 on root page (null sentinel)
                    if (isRoot && chunkInPage == 0)
                    {
                        continue;
                    }

                    // Get pointer to chunk raw data
                    var chunkData = page.RawData<byte>(dataOffset + chunkInPage * stride, stride);
                    sampledEntities++;

                    fixed (byte* ptr = chunkData)
                    {
                        for (int f = 0; f < fieldCount; f++)
                        {
                            if (!supported[f])
                            {
                                continue;
                            }

                            long key = ExtractKeyAsLong(ptr, indexedFieldInfos[f].OffsetToField, indexStats[f].KeyType);

                            // HLL
                            hlls[f].Add(key);

                            // Frequency counting for MCV
                            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(freqs[f], key, out _);
                            count++;

                            // Histogram bucketing (order-preserving encoding ensures correct bucket assignment for float/double)
                            {
                                long opKey = ToOrderPreserving(key, indexStats[f].KeyType);
                                int bucket;
                                if (bucketWidths[f] == 0)
                                {
                                    bucket = 0;
                                }
                                else
                                {
                                    // Unsigned subtraction for OP-encoded cross-zero ranges
                                    var b = (long)(((ulong)opKey - (ulong)mins[f]) / (ulong)bucketWidths[f]);
                                    bucket = (int)Math.Min(b, Histogram.BucketCount - 1);
                                }
                                bucketCounts[f][bucket]++;
                            }
                        }
                    }
                }
            }
        }

        if (sampledEntities == 0)
        {
            return;
        }

        // Compute scale factor for sampling
        int estimatedTotalEntities = table.EstimatedEntityCount;
        double scaleFactor = (pageInterval > 1 && sampledEntities > 0) ? (double)estimatedTotalEntities / sampledEntities : 1.0;
        long scaledTotal = (long)(sampledEntities * scaleFactor);

        // Build final structures and atomic-swap per field
        for (int f = 0; f < fieldCount; f++)
        {
            if (!supported[f])
            {
                continue;
            }

            // MCV: scale individual counts via scaleFactor
            var mcv = MostCommonValues.Build(freqs[f], scaledTotal, scaleFactor);

            // Histogram: scale bucket counts if sampling. Min/max are in order-preserving space.
            int[] scaledBuckets;
            int histogramTotal;
            if (scaleFactor > 1.0)
            {
                scaledBuckets = new int[Histogram.BucketCount];
                histogramTotal = 0;
                for (int b = 0; b < Histogram.BucketCount; b++)
                {
                    scaledBuckets[b] = Math.Max(0, (int)(bucketCounts[f][b] * scaleFactor));
                    histogramTotal += scaledBuckets[b];
                }
            }
            else
            {
                scaledBuckets = bucketCounts[f];
                histogramTotal = 0;
                for (int b = 0; b < Histogram.BucketCount; b++)
                {
                    histogramTotal += scaledBuckets[b];
                }
            }

            var histogram = new Histogram(mins[f], maxes[f], scaledBuckets, histogramTotal);

            // Atomic swap: volatile writes ensure visibility to concurrent readers
            indexStats[f].HyperLogLog = hlls[f];
            indexStats[f].MostCommonValues = mcv;
            indexStats[f].Histogram = histogram;
        }
    }

    /// <summary>
    /// Convenience API: full scan (no sampling). Suitable for tests and explicit rebuilds.
    /// </summary>
    internal static void RebuildStatistics(ComponentTable table, EpochManager epochManager) => RebuildAll(table, epochManager);

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
}
