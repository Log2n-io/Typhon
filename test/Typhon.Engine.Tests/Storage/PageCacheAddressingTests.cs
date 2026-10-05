using System;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The window arithmetic of <see cref="PageCacheAddressing"/>, as pure functions. Expectations are computed from the ACTIVE window size, so the
/// same tests hold at the default (1 GiB) and under the nightly's <c>TYPHON_PAGECACHE_WINDOW_PAGES_POW2=7</c> (1 MiB).
/// </summary>
[TestFixture]
internal sealed class PageCacheAddressingTests
{
    /// <summary>512 GiB of 8 KiB pages: #945's target.</summary>
    private const int TargetPageCount = 67_108_864;

    private static int PagesPerWindow => PageCacheAddressing.PagesPerWindowMask + 1;
    private static long WindowBytes => (long)PagesPerWindow * PagedMMF.PageSize;

    [Test]
    public void EveryWindowBoundary_RoundTripsToTheFlatOffset()
    {
        // Each boundary ±1 up to 512 GiB, plus the first index whose byte offset does not fit an int. Checked without NUnit asserts in the loop:
        // at 1 MiB windows that is 1.5 M pages.
        var checkedPages = 0;
        string failure = null;
        for (long b = 0; b <= TargetPageCount && failure == null; b += PagesPerWindow)
        {
            for (var d = -1; d <= 1 && failure == null; d++)
            {
                var i = b + d;
                if (i is >= 0 and < TargetPageCount)
                {
                    failure = RoundTripFailure((int)i);
                    checkedPages++;
                }
            }
        }

        Assert.That(failure, Is.Null);
        Assert.That(RoundTripFailure(262_144), Is.Null);
        Assert.That(RoundTripFailure(TargetPageCount - 1), Is.Null);
        Assert.That(checkedPages, Is.GreaterThanOrEqualTo(2 * TargetPageCount / PagesPerWindow), "every boundary was visited");
    }

    private static string RoundTripFailure(int i)
    {
        var window = PageCacheAddressing.WindowIndex(i);
        var offset = PageCacheAddressing.OffsetInWindow(i);
        if (window * WindowBytes + offset != i * (long)PagedMMF.PageSize)
        {
            return $"page {i}: window {window} + offset {offset} is not its flat offset";
        }

        if (offset + PagedMMF.PageSize > WindowBytes || offset >= 1 << 30)
        {
            return $"page {i}: offset {offset} does not fit its window";
        }

        return PageCacheAddressing.IsLastPageOfWindow(i) == ((i + 1) % PagesPerWindow == 0) ? null : $"page {i}: wrong last-of-window answer";
    }

    [TestCase(1)]
    [TestCase(1024)]
    [TestCase(262_143)]
    [TestCase(262_144)]
    [TestCase(TargetPageCount)]
    [TestCase(TargetPageCount + 1)]
    [TestCase(int.MaxValue)]
    public void WindowPageCounts_CoverTheCacheExactly(int pageCount)
    {
        var windows = PageCacheAddressing.WindowCount(pageCount);
        Assert.That(windows, Is.EqualTo((int)(((long)pageCount + PagesPerWindow - 1) / PagesPerWindow)));

        // The last window may be short; every other one is full.
        Assert.That(PageCacheAddressing.WindowPageCount(0, pageCount), Is.EqualTo(Math.Min(PagesPerWindow, pageCount)));
        var last = PageCacheAddressing.WindowPageCount(windows - 1, pageCount);
        Assert.That((long)(windows - 1) * PagesPerWindow + last, Is.EqualTo(pageCount), "full windows plus the last one hold every page");
        Assert.That(last, Is.InRange(1, PagesPerWindow));
    }

    [TestCase(null, ExpectedResult = PageCacheAddressing.DefaultPagesPerWindowPow2)]
    [TestCase("", ExpectedResult = PageCacheAddressing.DefaultPagesPerWindowPow2)]
    [TestCase("0", ExpectedResult = 0)]
    [TestCase("7", ExpectedResult = 7)]
    [TestCase("17", ExpectedResult = 17)]
    public int TheOverride_AcceptsZeroToSeventeen(string raw) => PageCacheAddressing.ParsePagesPerWindowPow2(raw);

    [TestCase("-1")]
    [TestCase("18")]
    [TestCase("abc")]
    [TestCase("7.5")]
    public void TheOverride_RejectsAnythingElse(string raw) =>
        Assert.Throws<InvalidOperationException>(() => PageCacheAddressing.ParsePagesPerWindowPow2(raw));
}
