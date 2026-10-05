using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

[PublicAPI]
internal ref struct ComponentRevision
{
    private ref ChunkAccessor<PersistentStore> _accessor;
    private readonly ComponentInfo _info;
    private readonly int _firstChunkId;
    private readonly ushort _uowId;
    private readonly ref ComponentInfo.CompRevInfo _compRevInfo;

    internal ComponentRevision(ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, int firstChunkId, ref ChunkAccessor<PersistentStore> accessor, ushort uowId = 0)
    {
        _accessor = ref accessor;
        _info = info;
        _firstChunkId = firstChunkId;
        _uowId = uowId;
        _compRevInfo = ref compRevInfo;
    }

    internal short LastCommitRevisionIndex => _accessor.GetChunk<CompRevStorageHeader>(_firstChunkId).LastCommitRevisionIndex;
    internal void SetLastCommitRevisionIndex(short index) => _accessor.GetChunk<CompRevStorageHeader>(_firstChunkId, true).LastCommitRevisionIndex = index;

    internal int CommitSequence => _accessor.GetChunk<CompRevStorageHeader>(_firstChunkId).CommitSequence;
    internal void IncrementCommitSequence()
    {
        ref var header = ref _accessor.GetChunk<CompRevStorageHeader>(_firstChunkId, true);
        header.CommitSequence++;
    }

    internal ComponentRevisionManager.ElementRevisionHandle GetRevisionElement(short revisionIndex)
        => ComponentRevisionManager.GetRevisionElement(ref _accessor, _firstChunkId, revisionIndex);
    internal void AddCompRev(long tsn, bool isDelete)
        => ComponentRevisionManager.AddCompRev(_info, ref _compRevInfo, tsn, _uowId, isDelete);
    internal int AllocCompRevStorage(long tsn, long pk) => ComponentRevisionManager.AllocCompRevStorage(_info, tsn, _uowId, _firstChunkId, pk);
    /// <summary>
    /// Voids a rolled-back entry, then drops the void entries at the END of the chain. Caller holds the chain's exclusive lock.
    /// </summary>
    /// <remarks>
    /// Only the tail may shrink. <c>ItemCount</c> bounds the range [FirstItemIndex, FirstItemIndex + ItemCount): decrementing it for an entry that is NOT
    /// last does not remove that entry, it removes the LAST one — a later transaction's entry, often its committed head, which every walk then stops seeing
    /// and the next append overwrites (#696). A void left in the middle is harmless: walks skip it and cleanup reclaims it. A void left at the tail is not:
    /// cleanup takes it for the newest kept entry and frees the committed sentinel before it, so the loop trims every trailing void, not just this one.
    /// </remarks>
    public void VoidElement(ComponentRevisionManager.ElementRevisionHandle elementRevisionHandle)
    {
        elementRevisionHandle.Element.Void();

        ref var firstHeader = ref _accessor.GetChunk<CompRevStorageHeader>(_firstChunkId, true);
        while (firstHeader.ItemCount > 1 && GetRevisionElement((short)(firstHeader.FirstItemIndex + firstHeader.ItemCount - 1)).Element.IsVoid)
        {
            --firstHeader.ItemCount;
        }
    }
}
