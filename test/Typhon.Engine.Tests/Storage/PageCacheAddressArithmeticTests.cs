using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The page-cache address product, as arithmetic: <c>base + memPageIndex × PageSize</c> must be evaluated in 64 bits.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a latent defect whose trigger is a configuration value, not a code path.</b> Both <c>SavePages</c> sites computed
/// <c>curPageInfo.MemPageIndex * PageSize</c> as an <c>int × int</c> product. Every index the cache-size validator admits keeps that product
/// exact, so the expression is correct today and wrong the moment the ceiling moves — which is why it is fixed BEFORE the ceiling moves rather
/// than alongside it. Nothing in the suite could have caught it, because nothing could ask for a cache large enough to reach the index.
/// </para>
/// <para>
/// <b>What this fixture binds to, and what it does not.</b> It pins the arithmetic, the two boundaries and the headroom between them. It cannot
/// on its own detect someone re-narrowing the expression at the call site; a source-text assertion would be the only way, and it would be worse
/// than the disease. The binding becomes real once the call sites resolve their offsets through a shared helper and this fixture tests that
/// helper. Until then the guard is the documented boundary plus rule PS-12.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class PageCacheAddressArithmeticTests
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
    /// Why the narrow product was never observed: the ceiling stops one page short of where an <c>int</c> product stops being exact.
    /// </summary>
    /// <remarks>
    /// The admitted index range is <c>[0, 262142]</c> and an <c>int</c> product stays exact through <c>262143</c> — so there is exactly ONE page
    /// of headroom, and no configuration reachable today can cross it. That single page is the entire reason this expression survived. If
    /// <see cref="PagedMMF.MaximumCacheSize"/> is ever raised without the widening in place, this is the test that explains the crash.
    /// </remarks>
    [Test]
    [VerifiesRule("PS-12")]
    public void TheCurrentCeilingStopsOnePageShortOfTheIntBoundary()
    {
        var pagesAdmitted = (long)(PagedMMF.MaximumCacheSize / (ulong)PagedMMF.PageSize);
        var lastAdmittedIndex = pagesAdmitted - 1;
        var lastExactIndex = FirstIndexPastIntRange - 1;

        Assert.Multiple(() =>
        {
            Assert.That(PagedMMF.MaximumCacheSize, Is.EqualTo(2147475456UL), "2 GiB minus one page — the largest page multiple an int holds");
            Assert.That(pagesAdmitted, Is.EqualTo(262143L), "the ceiling admits 262 143 pages");
            Assert.That(lastAdmittedIndex, Is.EqualTo(262142L), "so the highest reachable index is 262 142");
            Assert.That(lastExactIndex - lastAdmittedIndex, Is.EqualTo(1L), "exactly one page of headroom — the reason the defect stayed latent");
            Assert.That(lastAdmittedIndex * (long)PagedMMF.PageSize, Is.EqualTo(unchecked((int)lastAdmittedIndex * PagedMMF.PageSize)),
                "at every admitted index the widened and narrow forms still agree, which is why no existing test could fail");
        });
    }
}
