// unset

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Engine.Internals;

internal class StringTableSegment<TStore> where TStore : struct, IPageStore
{
    private struct ChunkHeader
    {
        public int SizeLeft;
        public int NextChunkId;
    }

    public int Stride { get; }

    private readonly EpochManager _epochManager;
    private readonly ChunkBasedSegment<TStore> _segment;

    public StringTableSegment(ChunkBasedSegment<TStore> segment, EpochManager epochManager)
    {
        _segment = segment;
        _epochManager = epochManager;
        Stride = segment.Stride;
    }

    unsafe public int StoreString(string str)
    {
        var byteCount = Encoding.UTF8.GetByteCount(str);
        var blockSize = Stride - sizeof(ChunkHeader);
        var chunkCount = (int)Math.Ceiling(byteCount / (double)blockSize);

        var chunks = _segment.AllocateChunks(chunkCount, false);
        var chunkIds = chunks.Memory.Span.Slice(0, chunkCount);
        var rootChunkId = chunkIds[0];

        Span<byte> utfString = stackalloc byte[byteCount];
        Encoding.UTF8.GetBytes(str.AsSpan(), utfString);

        var depth = _epochManager.EnterScope();
        try
        {
            var accessor = _segment.CreateChunkAccessor();

            var sizeLeft = byteCount;
            var curOffset = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                var chunkAddr = accessor.GetChunkAddress(chunkIds[i], true);
                ref var h = ref Unsafe.AsRef<ChunkHeader>(chunkAddr);

                var copySize = Math.Min(sizeLeft, blockSize);
                h.SizeLeft = sizeLeft;
                h.NextChunkId = (i + 1 < chunkCount) ? chunkIds[i+1] : 0;

                chunkAddr += sizeof(ChunkHeader);
                utfString.Slice(curOffset, copySize).CopyTo(new Span<byte>(chunkAddr, copySize));

                sizeLeft -= copySize;
                curOffset += copySize;
            }

            accessor.Dispose();
        }
        finally
        {
            _epochManager.ExitScope(depth);
        }

        chunks.Dispose();
        return rootChunkId;
    }

    unsafe public string LoadString(int stringId)
    {
        var depth = _epochManager.EnterScope();
        try
        {
            var accessor = _segment.CreateChunkAccessor();

            var curChunkAddr = accessor.GetChunkAddress(stringId);
            ref var curChunk = ref Unsafe.AsRef<ChunkHeader>(curChunkAddr);

            Span<byte> ustr = stackalloc byte[curChunk.SizeLeft];
            var totalSize = curChunk.SizeLeft;

            var curOffset = 0;
            var blockSize = Stride - sizeof(ChunkHeader);

            while (true)
            {
                var copySize = Math.Min(curChunk.SizeLeft, blockSize);
                new Span<byte>(curChunkAddr + sizeof(ChunkHeader), copySize).CopyTo(ustr.Slice(curOffset, copySize));

                if (curChunk.NextChunkId == 0) break;

                curOffset += copySize;
                curChunkAddr = accessor.GetChunkAddress(curChunk.NextChunkId);
                curChunk = ref Unsafe.AsRef<ChunkHeader>(curChunkAddr);
            }

            accessor.Dispose();

            fixed (byte* d = ustr)
            {
                return Marshal.PtrToStringUTF8(new IntPtr(d), totalSize);
            }
        }
        finally
        {
            _epochManager.ExitScope(depth);
        }
    }

    unsafe public void DeleteString(int stringId)
    {
        var depth = _epochManager.EnterScope();
        try
        {
            var accessor = _segment.CreateChunkAccessor();

            var curChunkId = stringId;
            var curChunkAddr = accessor.GetChunkAddress(stringId, true);
            ref var curChunk = ref Unsafe.AsRef<ChunkHeader>(curChunkAddr);

            while (true)
            {
                var nextChunkId = curChunk.NextChunkId;
                _segment.FreeChunk(curChunkId);

                if (curChunk.NextChunkId == 0) break;

                curChunkId = nextChunkId;
                curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
                curChunk = ref Unsafe.AsRef<ChunkHeader>(curChunkAddr);
            }

            accessor.Dispose();
        }
        finally
        {
            _epochManager.ExitScope(depth);
        }
    }
}
