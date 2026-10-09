using System;
using System.Buffers;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// One window of the page cache (<see cref="PageCacheAddressing"/>) as a <see cref="MemoryManager{T}"/>, so the async disk read and the async
/// structural write can take a <see cref="Memory{T}"/> over a page past 2 GiB from the cache base. A <see cref="Memory{T}"/> addresses its
/// manager with <c>int</c> offsets, so one manager can only reach its first 2 GiB: hence one per window, built when the cache opens and
/// immutable after.
/// </summary>
/// <remarks>
/// Native memory only, owned by the page cache: <see cref="Pin"/> hands out a pointer and <see cref="Unpin"/> does nothing, as
/// <see cref="PinnedMemoryBlock"/> does. A window outlives every I/O issued through it because it lives as long as the cache. Getting a
/// <see cref="Memory{T}"/> from it allocates nothing.
/// </remarks>
internal sealed unsafe class PageCacheWindow : MemoryManager<byte>
{
    private readonly byte* _base;
    private readonly int _length;

    internal PageCacheWindow(byte* windowBase, int length)
    {
        _base = windowBase;
        _length = length;
    }

    /// <summary><paramref name="length"/> bytes at <paramref name="offset"/> inside this window.</summary>
    internal Memory<byte> Slice(int offset, int length)
    {
        // Checked in every build: an async write may pin the memory rather than read its span, so a slice past the window's end would not
        // throw anywhere else. One compare, on a path waiting on a disk.
        if (offset < 0 || length < 0 || (long)offset + length > _length)
        {
            ThrowOutsideWindow(offset, length, _length);
        }

        return CreateMemory(offset, length);
    }

    private static void ThrowOutsideWindow(int offset, int length, int windowLength) =>
        throw new InvalidOperationException($"A page-cache I/O slice [{offset}, +{length}) is outside its {windowLength}-byte window: a run straddles windows.");

    public override Span<byte> GetSpan() => new(_base, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => new(_base + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }

    /// <summary>
    /// One window per <see cref="PageCacheAddressing.PagesPerWindowPow2"/> pages over the cache at <paramref name="cacheBase"/>, the last one
    /// possibly short.
    /// </summary>
    internal static PageCacheWindow[] Build(byte* cacheBase, int memPagesCount)
    {
        var windows = new PageCacheWindow[PageCacheAddressing.WindowCount(memPagesCount)];
        for (var w = 0; w < windows.Length; w++)
        {
            var firstPage = w << PageCacheAddressing.PagesPerWindowPow2;
            var pages = PageCacheAddressing.WindowPageCount(w, memPagesCount);
            windows[w] = new PageCacheWindow(cacheBase + firstPage * (long)PagedMMF.PageSize, pages << PagedMMF.PageSizePow2);
        }

        return windows;
    }

    /// <summary>The page <paramref name="memPageIndex"/> of the cache whose windows are <paramref name="windows"/>.</summary>
    internal static Memory<byte> PageMemory(PageCacheWindow[] windows, int memPageIndex) =>
        windows[PageCacheAddressing.WindowIndex(memPageIndex)].Slice(PageCacheAddressing.OffsetInWindow(memPageIndex), PagedMMF.PageSize);

    /// <summary>
    /// <paramref name="pageCount"/> consecutive pages from <paramref name="firstMemPageIndex"/>. The run must stay inside one window, which
    /// the write-run builder guarantees by ending every run at the last page of a window.
    /// </summary>
    internal static Memory<byte> PageRunMemory(PageCacheWindow[] windows, int firstMemPageIndex, int pageCount)
    {
        Debug.Assert(pageCount >= 1);
        Debug.Assert(PageCacheAddressing.WindowIndex(firstMemPageIndex) == PageCacheAddressing.WindowIndex(firstMemPageIndex + pageCount - 1),
            "A write run must not straddle a page-cache window.");
        return windows[PageCacheAddressing.WindowIndex(firstMemPageIndex)]
            .Slice(PageCacheAddressing.OffsetInWindow(firstMemPageIndex), pageCount << PagedMMF.PageSizePow2);
    }
}
