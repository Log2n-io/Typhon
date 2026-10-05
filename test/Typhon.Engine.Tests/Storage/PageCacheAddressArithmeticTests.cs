using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The page-cache address product, as arithmetic: <c>base + memPageIndex × PageSize</c> must be evaluated in 64 bits.
/// </summary>
/// <remarks>
/// <para>
/// <b>A defect whose trigger was a configuration value, not a code path.</b> Both <c>SavePages</c> sites computed <c>MemPageIndex * PageSize</c>
/// as an <c>int × int</c> product. Every index the old 2 GiB ceiling admitted kept that product exact, so the expression was correct until the
/// ceiling moved — which is why it was fixed (#945 S1) before the ceiling went (#945 S7). Nothing could have caught it, because nothing could ask
/// for a cache large enough to reach the index.
/// </para>
/// <para>
/// What runs past 2 GiB for real is <see cref="LargePageCacheTests"/> (nightly): a 3 GiB cache whose pages past index 262 143 are written,
/// checkpointed and read back.
/// </para>
/// </remarks>
[TestFixture]
internal sealed unsafe class PageCacheAddressArithmeticTests
{
    /// <summary>The first mem-page index whose byte offset does not fit a signed <c>int</c>: 2 GiB ÷ 8 KiB.</summary>
    private const int FirstIndexPastIntRange = 262144;

    [Test]
    [VerifiesRule("PS-12")]
    public void AMemPageOffsetPastTwoGibIsComputedIn64Bits()
    {
        // The widened form, as the production expression now writes it.
        var widened = FirstIndexPastIntRange * (long)PagedMMF.PageSize;

        // The narrow form it replaced. Wrapping is the defect: `byte* + int` sign-extends, so the pointer lands 2 GiB BELOW the cache base and
        // both SavePages sites then read-modify-write a ChangeRevision there and CRC-stamp 8 KiB over it.
        var narrow = unchecked(FirstIndexPastIntRange * PagedMMF.PageSize);

        Assert.Multiple(() =>
        {
            Assert.That(widened, Is.EqualTo(2147483648L), "the first index past int range sits at exactly 2 GiB");
            Assert.That(narrow, Is.EqualTo(int.MinValue), "the narrow product wraps to the most negative int — a write 2 GiB below the cache base");
            Assert.That((long)narrow, Is.Not.EqualTo(widened), "precondition: the two forms genuinely disagree at this index");
        });
    }

    /// <summary>
    /// The disk I/O path's addressing, at the first page past 2 GiB: the window that holds it hands out exactly its 64-bit offset from the cache
    /// base. A made-up base, since a window is pointer arithmetic until its span is read.
    /// </summary>
    [Test]
    [VerifiesRule("PS-12")]
    public void TheFirstPagePastTwoGib_IsAddressedAtItsSixtyFourBitOffset_ByTheIOWindows()
    {
        var cacheBase = (byte*)0x1000_0000_0000;
        var windows = PageCacheWindow.Build(cacheBase, FirstIndexPastIntRange + 2);

        using var handle = PageCacheWindow.PageMemory(windows, FirstIndexPastIntRange).Pin();

        Assert.That((long)handle.Pointer - (long)cacheBase, Is.EqualTo(2147483648L));
    }
}
