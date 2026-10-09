using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Splits the page cache into fixed windows for the two async I/O paths, which need a <see cref="Memory{T}"/> and so cannot address past 2 GiB
/// from one base: <see cref="Memory{T}"/> and <c>MemoryManager&lt;T&gt;.CreateMemory</c> take <c>int</c> offsets. One window per 2^17 pages
/// (1 GiB), each with its own manager (<see cref="PageCacheWindow"/>).
/// </summary>
/// <remarks>
/// All arithmetic is in page-index space and stays <c>int</c>: an offset inside a window is below 2^30 bytes, and a write run never leaves its
/// window, so no 64-bit product exists on this path. The window size is a process-wide constant read once, before any engine exists, so the JIT
/// treats it like a constant and two engines in one process can never disagree. <c>TYPHON_PAGECACHE_WINDOW_PAGES_POW2</c> overrides it, for
/// tests only: an 8 MiB test cache is one window at the default, and at 7 (1 MiB windows) every miss, structural write and grow in the suite
/// crosses windows.
/// </remarks>
internal static class PageCacheAddressing
{
    /// <summary>The environment variable that overrides <see cref="PagesPerWindowPow2"/> (tests only).</summary>
    internal const string WindowPagesPow2Variable = "TYPHON_PAGECACHE_WINDOW_PAGES_POW2";

    /// <summary>The default: 2^17 pages × 8 KiB = 1 GiB per window.</summary>
    internal const int DefaultPagesPerWindowPow2 = 17;

    /// <summary>log2 of the pages per window, 0 to 17.</summary>
    internal static readonly int PagesPerWindowPow2 = ParsePagesPerWindowPow2(Environment.GetEnvironmentVariable(WindowPagesPow2Variable));

    /// <summary>Pages per window minus one: the in-window part of a page index.</summary>
    internal static readonly int PagesPerWindowMask = (1 << PagesPerWindowPow2) - 1;

    /// <summary>The window holding <paramref name="memPageIndex"/>.</summary>
    internal static int WindowIndex(int memPageIndex) => memPageIndex >> PagesPerWindowPow2;

    /// <summary>The byte offset of <paramref name="memPageIndex"/> inside its window, below 2^30.</summary>
    internal static int OffsetInWindow(int memPageIndex) => (memPageIndex & PagesPerWindowMask) << PagedMMF.PageSizePow2;

    /// <summary>Whether <paramref name="memPageIndex"/> is the last page of its window: a write run must end there.</summary>
    internal static bool IsLastPageOfWindow(int memPageIndex) => (memPageIndex & PagesPerWindowMask) == PagesPerWindowMask;

    /// <summary>The number of windows a cache of <paramref name="memPagesCount"/> pages needs.</summary>
    internal static int WindowCount(int memPagesCount) => (int)(((long)memPagesCount + PagesPerWindowMask) >> PagesPerWindowPow2);

    /// <summary>The pages in window <paramref name="window"/>: a full window, except possibly the last.</summary>
    internal static int WindowPageCount(int window, int memPagesCount) => Math.Min(PagesPerWindowMask + 1, memPagesCount - (window << PagesPerWindowPow2));

    /// <summary>
    /// Reads the override: absent or empty means the default; anything else must be an integer from 0 to 17, or this throws (at type
    /// initialisation, so a mistyped value fails the process at once instead of running it at some other window size).
    /// </summary>
    internal static int ParsePagesPerWindowPow2(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return DefaultPagesPerWindowPow2;
        }

        if (!int.TryParse(raw, out var value) || value < 0 || value > DefaultPagesPerWindowPow2)
        {
            throw new InvalidOperationException($"{WindowPagesPow2Variable} must be an integer from 0 to {DefaultPagesPerWindowPow2}, got '{raw}'.");
        }

        return value;
    }
}
