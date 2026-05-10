using JetBrains.Annotations;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Typhon.Engine.Internals;

[PublicAPI]
[ExcludeFromCodeCoverage]
internal ref struct RevisionWalker
{
    private ref ChunkAccessor<PersistentStore> _accessor;
    private readonly int _firstChunkId;
    private readonly ref CompRevStorageHeader _header;
    private Span<CompRevStorageElement> _elements;
    private ref int _nextChunkId;
    private int _curChunkId;

    public ref CompRevStorageHeader Header => ref _header;
    public int CurChunkId => _curChunkId;
    public ref int NextChunkId => ref _nextChunkId;
    public Span<CompRevStorageElement> Elements => _elements;

    public unsafe RevisionWalker(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId)
    {
        _accessor = ref accessor;
        _firstChunkId = firstChunkId;
        _header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId);
        _curChunkId = firstChunkId;
        var chunkSpan = accessor.GetChunkAsSpan(firstChunkId, false);
        _nextChunkId = ref chunkSpan.Cast<byte, int>()[0];
        _elements = chunkSpan.Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>();
    }

    public bool Step(int stepCount, bool loop, out bool hasLopped)
    {
        hasLopped = false;
        for (int i = 0; i < stepCount; i++)
        {
            if (_nextChunkId == 0 && !loop)
            {
                return false;
            }
            var nextChunkId = _nextChunkId;
            if (_nextChunkId == 0)
            {
                hasLopped = true;
                nextChunkId = _firstChunkId;
            }

            _curChunkId = nextChunkId;
            var chunkSpan = _accessor.GetChunkAsSpan(nextChunkId, false);
            _nextChunkId = ref chunkSpan.Cast<byte, int>()[0];
            _elements = chunkSpan.Slice(sizeof(int)).Cast<byte, CompRevStorageElement>().Slice(0, ComponentRevisionManager.CompRevCountInNext);
        }
        return true;
    }

}
