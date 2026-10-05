using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The page cache's I/O windows, over a made-up base address: a window and the <see cref="System.Memory{T}"/> it hands out are pure pointer
/// arithmetic until someone reads the span, so the mapping can be checked at any cache size without allocating it. Expectations follow the
/// ACTIVE window size, so these hold at the default and under the nightly's 1 MiB windows.
/// </summary>
[TestFixture]
internal sealed unsafe class PageCacheWindowTests
{
    private static readonly byte* FakeBase = (byte*)0x1000_0000_0000;
    private static int PagesPerWindow => PageCacheAddressing.PagesPerWindowMask + 1;

    private static byte* AddressOf(System.Memory<byte> memory)
    {
        using var handle = memory.Pin();
        return (byte*)handle.Pointer;
    }

    [Test]
    public void EachPage_MapsToItsFlatAddress_AcrossWindows()
    {
        var pageCount = 2 * PagesPerWindow + 3;
        var windows = PageCacheWindow.Build(FakeBase, pageCount);
        Assert.That(windows.Length, Is.EqualTo(3));

        foreach (var i in new[] { 0, 1, PagesPerWindow - 1, PagesPerWindow, PagesPerWindow + 1, 2 * PagesPerWindow, pageCount - 1 })
        {
            var page = PageCacheWindow.PageMemory(windows, i);
            Assert.That((nint)AddressOf(page), Is.EqualTo((nint)(FakeBase + i * (long)PagedMMF.PageSize)), $"page {i}");
            Assert.That(page.Length, Is.EqualTo(PagedMMF.PageSize));
        }
    }

    [Test]
    public void TheLastWindow_IsShort()
    {
        var pageCount = 2 * PagesPerWindow + 3;
        var windows = PageCacheWindow.Build(FakeBase, pageCount);

        Assert.That(windows[0].GetSpan().Length, Is.EqualTo(PagesPerWindow * PagedMMF.PageSize));
        Assert.That(windows[2].GetSpan().Length, Is.EqualTo(3 * PagedMMF.PageSize));
    }

    [Test]
    public void ARunUpToTheEndOfAWindow_IsOneSlice()
    {
        var pageCount = 2 * PagesPerWindow;
        var windows = PageCacheWindow.Build(FakeBase, pageCount);

        // The whole second window, then a run ending on its last page.
        var whole = PageCacheWindow.PageRunMemory(windows, PagesPerWindow, PagesPerWindow);
        Assert.That((nint)AddressOf(whole), Is.EqualTo((nint)(FakeBase + PagesPerWindow * (long)PagedMMF.PageSize)));
        Assert.That(whole.Length, Is.EqualTo(PagesPerWindow * PagedMMF.PageSize));

        var tail = PageCacheWindow.PageRunMemory(windows, pageCount - 1, 1);
        Assert.That((nint)AddressOf(tail), Is.EqualTo((nint)(FakeBase + (pageCount - 1) * (long)PagedMMF.PageSize)));
    }
}
