using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Sequential reader for WAL segment files during crash recovery. Opens a segment, validates the header,
/// and iterates chunks with CRC chain validation. Stops at end-of-data or CRC break (truncation).
/// </summary>
/// <remarks>
/// A segment's data area lives in native memory zero-filled by the OS (<see cref="NativeMemory"/>), not a GC array: a segment is up to its declared size
/// (64 MiB by default), and a backup computes its point LSN with this reader on every capture, so an array per segment was large-object garbage on
/// every point. Fresh zeroed pages cost nothing until touched, and only the part <see cref="IWalFileIO.ReadableLength"/> reports is read: past it the
/// reader sees the zeros a tail view would have returned, so what it parses is unchanged. A body returned by <see cref="TryReadNext"/> points into that
/// memory and is valid until the next <see cref="OpenSegment"/> or <see cref="Dispose"/>.
/// </remarks>
internal sealed unsafe class WalSegmentReader : IDisposable
{
    private const int PageSize = 4096;
    private const int ReadChunkBytes = 4 << 20;

    private readonly IWalFileIO _fileIO;

    // The header's read buffer, page-aligned: segments are opened unbuffered on Windows
    private byte* _header;

    // Segment data loaded into memory: _data is _block aligned up to a page
    private byte* _block;
    private byte* _data;
    private int _segmentDataLength;

    private Span<byte> Data => new(_data, _segmentDataLength);

    // Frame-level iteration
    private int _frameOffset;         // Start of current frame (WalFrameHeader position)
    private int _frameEnd;            // End of current frame
    private int _recordsRemainingInFrame;

    // Record-level iteration within a frame
    private int _recordOffset;        // Current chunk position within frame

    // End of the last frame read whole (every chunk valid) or skipped as empty: where a torn segment's usable data ends. Bookkeeping only; the walk
    // itself is unchanged.
    private int _lastCompleteFrameEnd;

    // CRC chain state
    private uint _lastFooterCrc;
    private bool _isFirstChunk;
    private bool _opened;

    private WalSegmentHeader _segmentHeader;

    /// <summary>The validated segment header.</summary>
    public ref readonly WalSegmentHeader SegmentHeader => ref _segmentHeader;

    /// <summary>True if a CRC chain break was detected during iteration (indicates crash truncation).</summary>
    public bool WasTruncated { get; private set; }

    /// <summary>The LSN of the last successfully validated chunk.</summary>
    public long LastValidLSN { get; private set; }

    /// <summary>Number of chunks successfully read.</summary>
    public int RecordsRead { get; private set; }

    public WalSegmentReader(IWalFileIO fileIO)
    {
        ArgumentNullException.ThrowIfNull(fileIO);
        _fileIO = fileIO;
        // native-alloc: one page per reader, freed by Dispose; recovery runs before the engine's allocator and resource tree exist.
        _header = (byte*)NativeMemory.AlignedAlloc(WalSegmentHeader.SizeInBytes, PageSize);
    }

    /// <summary>
    /// Opens and validates a WAL segment file. Returns true if the header is valid.
    /// </summary>
    public bool OpenSegment(string path)
    {
        if (!_fileIO.Exists(path))
        {
            return false;
        }

        using var handle = _fileIO.OpenSegmentForRead(path);
        var loaded = Load(handle, long.MaxValue);
        _segmentHeader.ThrowIfAnotherFormat(path);
        return loaded;
    }

    /// <summary>
    /// Opens a sealed segment for the WAL archiver (<see cref="IWalFileIO.OpenSegmentForArchive"/>: delete sharing, WA-5), reading no further than
    /// <paramref name="writtenBytes"/> from the start of the file: past it the segment holds zeros, which the reader sees without reading them.
    /// </summary>
    internal bool OpenSegmentForArchive(string path, long writtenBytes)
    {
        if (!_fileIO.Exists(path))
        {
            return false;
        }

        using var handle = _fileIO.OpenSegmentForArchive(path);
        return Load(handle, writtenBytes);
    }

    /// <summary>The segment header's bytes as read (a page), valid until the next open.</summary>
    internal ReadOnlySpan<byte> HeaderBytes => new(_header, WalSegmentHeader.SizeInBytes);

    /// <summary>
    /// The segment's data area as loaded (zeros past what was read), valid until the next open: the archiver compresses from it, so a segment is read
    /// once. Writable, for <see cref="ZeroFrom"/> only.
    /// </summary>
    internal Span<byte> LoadedData => Data;

    /// <summary>Offset in the data area (after the header) of the frame being read, or of the one where the walk stopped.</summary>
    internal int FrameStart => _frameOffset;

    /// <summary>Offset in the data area of the end of the frame being read; 0 before the first.</summary>
    internal int FrameEnd => _frameEnd;

    /// <summary>Offset in the data area of the end of the last frame read whole, every chunk valid; 0 when none.</summary>
    internal int LastCompleteFrameEnd => _lastCompleteFrameEnd;

    /// <summary>The highest LSN the frame being read declares (<see cref="WalFrameHeader.LastLsn"/>).</summary>
    internal long FrameLastLsn => _frameEnd > 0 ? Unsafe.As<byte, WalFrameHeader>(ref Data[_frameOffset]).LastLsn : 0;

    /// <summary>Zeros the loaded data from <paramref name="offset"/> (data-relative) on: the archiver keeps no byte of a torn frame.</summary>
    internal void ZeroFrom(int offset)
    {
        if (offset < _segmentDataLength)
        {
            Data[offset..].Clear();
        }
    }

    private bool Load(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long readLimit)
    {
        // Read the header (first 4096 bytes)
        var headerBuffer = new Span<byte>(_header, WalSegmentHeader.SizeInBytes);
        headerBuffer.Clear();
        _fileIO.ReadAligned(handle, 0, headerBuffer);

        _segmentHeader = MemoryMarshal.Read<WalSegmentHeader>(headerBuffer);

        if (!_segmentHeader.Validate())
        {
            return false;
        }

        // Read the segment data area (after header) into memory: zeros past what the source can return
        var dataSize = (int)_segmentHeader.SegmentSize - WalSegmentHeader.SizeInBytes;
        FreeData();
        if (dataSize > 0)
        {
            var capacity = AlignUp(dataSize, PageSize);
            // native-alloc: lazily zeroed pages (the declared size, mostly untouched past the written part), freed at the next open or Dispose.
            _block = (byte*)NativeMemory.AllocZeroed((nuint)(capacity + PageSize));
            _data = (byte*)AlignUp((nint)_block, PageSize);
            _segmentDataLength = dataSize;

            var readable = (int)Math.Clamp(Math.Min(_fileIO.ReadableLength(handle), readLimit) - WalSegmentHeader.SizeInBytes, 0, capacity);
            var toRead = AlignUp(readable, PageSize);
            for (var offset = 0; offset < toRead; offset += ReadChunkBytes)
            {
                var chunk = new Span<byte>(_data + offset, Math.Min(ReadChunkBytes, toRead - offset));
                _fileIO.ReadAligned(handle, WalSegmentHeader.SizeInBytes + (long)offset, chunk);
            }
        }

        _opened = true;
        _frameOffset = 0;
        _frameEnd = 0;
        _lastCompleteFrameEnd = 0;
        _recordOffset = 0;
        _recordsRemainingInFrame = 0;
        _isFirstChunk = true;
        _lastFooterCrc = 0;
        WasTruncated = false;
        LastValidLSN = 0;
        RecordsRead = 0;

        return true;
    }

    /// <summary>
    /// Reads the next WAL chunk from the segment. Returns false at end-of-data or CRC break.
    /// </summary>
    /// <param name="chunkHeader">The chunk header.</param>
    /// <param name="body">The chunk body (between chunk header and footer).</param>
    /// <returns>True if a valid chunk was read; false at end-of-segment or on CRC break.</returns>
    public bool TryReadNext(out WalChunkHeader chunkHeader, out ReadOnlySpan<byte> body)
    {
        chunkHeader = default;
        body = default;

        if (!_opened || _data == null)
        {
            return false;
        }

        // Advance to the next frame when the current frame's chunk region is exhausted. Bound by OFFSET (not the frame's
        // RecordCount): WAL v2 packs multiple records per chunk, so chunkCount ≤ RecordCount — decrementing a per-chunk counter
        // would stop early. Offset-based termination is correct for both v1 (1 record/chunk) and v2.
        while (_frameEnd == 0 || _recordOffset + WalChunkHeader.SizeInBytes > _frameEnd)
        {
            if (!AdvanceToNextFrame())
            {
                return false;
            }
        }

        // Read the chunk header at _recordOffset
        if (_recordOffset + WalChunkHeader.SizeInBytes > _frameEnd)
        {
            _recordsRemainingInFrame = 0;
            return false;
        }

        chunkHeader = Unsafe.As<byte, WalChunkHeader>(ref Data[_recordOffset]);

        // Validate minimum chunk size: header (8) + footer (4) = 12
        if (chunkHeader.ChunkSize < WalChunkHeader.SizeInBytes + WalChunkFooter.SizeInBytes)
        {
            WasTruncated = true;
            return false;
        }

        // Validate chunk fits within frame
        if (_recordOffset + chunkHeader.ChunkSize > _frameEnd)
        {
            WasTruncated = true;
            return false;
        }

        // Read footer CRC
        var footerOffset = _recordOffset + chunkHeader.ChunkSize - WalChunkFooter.SizeInBytes;
        var footerCrc = Unsafe.As<byte, uint>(ref Data[footerOffset]);

        // Compute CRC over [0, ChunkSize - 4) — header + body
        var crcSpan = Data.Slice(_recordOffset, chunkHeader.ChunkSize - WalChunkFooter.SizeInBytes);
        var computedCrc = Crc32CUtil.Compute(crcSpan);

        if (computedCrc != footerCrc)
        {
            WasTruncated = true;
            return false;
        }

        // PrevCRC chain validation
        if (_isFirstChunk)
        {
            _isFirstChunk = false;
        }
        else if (chunkHeader.PrevCRC != _lastFooterCrc)
        {
            WasTruncated = true;
            return false;
        }

        // Update chain state
        _lastFooterCrc = footerCrc;

        // Extract body: bytes between chunk header and footer
        var bodyStart = _recordOffset + WalChunkHeader.SizeInBytes;
        var bodyLen = chunkHeader.ChunkSize - WalChunkHeader.SizeInBytes - WalChunkFooter.SizeInBytes;
        body = Data.Slice(bodyStart, bodyLen);

        // Read LSN from body[0..8] (convention: LSN is always at body offset 0)
        if (bodyLen >= sizeof(long))
        {
            LastValidLSN = Unsafe.As<byte, long>(ref Data[bodyStart]);
        }

        RecordsRead++;
        _recordsRemainingInFrame--;

        // Advance to next chunk
        _recordOffset += chunkHeader.ChunkSize;

        return true;
    }

    /// <summary>
    /// Advances to the next non-empty frame. Returns false at end-of-data.
    /// </summary>
    private bool AdvanceToNextFrame()
    {
        // If we were mid-frame, jump to end of current frame. Called only once its chunks are exhausted (a broken chunk returns before), so that frame
        // was read whole.
        if (_frameEnd > 0)
        {
            _frameOffset = _frameEnd;
            _lastCompleteFrameEnd = _frameEnd;
        }

        while (_frameOffset < _segmentDataLength)
        {
            if (_frameOffset + WalFrameHeader.SizeInBytes > _segmentDataLength)
            {
                return false;
            }

            ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref Data[_frameOffset]);

            // Zero frame length = either intra-block tail padding or genuine end-of-data. The WAL writer issues each drain as its
            // own O_DIRECT block, padded up to a 4096-byte boundary (WalWriter: bytesToWrite = AlignUp(data.Length, PageSize)). A
            // frame whose length is not a page multiple leaves a zero tail before the next page-aligned block — so the contiguous
            // frame walk hits a zero FrameLength mid-segment with more frames still ahead. Drain blocks are written consecutively
            // at page-aligned offsets and each block's first frame sits at its aligned start, so the next block (if any) begins at
            // the next 4096 boundary: jump the tail and keep scanning. A zero FrameLength already AT a boundary means no block was
            // ever written there (blocks are append-only with no inter-block gaps) → real end-of-data. See WR-02 in durability.md.
            if (frameHeader.FrameLength == 0)
            {
                var nextBlock = AlignUp(_frameOffset, PageSize);
                if (nextBlock == _frameOffset || nextBlock + WalFrameHeader.SizeInBytes > _segmentDataLength)
                {
                    return false;
                }

                _frameOffset = nextBlock;
                continue;
            }

            // Padding sentinel = end of usable data
            if (frameHeader.FrameLength == WalFrameHeader.PaddingSentinel)
            {
                return false;
            }

            // Validate frame bounds
            if (frameHeader.FrameLength < WalFrameHeader.SizeInBytes ||
                _frameOffset + frameHeader.FrameLength > _segmentDataLength)
            {
                WasTruncated = true;
                return false;
            }

            _frameEnd = _frameOffset + frameHeader.FrameLength;
            _recordOffset = _frameOffset + WalFrameHeader.SizeInBytes;
            _recordsRemainingInFrame = frameHeader.RecordCount;

            if (_recordsRemainingInFrame > 0)
            {
                return true;
            }

            // Empty frame (abandoned claim) — skip to next
            _frameOffset = _frameEnd;
            _lastCompleteFrameEnd = _frameEnd;
        }

        return false;
    }

    public void Dispose()
    {
        FreeData();
        if (_header != null)
        {
            NativeMemory.AlignedFree(_header);
            _header = null;
        }

        _opened = false;
    }

    private void FreeData()
    {
        if (_block != null)
        {
            NativeMemory.Free(_block);
            _block = null;
        }

        _data = null;
        _segmentDataLength = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint AlignUp(nint value, int alignment) => (value + alignment - 1) & ~(nint)(alignment - 1);
}
