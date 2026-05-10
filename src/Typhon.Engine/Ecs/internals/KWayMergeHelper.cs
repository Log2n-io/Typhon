using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// A sorted stream of (orderedKey, entityPK) pairs from a single archetype's per-archetype B+Tree.
/// Collects all matching entries in a single pass (B+Tree enumerator is a ref struct that can't be stored).
/// The orderedKey uses the same sign-flip encoding as <see cref="ZoneMapArray"/> for universal comparison.
/// </summary>
internal unsafe struct ArchetypeSortedStream : IDisposable
{
    private long[] _orderedKeys;    // Rented from ArrayPool
    private long[] _entityPKs;      // Rented from ArrayPool
    private int _count;
    private int _pos;

    public bool HasCurrent
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _pos < _count;
    }

    public long CurrentKey
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _orderedKeys[_pos];
    }

    public long CurrentEntityPK
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _entityPKs[_pos];
    }

    public int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Advance()
    {
        _pos++;
        return _pos < _count;
    }

    /// <summary>
    /// Create and fill a sorted stream from a per-archetype B+Tree range scan.
    /// Collects entries in the [scanMin, scanMax] range in key order.
    /// When <paramref name="maxEntries"/> is greater than 0, stops after collecting that many entries
    /// (the B+Tree enumerator yields in sort order, so early termination is correct for Skip/Take).
    /// </summary>
    public static ArchetypeSortedStream Create(BTreeBase<PersistentStore> tree, KeyType keyType, long scanMin, long scanMax, bool allowMultiple, bool descending,
        ArchetypeClusterState clusterState, ArchetypeClusterInfo layout, int maxEntries = 0)
    {
        var stream = new ArchetypeSortedStream();
        stream._pos = 0;
        stream._count = 0;

        // Initial capacity — use maxEntries as hint when available to avoid over-allocating
        int initialCapacity = maxEntries > 0 ? Math.Min(maxEntries, 256) : 256;
        stream._orderedKeys = ArrayPool<long>.Shared.Rent(initialCapacity);
        stream._entityPKs = ArrayPool<long>.Shared.Rent(initialCapacity);

        // Dispatch on KeyType to open the correct typed B+Tree enumerator
        switch (keyType)
        {
            case KeyType.Int:
                FillTypedUnique((BTree<int, PersistentStore>)tree, (int)scanMin, (int)scanMax, allowMultiple, descending, keyType, ref stream, clusterState, layout,
                    maxEntries);
                break;
            case KeyType.Long:
                FillTypedUnique((BTree<long, PersistentStore>)tree, scanMin, scanMax, allowMultiple, descending, keyType, ref stream, clusterState, layout,
                    maxEntries);
                break;
            case KeyType.Float:
                FillTypedUnique((BTree<float, PersistentStore>)tree,
                    BitConverter.Int32BitsToSingle((int)scanMin), BitConverter.Int32BitsToSingle((int)scanMax), allowMultiple, descending, keyType,
                    ref stream, clusterState, layout, maxEntries);
                break;
            case KeyType.Double:
                FillTypedUnique((BTree<double, PersistentStore>)tree, BitConverter.Int64BitsToDouble(scanMin), BitConverter.Int64BitsToDouble(scanMax),
                    allowMultiple, descending, keyType, ref stream, clusterState, layout, maxEntries);
                break;
            case KeyType.Short:
                FillTypedUnique((BTree<short, PersistentStore>)tree, (short)scanMin, (short)scanMax, allowMultiple, descending, keyType, ref stream,
                    clusterState, layout, maxEntries);
                break;
            case KeyType.UShort:
                FillTypedUnique((BTree<ushort, PersistentStore>)tree, (ushort)scanMin, (ushort)scanMax, allowMultiple, descending, keyType, ref stream,
                    clusterState, layout, maxEntries);
                break;
            case KeyType.Byte:
                FillTypedUnique((BTree<byte, PersistentStore>)tree, (byte)scanMin, (byte)scanMax, allowMultiple, descending, keyType, ref stream,
                    clusterState, layout, maxEntries);
                break;
            case KeyType.SByte:
                FillTypedUnique((BTree<sbyte, PersistentStore>)tree, (sbyte)scanMin, (sbyte)scanMax, allowMultiple, descending, keyType, ref stream,
                    clusterState, layout, maxEntries);
                break;
            case KeyType.UInt:
                FillTypedUnique((BTree<uint, PersistentStore>)tree, (uint)scanMin, (uint)scanMax, allowMultiple, descending, keyType, ref stream,
                    clusterState, layout, maxEntries);
                break;
            case KeyType.ULong:
                // ULong stored as BTree<long> (same convention as PipelineExecutor)
                FillTypedUnique((BTree<long, PersistentStore>)tree, scanMin, scanMax, allowMultiple, descending, keyType, ref stream, clusterState, layout,
                    maxEntries);
                break;
        }

        return stream;
    }

    /// <summary>Fill the stream from a typed B+Tree (handles both unique and AllowMultiple).</summary>
    private static void FillTypedUnique<TKey>(BTree<TKey, PersistentStore> tree, TKey minKey, TKey maxKey, bool allowMultiple, bool descending, KeyType keyType,
        ref ArchetypeSortedStream stream, ArchetypeClusterState clusterState, ArchetypeClusterInfo layout, int maxEntries) where TKey : unmanaged
    {
        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            if (allowMultiple)
            {
                FillMultiple(tree, minKey, maxKey, descending, keyType, ref stream, ref clusterAccessor, layout, maxEntries);
            }
            else
            {
                FillUnique(tree, minKey, maxKey, descending, keyType, ref stream, ref clusterAccessor, layout, maxEntries);
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }
    }

    private static void FillUnique<TKey>(BTree<TKey, PersistentStore> tree, TKey minKey, TKey maxKey, bool descending, KeyType keyType,
        ref ArchetypeSortedStream stream, ref ChunkAccessor<PersistentStore> clusterAccessor, ArchetypeClusterInfo layout, int maxEntries) where TKey : unmanaged
    {
        using var enumerator = descending ? tree.EnumerateRangeDescending(minKey, maxKey) : tree.EnumerateRange(minKey, maxKey);

        while (enumerator.MoveNext())
        {
            var item = enumerator.Current;
            long orderedKey = KeyToOrderedLong(item.Key, keyType);
            long entityPK = ResolveEntityPK(item.Value, ref clusterAccessor, layout);

            AppendEntry(ref stream, orderedKey, entityPK);

            if (maxEntries > 0 && stream._count >= maxEntries)
            {
                break;
            }
        }
    }

    private static void FillMultiple<TKey>(BTree<TKey, PersistentStore> tree, TKey minKey, TKey maxKey, bool descending, KeyType keyType,
        ref ArchetypeSortedStream stream, ref ChunkAccessor<PersistentStore> clusterAccessor, ArchetypeClusterInfo layout, int maxEntries) where TKey : unmanaged
    {
        using var enumerator = descending ? tree.EnumerateRangeMultipleDescending(minKey, maxKey) : tree.EnumerateRangeMultiple(minKey, maxKey);

        while (enumerator.MoveNextKey())
        {
            long orderedKey = KeyToOrderedLong(enumerator.CurrentKey, keyType);
            do
            {
                var values = enumerator.CurrentValues;
                for (int i = 0; i < values.Length; i++)
                {
                    long entityPK = ResolveEntityPK(values[i], ref clusterAccessor, layout);
                    AppendEntry(ref stream, orderedKey, entityPK);

                    if (maxEntries > 0 && stream._count >= maxEntries)
                    {
                        return;
                    }
                }
            }
            while (enumerator.NextChunk());
        }
    }

    /// <summary>Resolve a ClusterLocation (packed int) to an EntityPK by reading the cluster's EntityIds array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ResolveEntityPK(int clusterLocation, ref ChunkAccessor<PersistentStore> clusterAccessor, ArchetypeClusterInfo layout)
    {
        int chunkId = clusterLocation >> 6;
        int slotIndex = clusterLocation & 0x3F;
        byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId);
        Debug.Assert(clusterBase != null, $"Cluster chunk {chunkId} not accessible");
        return *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
    }

    /// <summary>Append an entry to the stream, growing buffers if needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendEntry(ref ArchetypeSortedStream stream, long orderedKey, long entityPK)
    {
        if (stream._count >= stream._orderedKeys.Length)
        {
            GrowBuffers(ref stream);
        }

        stream._orderedKeys[stream._count] = orderedKey;
        stream._entityPKs[stream._count] = entityPK;
        stream._count++;
    }

    private static void GrowBuffers(ref ArchetypeSortedStream stream)
    {
        int newCapacity = stream._orderedKeys.Length * 2;
        var newKeys = ArrayPool<long>.Shared.Rent(newCapacity);
        var newPKs = ArrayPool<long>.Shared.Rent(newCapacity);

        Array.Copy(stream._orderedKeys, newKeys, stream._count);
        Array.Copy(stream._entityPKs, newPKs, stream._count);

        ArrayPool<long>.Shared.Return(stream._orderedKeys);
        ArrayPool<long>.Shared.Return(stream._entityPKs);

        stream._orderedKeys = newKeys;
        stream._entityPKs = newPKs;
    }

    /// <summary>
    /// Convert a typed B+Tree key to the universal ordered-long encoding.
    /// Same sign-flip logic as <see cref="ZoneMapArray.ReadFieldAsOrderedLong"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long KeyToOrderedLong<TKey>(TKey key, KeyType keyType) where TKey : unmanaged
    {
        switch (keyType)
        {
            case KeyType.Float:
                return ZoneMapArray.FloatToOrderedLong(Unsafe.As<TKey, float>(ref key));
            case KeyType.Double:
                return ZoneMapArray.DoubleToOrderedLong(Unsafe.As<TKey, double>(ref key));
            case KeyType.UShort:
                return Unsafe.As<TKey, ushort>(ref key) ^ (1L << 15);
            case KeyType.UInt:
                return Unsafe.As<TKey, uint>(ref key) ^ (1L << 31);
            case KeyType.ULong:
                return Unsafe.As<TKey, long>(ref key) ^ long.MinValue;
            case KeyType.Byte:
                return Unsafe.As<TKey, byte>(ref key);
            default:
                // Signed integer types (sbyte, short, int, long): direct widening preserves order
                return keyType switch
                {
                    KeyType.SByte => Unsafe.As<TKey, sbyte>(ref key),
                    KeyType.Short => Unsafe.As<TKey, short>(ref key),
                    KeyType.Int => Unsafe.As<TKey, int>(ref key),
                    KeyType.Long => Unsafe.As<TKey, long>(ref key),
                    _ => 0
                };
        }
    }

    public void Dispose()
    {
        if (_orderedKeys != null)
        {
            ArrayPool<long>.Shared.Return(_orderedKeys);
            _orderedKeys = null;
        }

        if (_entityPKs != null)
        {
            ArrayPool<long>.Shared.Return(_entityPKs);
            _entityPKs = null;
        }
    }
}

/// <summary>
/// K-way merge of K sorted <see cref="ArchetypeSortedStream"/> instances.
/// Uses a binary min-heap (or max-heap for descending) to efficiently yield entries in global sort order.
/// Supports early termination for Skip/Take pagination.
/// </summary>
internal struct KWayMergeState : IDisposable
{
    private ArchetypeSortedStream[] _streams;
    private int _streamCount;       // Actual number of streams (array may be larger if rented)
    private int[] _heap;            // Heap of stream indices, rented from ArrayPool to avoid GC allocation
    private int _heapSize;
    private bool _descending;
    private bool _ownsStreamsArray; // True if _streams was rented from ArrayPool

    /// <summary>
    /// Initialize the merge state from K pre-filled streams.
    /// Builds the initial heap from all non-empty streams.
    /// </summary>
    /// <param name="streams">Array of streams (may be larger than streamCount if rented from ArrayPool).</param>
    /// <param name="streamCount">Actual number of valid streams in the array.</param>
    /// <param name="descending">True for descending sort order.</param>
    /// <param name="ownsArray">True if the array was rented from ArrayPool and should be returned on Dispose.</param>
    public static KWayMergeState Create(ArchetypeSortedStream[] streams, int streamCount, bool descending, bool ownsArray = false)
    {
        int heapCapacity = streamCount <= 16 ? 16 : streamCount;
        var state = new KWayMergeState
        {
            _streams = streams,
            _streamCount = streamCount,
            _heap = ArrayPool<int>.Shared.Rent(heapCapacity),
            _heapSize = 0,
            _descending = descending,
            _ownsStreamsArray = ownsArray
        };

        // Insert all non-empty streams into the heap
        for (int i = 0; i < streamCount; i++)
        {
            if (streams[i].HasCurrent)
            {
                state._heap[state._heapSize] = i;
                state._heapSize++;
            }
        }

        // Build heap bottom-up (O(K))
        for (int i = state._heapSize / 2 - 1; i >= 0; i--)
        {
            state.SiftDown(i);
        }

        return state;
    }

    /// <summary>
    /// Pop the next entry from the merged stream.
    /// Returns false when all streams are exhausted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext(out long entityPK)
    {
        if (_heapSize == 0)
        {
            entityPK = 0;
            return false;
        }

        int topStream = _heap[0];
        entityPK = _streams[topStream].CurrentEntityPK;

        // Advance the stream that yielded the current entry
        if (_streams[topStream].Advance())
        {
            // Stream has more entries — re-heapify from root
            SiftDown(0);
        }
        else
        {
            // Stream exhausted — remove from heap
            _heapSize--;
            if (_heapSize > 0)
            {
                _heap[0] = _heap[_heapSize];
                SiftDown(0);
            }
        }

        return true;
    }

    /// <summary>Peek at the current top key without consuming it.</summary>
    public long PeekKey => _heapSize > 0 ? _streams[_heap[0]].CurrentKey : 0;

    public bool IsEmpty => _heapSize == 0;

    private void SiftDown(int i)
    {
        while (true)
        {
            int left = 2 * i + 1;
            int right = 2 * i + 2;
            int best = i;

            if (left < _heapSize && IsHigherPriority(left, best))
            {
                best = left;
            }
            if (right < _heapSize && IsHigherPriority(right, best))
            {
                best = right;
            }
            if (best == i)
            {
                break;
            }

            // Swap
            (_heap[i], _heap[best]) = (_heap[best], _heap[i]);
            i = best;
        }
    }

    /// <summary>
    /// Returns true if heap position a has higher priority (should be closer to root) than b.
    /// For ascending: smaller key = higher priority. For descending: larger key = higher priority.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsHigherPriority(int a, int b)
    {
        long keyA = _streams[_heap[a]].CurrentKey;
        long keyB = _streams[_heap[b]].CurrentKey;
        return _descending ? keyA > keyB : keyA < keyB;
    }

    public void Dispose()
    {
        if (_streams != null)
        {
            for (int i = 0; i < _streamCount; i++)
            {
                _streams[i].Dispose();
            }

            if (_ownsStreamsArray)
            {
                ArrayPool<ArchetypeSortedStream>.Shared.Return(_streams, true);
            }

            _streams = null;
        }

        if (_heap != null)
        {
            ArrayPool<int>.Shared.Return(_heap);
            _heap = null;
        }
    }
}
