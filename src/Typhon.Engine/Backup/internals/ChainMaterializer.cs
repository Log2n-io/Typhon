using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Reads one pack's page frames through a forward read window. A pack's <c>DataOffset</c> grows with <c>PageId</c> except for copy-before-write
/// pre-images, written where they arrived: reading in page order is a sequential stream with a few frames out of place.
/// </summary>
/// <remarks>
/// A frame in the window comes from memory. A miss just ahead of it (within half a window past its end: the stream moving on, perhaps over a few
/// out-of-place frames) slides the window to it. Any other miss — behind the window, or far ahead (an out-of-place pre-image) — reads that frame
/// alone and keeps the window. Reloading the window there cost two whole windows per out-of-place frame: one to reach it, one to come back.
/// </remarks>
internal sealed class FrameReader : IDisposable
{
    /// <summary>The smallest useful window: one whole frame of an incompressible page.</summary>
    internal const int MinimumWindowBytes = PackFormat.FramePrefixSize + PackFormat.PageSize;

    private readonly PackReader _pack;
    private NativeBuffer _window;
    private NativeBuffer _single;
    private long _windowStart;
    private int _windowLength;

    public FrameReader(PackReader pack, int windowBytes)
    {
        _pack = pack;
        // Never larger than the pack's data region: a small pack in a shallow chain would otherwise take the whole budget share.
        var data = pack.Footer.DataRegionEnd - PackFormat.HeaderSize;
        var size = (int)Math.Min(windowBytes, Math.Max(data, 0));
        // Engine-free (a restore or a deep verify has no resource tree): NativeBuffer's direct, annotated path.
        _window = NativeBuffer.Allocate(Math.Max(size, MinimumWindowBytes), null, null);
        _windowStart = -1;
    }

    public PackReader Pack => _pack;

    /// <summary>Decodes and checks the page <paramref name="entry"/> names into <paramref name="destination"/>; reports damage, never throws it.</summary>
    public bool TryRead(in PackPageEntry entry, Span<byte> destination, out string error)
    {
        if (entry.StoredLength == 0)
        {
            return _pack.TryReadPage(entry, destination, out error);
        }

        if (!TryReadFrame(entry, out var frame, out error))
        {
            destination[..PackFormat.PageSize].Clear();
            return false;
        }

        return PackReader.DecodePage(entry, frame, destination, out error);
    }

    /// <summary>
    /// The stored frame of a page with a frame (<c>StoredLength &gt; 0</c>), length prefix included, as the pack holds it: a slice of this reader's
    /// window, valid until its next read. Nothing is decoded or checked here (the caller does, <see cref="PackReader.DecodePage"/>); <c>false</c> when
    /// the frame runs past the data region. The compactor copies frames through this (DL-2).
    /// </summary>
    public bool TryReadFrame(in PackPageEntry entry, out ReadOnlySpan<byte> frame, out string error)
    {
        var start = entry.DataOffset - PackFormat.FramePrefixSize;
        var length = PackFormat.FramePrefixSize + (int)entry.StoredLength;
        error = null;
        if (_windowStart >= 0 && start >= _windowStart && start + length <= _windowStart + _windowLength)
        {
            frame = _window.Span.Slice((int)(start - _windowStart), length);
            return true;
        }

        var windowEnd = _windowStart + _windowLength;
        if (_windowStart < 0 || (start >= _windowStart && start < windowEnd + (_window.Length / 2)))
        {
            // The stream moving on: slide the window to this frame.
            var n = (int)Math.Min(_window.Length, _pack.Footer.DataRegionEnd - start);
            if (n < length || !_pack.ReadAt(_window.Span[..n], start))
            {
                return RunsPast(out frame, out error);
            }

            _windowStart = start;
            _windowLength = n;
            frame = _window.Span[..length];
            return true;
        }

        // Out of place: this frame alone, the window kept for the stream.
        _single ??= NativeBuffer.Allocate(MinimumWindowBytes, null, null);
        if (start + length > _pack.Footer.DataRegionEnd || !_pack.ReadAt(_single.Span[..length], start))
        {
            return RunsPast(out frame, out error);
        }

        frame = _single.Span[..length];
        return true;
    }

    private bool RunsPast(out ReadOnlySpan<byte> frame, out string error)
    {
        frame = default;
        error = "frame runs past the data region";
        _windowStart = -1;
        return false;
    }

    /// <summary>
    /// The window per pack for a read budget shared by <paramref name="packs"/> packs: the budget honoured whatever the chain length (a frame per
    /// pack at the least), and never more than 64 MiB.
    /// </summary>
    internal static int WindowFor(long readBudgetBytes, int packs) => (int)Math.Clamp(readBudgetBytes / Math.Max(packs, 1), MinimumWindowBytes, 64 << 20);

    public void Dispose()
    {
        ((IDisposable)_window)?.Dispose();
        _window = null;
        ((IDisposable)_single)?.Dispose();
        _single = null;
    }
}

/// <summary>How to materialise a point.</summary>
internal sealed class MaterializeOptions
{
    public static readonly MaterializeOptions Default = new();

    /// <summary>Total read-window memory, split across the chain's packs (one frame each at the least).</summary>
    public long ReadBudgetBytes { get; init; } = 64L << 20;

    /// <summary>Forensics only: a page that fails its check is written as stored (or zeros when it does not decode) and listed, instead of failing.</summary>
    public bool AllowDamaged { get; init; }

    public CancellationToken Cancellation { get; init; }

    /// <summary>Called with the pages written so far, after each buffer flush.</summary>
    public Action<long> Progress { get; init; }
}

/// <summary>A page that failed its check, written anyway under <see cref="MaterializeOptions.AllowDamaged"/>.</summary>
internal readonly record struct DamagedPage(int PageId, string PackFile, string Reason);

/// <summary>What a materialisation did.</summary>
internal sealed class MaterializeResult
{
    public int Pages { get; init; }

    /// <summary>Pages written as zeros: holes no pack of the chain holds, and pages stored as all-zero.</summary>
    public int ZeroPages { get; init; }

    public IReadOnlyList<DamagedPage> Damaged { get; init; }

    public long BytesRead { get; init; }

    public long BytesWritten { get; init; }
}

/// <summary>
/// Writes a point's image into a target file (plan 1413 §2.7, 04 §1): <see cref="ChainPageCursor"/> yields, for each page in ascending order, the entry
/// of the NEWEST pack that holds it; a page no pack holds is zeros. The target is written strictly ascending and
/// completely, so its length is exactly <c>N × 8 KiB</c> and NTFS never zero-fills a gap (DC-11).
/// </summary>
internal static class ChainMaterializer
{
    private const int WriteBufferBytes = 4 << 20;

    /// <summary>
    /// Materialises the point whose chain is <paramref name="chain"/> (base first). Every page is decoded and checked against its
    /// <c>PageCrc</c>, every INDEX against its <c>IndexCRC</c>; any failure throws <see cref="BackupPackCorruptException"/> unless
    /// <see cref="MaterializeOptions.AllowDamaged"/>. The target is flushed before this returns.
    /// </summary>
    public static MaterializeResult Materialize(IReadOnlyList<PackReader> chain, IStoreFile target, MaterializeOptions options = null)
    {
        options ??= MaterializeOptions.Default;
        var n = chain[^1].Header.ImagePages;
        var damaged = new List<DamagedPage>();
        var buffer = NativeBuffer.Allocate(WriteBufferBytes, null, null);   // restore runs with no engine
        long bytesWritten = 0;
        var zeroPages = 0;
        ChainPageCursor cursor = null;
        try
        {
            cursor = new ChainPageCursor(chain, options.ReadBudgetBytes);
            var fill = 0;
            long flushedPages = 0;
            while (cursor.MoveNext(out var p, out var newest, out var entry))
            {
                var page = buffer.Span.Slice(fill, PackFormat.PageSize);
                if (newest < 0)
                {
                    page.Clear();
                    zeroPages++;
                }
                else
                {
                    if (entry.StoredLength == 0)
                    {
                        zeroPages++;
                    }

                    if (!cursor.Frames(newest).TryRead(entry, page, out var error))
                    {
                        if (!options.AllowDamaged)
                        {
                            PackFormat.ThrowCorrupt(chain[newest].Path, "Page", p, error);
                        }

                        damaged.Add(new DamagedPage(p, chain[newest].Path, error));
                    }
                }

                fill += PackFormat.PageSize;
                if (fill == buffer.Length)
                {
                    target.Write(buffer.Span[..fill], flushedPages * PackFormat.PageSize);
                    bytesWritten += fill;
                    flushedPages += fill / PackFormat.PageSize;
                    fill = 0;
                    options.Cancellation.ThrowIfCancellationRequested();
                    options.Progress?.Invoke(flushedPages);
                }
            }

            if (fill > 0)
            {
                target.Write(buffer.Span[..fill], flushedPages * PackFormat.PageSize);
                bytesWritten += fill;
                flushedPages += fill / PackFormat.PageSize;
                options.Progress?.Invoke(flushedPages);
            }

            // Entries are proven only once their stream ends on a matching IndexCRC; an entry past N would be one no page consumed.
            cursor.Complete();
            target.Flush();
            long bytesRead = 0;
            foreach (var pack in chain)
            {
                bytesRead += pack.BytesRead;
            }

            return new MaterializeResult { Pages = n, ZeroPages = zeroPages, Damaged = damaged, BytesRead = bytesRead, BytesWritten = bytesWritten };
        }
        finally
        {
            ((IDisposable)buffer).Dispose();
            cursor?.Dispose();
        }
    }
}
