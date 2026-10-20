using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// The page walk of a point's image (04 §1): a k-way merge of the chain's sorted INDEX streams that yields, for each page of <c>[0, N)</c> in
/// ascending order, the entry of the NEWEST pack holding it, or a hole when no pack does. Shared by the materialiser (restore) and the compactor
/// (#1415), so both read a point the same way.
/// </summary>
/// <remarks>
/// Every INDEX is streamed through a bounded buffer and checked as it goes (<see cref="PackIndexStream"/>); a stream is proven only once it ends on its
/// <c>IndexCRC</c>, which <see cref="Complete"/> does after the last page. Frames are read through one <see cref="FrameReader"/> per pack, the read
/// budget split across them. Single-threaded; nothing is allocated per page.
/// </remarks>
internal sealed class ChainPageCursor : IDisposable
{
    private readonly IReadOnlyList<PackReader> _chain;
    private readonly PackIndexStream[] _streams;
    private readonly FrameReader[] _readers;
    private readonly PackPageEntry[] _heads;
    private readonly PriorityQueue<int, uint> _heap;
    private int _next;

    /// <param name="chain">The point's packs, base first.</param>
    /// <param name="readBudgetBytes">Read-window memory, split across the packs (one frame each at the least).</param>
    public ChainPageCursor(IReadOnlyList<PackReader> chain, long readBudgetBytes)
    {
        _chain = chain;
        var k = chain.Count;
        ImagePages = chain[^1].Header.ImagePages;
        _streams = new PackIndexStream[k];
        _readers = new FrameReader[k];
        _heads = new PackPageEntry[k];
        _heap = new PriorityQueue<int, uint>(k);
        var window = FrameReader.WindowFor(readBudgetBytes, k);
        try
        {
            for (var i = 0; i < k; i++)
            {
                _streams[i] = chain[i].OpenIndexStream();
                _readers[i] = new FrameReader(chain[i], window);
                if (_streams[i].TryNext(out _heads[i]))
                {
                    _heap.Enqueue(i, _heads[i].PageId);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The point's image size in pages: the newest pack's <c>N</c>.</summary>
    public int ImagePages { get; }

    /// <summary>The pack at chain position <paramref name="packIndex"/> (0 = the base).</summary>
    public PackReader Pack(int packIndex) => _chain[packIndex];

    /// <summary>The frame reader of the pack at chain position <paramref name="packIndex"/>.</summary>
    public FrameReader Frames(int packIndex) => _readers[packIndex];

    /// <summary>
    /// The next page of the image: its id, the chain position of the newest pack holding it (<c>-1</c> for a hole, a page no pack holds) and that
    /// pack's entry. <c>false</c> once every page of <c>[0, N)</c> was returned.
    /// </summary>
    public bool MoveNext(out int pageId, out int packIndex, out PackPageEntry entry)
    {
        pageId = _next;
        packIndex = -1;
        entry = default;
        if (_next >= ImagePages)
        {
            return false;
        }

        // Every pack holding the page sits at the top of the heap now: take them all, keep the newest (the highest chain position).
        while (_heap.TryPeek(out var i, out var id) && id == (uint)_next)
        {
            _heap.Dequeue();
            if (i > packIndex)
            {
                packIndex = i;
                entry = _heads[i];
            }

            if (_streams[i].TryNext(out _heads[i]))
            {
                _heap.Enqueue(i, _heads[i].PageId);
            }
        }

        _next++;
        return true;
    }

    /// <summary>
    /// After the last page: proves every INDEX. An entry left past <c>N</c> is one no page consumed (corrupt); each stream must then end on its
    /// <c>IndexCRC</c>. Throws <see cref="BackupPackCorruptException"/>.
    /// </summary>
    public void Complete()
    {
        for (var i = 0; i < _streams.Length; i++)
        {
            if (_heap.Count > 0 || _streams[i].TryNext(out _))
            {
                PackFormat.ThrowCorrupt(_chain[i].Path, "Index", -1, "entries beyond the point's image");
            }
        }
    }

    public void Dispose()
    {
        foreach (var r in _readers)
        {
            r?.Dispose();
        }

        foreach (var s in _streams)
        {
            s?.Dispose();
        }
    }
}
