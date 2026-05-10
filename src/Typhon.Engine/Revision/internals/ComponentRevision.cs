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
    public void VoidElement(ComponentRevisionManager.ElementRevisionHandle elementRevisionHandle)
    {
        ref var firstHeader = ref _accessor.GetChunk<CompRevStorageHeader>(_firstChunkId, true);
        --firstHeader.ItemCount;
        elementRevisionHandle.Element.Void();
    }
}
