using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// A snapshot's view of an EntityMap record, a pending born or died resolved through its owner's commit card (VIS-01, VIS-08, VIS-09). A published record is tested
/// exactly as before, inline; only a spawn caught between its PREPARE and its PUBLISH takes the slow path.
/// </summary>
internal static unsafe class EntityVisibility
{
    /// <summary>
    /// Whether the record copied into <paramref name="record"/> is visible at <paramref name="tsn"/>. When the owner holds no card the record is read again
    /// into <paramref name="record"/> (published since, or removed by an abort) and decided once more; gone, it is not visible.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsVisible(byte* record, long tsn, CommitCardTable cards, RawValuePagedHashMap<long, PersistentStore> map, long key,
        ref ChunkAccessor<PersistentStore> accessor, EpochManager epochManager)
    {
        ref var header = ref EntityRecordAccessor.GetHeader(record);
        return header.HasPending ? IsVisiblePending(record, tsn, cards, map, key, ref accessor, epochManager) : header.IsVisibleAt(tsn);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsVisiblePending(byte* record, long tsn, CommitCardTable cards, RawValuePagedHashMap<long, PersistentStore> map, long key,
        ref ChunkAccessor<PersistentStore> accessor, EpochManager epochManager)
    {
        var visible = EntityRecordAccessor.GetHeader(record).IsVisibleAt(tsn, cards, out var reread);
        if (!reread)
        {
            return visible;
        }

        using var guard = EpochGuard.Enter(epochManager);
        return map.TryGet(key, record, ref accessor) && EntityRecordAccessor.GetHeader(record).IsVisibleAt(tsn, cards, out _);
    }

    /// <summary>
    /// The scan form, for an entry an EntityMap enumeration hands over (<paramref name="entry"/> is not to be written): a re-read goes through an accessor
    /// of its own, into a buffer of its own.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsVisibleInScan(byte* entry, long tsn, CommitCardTable cards, RawValuePagedHashMap<long, PersistentStore> map, long key)
    {
        ref var header = ref EntityRecordAccessor.GetHeader(entry);
        return header.HasPending ? IsVisiblePendingInScan(entry, tsn, cards, map, key) : header.IsVisibleAt(tsn);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsVisiblePendingInScan(byte* entry, long tsn, CommitCardTable cards, RawValuePagedHashMap<long, PersistentStore> map, long key)
    {
        var visible = EntityRecordAccessor.GetHeader(entry).IsVisibleAt(tsn, cards, out var reread);
        if (!reread || map == null)
        {
            return visible;
        }

        byte* record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        var accessor = map.Segment.CreateChunkAccessor();
        try
        {
            return map.TryGet(key, record, ref accessor) && EntityRecordAccessor.GetHeader(record).IsVisibleAt(tsn, cards, out _);
        }
        finally
        {
            accessor.Dispose();
        }
    }
}
