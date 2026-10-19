using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Reads one pack's page frames through a forward read window: frames near the last one read come from memory, any other costs one positional
/// read of up to the window size starting at the frame. A pack's <c>DataOffset</c> grows with <c>PageId</c> except for copy-before-write
/// pre-images, so reads in page order stay near-sequential.
/// </summary>
internal sealed class FrameReader : IDisposable
{
    private readonly PackReader _pack;
    private byte[] _window;
    private long _windowStart;
    private int _windowLength;

    public FrameReader(PackReader pack, int windowBytes)
    {
        _pack = pack;
        // Never larger than the pack's data region: a small pack in a shallow chain would otherwise take the whole budget share.
        var data = pack.Footer.DataRegionEnd - PackFormat.HeaderSize;
        var size = (int)Math.Min(windowBytes, Math.Max(data, 0));
        _window = ArrayPool<byte>.Shared.Rent(Math.Max(size, PackFormat.FramePrefixSize + PackFormat.PageSize));
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

        var start = entry.DataOffset - PackFormat.FramePrefixSize;
        var length = PackFormat.FramePrefixSize + (int)entry.StoredLength;
        if (_windowStart < 0 || start < _windowStart || start + length > _windowStart + _windowLength)
        {
            var available = _pack.Footer.DataRegionEnd - start;
            var n = (int)Math.Min(_window.Length, available);
            if (n < length || !_pack.ReadAt(_window.AsSpan(0, n), start))
            {
                destination[..PackFormat.PageSize].Clear();
                error = "frame runs past the data region";
                _windowStart = -1;
                return false;
            }

            _windowStart = start;
            _windowLength = n;
        }

        return PackReader.DecodePage(entry, _window.AsSpan((int)(start - _windowStart), length), destination, out error);
    }

    public void Dispose()
    {
        if (_window != null)
        {
            ArrayPool<byte>.Shared.Return(_window);
            _window = null;
        }
    }
}

/// <summary>How to materialise a point.</summary>
internal sealed class MaterializeOptions
{
    public static readonly MaterializeOptions Default = new();

    /// <summary>Total read-window memory, split across the chain's packs (at least 256 KiB each).</summary>
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
/// Writes a point's image into a target file (plan 1413 §2.7, 04 §1): a k-way merge of the chain's sorted INDEX streams yields, for each page in
/// ascending order, the entry of the NEWEST pack that holds it; a page no pack holds is zeros. The target is written strictly ascending and
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
        var k = chain.Count;
        var n = chain[^1].Header.ImagePages;
        var window = (int)Math.Clamp(options.ReadBudgetBytes / k, 256 << 10, 64 << 20);
        var streams = new PackIndexStream[k];
        var readers = new FrameReader[k];
        var heads = new PackPageEntry[k];
        var heap = new PriorityQueue<int, uint>(k);
        var damaged = new List<DamagedPage>();
        var buffer = new NativeBuffer(WriteBufferBytes);
        long bytesWritten = 0;
        var zeroPages = 0;
        try
        {
            for (var i = 0; i < k; i++)
            {
                streams[i] = chain[i].OpenIndexStream();
                readers[i] = new FrameReader(chain[i], window);
                if (streams[i].TryNext(out heads[i]))
                {
                    heap.Enqueue(i, heads[i].PageId);
                }
            }

            var fill = 0;
            long flushedPages = 0;
            for (var p = 0; p < n; p++)
            {
                // Every pack holding p sits at the top of the heap now: take them all, keep the newest (the highest chain index).
                var newest = -1;
                PackPageEntry entry = default;
                while (heap.TryPeek(out var i, out var id) && id == (uint)p)
                {
                    heap.Dequeue();
                    if (i > newest)
                    {
                        newest = i;
                        entry = heads[i];
                    }

                    if (streams[i].TryNext(out heads[i]))
                    {
                        heap.Enqueue(i, heads[i].PageId);
                    }
                }

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

                    if (!readers[newest].TryRead(entry, page, out var error))
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
            for (var i = 0; i < k; i++)
            {
                if (heap.Count > 0 || streams[i].TryNext(out _))
                {
                    PackFormat.ThrowCorrupt(chain[i].Path, "Index", -1, "entries beyond the point's image");
                }
            }

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
            foreach (var r in readers)
            {
                r?.Dispose();
            }

            foreach (var s in streams)
            {
                s?.Dispose();
            }
        }
    }
}
