using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Sequential reader for WAL segment files during crash recovery. Opens a segment, validates the header,
/// and iterates chunks with CRC chain validation. Stops at end-of-data or CRC break (truncation).
/// </summary>
internal sealed class WalSegmentReader : IDisposable
{
    private const int PageSize = 4096;

    private readonly IWalFileIO _fileIO;

    // Staging buffer for aligned reads
    private byte[] _readBuffer;
    private readonly int _readBufferSize;

    // Segment data loaded into memory
    private byte[] _segmentData;
    private int _segmentDataLength;

    // Frame-level iteration
    private int _frameOffset;         // Start of current frame (WalFrameHeader position)
    private int _frameEnd;            // End of current frame
    private int _recordsRemainingInFrame;

    // Record-level iteration within a frame
    private int _recordOffset;        // Current chunk position within frame

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
        _readBufferSize = 256 * 1024; // 256KB read buffer
        _readBuffer = GC.AllocateArray<byte>(_readBufferSize, true);
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

        // Read the header (first 4096 bytes)
        var headerBuffer = new byte[WalSegmentHeader.SizeInBytes];
        _fileIO.ReadAligned(handle, 0, headerBuffer);

        _segmentHeader = MemoryMarshal.Read<WalSegmentHeader>(headerBuffer);

        if (!_segmentHeader.Validate())
        {
            return false;
        }

        // Read the full segment data area (after header) into memory
        var dataSize = (int)_segmentHeader.SegmentSize - WalSegmentHeader.SizeInBytes;
        if (dataSize <= 0)
        {
            _segmentData = [];
            _segmentDataLength = 0;
        }
        else
        {
            _segmentData = new byte[dataSize];
            var remaining = dataSize;
            var fileOffset = (long)WalSegmentHeader.SizeInBytes;
            var destOffset = 0;

            while (remaining > 0)
            {
                var toRead = Math.Min(remaining, _readBufferSize);
                var alignedRead = AlignUp(toRead, PageSize);
                if (alignedRead > _readBufferSize)
                {
                    alignedRead = _readBufferSize;
                    toRead = alignedRead;
                }

                _fileIO.ReadAligned(handle, fileOffset, _readBuffer.AsSpan(0, alignedRead));

                var toCopy = Math.Min(toRead, remaining);
                _readBuffer.AsSpan(0, toCopy).CopyTo(_segmentData.AsSpan(destOffset));

                fileOffset += alignedRead;
                destOffset += toCopy;
                remaining -= toCopy;
            }

            _segmentDataLength = dataSize;
        }

        _opened = true;
        _frameOffset = 0;
        _frameEnd = 0;
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

        if (!_opened || _segmentData == null)
        {
            return false;
        }

        // Advance to next frame if we've consumed all chunks in the current one
        while (_recordsRemainingInFrame == 0)
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

        chunkHeader = Unsafe.As<byte, WalChunkHeader>(ref _segmentData[_recordOffset]);

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
        var footerCrc = Unsafe.As<byte, uint>(ref _segmentData[footerOffset]);

        // Compute CRC over [0, ChunkSize - 4) — header + body
        var crcSpan = _segmentData.AsSpan(_recordOffset, chunkHeader.ChunkSize - WalChunkFooter.SizeInBytes);
        var computedCrc = WalCrc.Compute(crcSpan);

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
        body = _segmentData.AsSpan(bodyStart, bodyLen);

        // Read LSN from body[0..8] (convention: LSN is always at body offset 0)
        if (bodyLen >= sizeof(long))
        {
            LastValidLSN = Unsafe.As<byte, long>(ref _segmentData[bodyStart]);
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
        // If we were mid-frame, jump to end of current frame
        if (_frameEnd > 0)
        {
            _frameOffset = _frameEnd;
        }

        while (_frameOffset < _segmentDataLength)
        {
            if (_frameOffset + WalFrameHeader.SizeInBytes > _segmentDataLength)
            {
                return false;
            }

            ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref _segmentData[_frameOffset]);

            // Zero frame length = end-of-data (not yet published)
            if (frameHeader.FrameLength == 0)
            {
                return false;
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
        }

        return false;
    }

    public void Dispose()
    {
        _segmentData = null;
        _readBuffer = null;
        _opened = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
