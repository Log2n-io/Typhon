using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// The per-thread traversal-stack pool and the shape of the enumerator it exists to shrink (#916 O1/O3).
/// </summary>
/// <remarks>
/// These are the properties the pool has to hold for <c>SQ-05</c> to survive the move off an inline array: the depth bound is unchanged, and two buffers
/// handed out at once are two different buffers. The integration half — that a nested pair of real queries over promoted cells still answers correctly —
/// lives in <c>CellTreePromotionTests</c>, because it needs an engine.
/// </remarks>
[TestFixture]
class QueryStackPoolTests
{
    /// <summary>
    /// The free list is <c>[ThreadStatic]</c>, so it carries whatever the previous test on this thread left behind — which would make every identity
    /// assertion below pass or fail on test ORDER. Reset makes each one start from empty.
    /// </summary>
    [SetUp]
    public void ResetPool() => QueryStackPool.ResetForTests();

    /// <summary>
    /// <c>SQ-05</c>'s depth bound is a property of the pool now, and it is the same 256 the inline array gave.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed because the bound is enforced in <c>PushChild</c> against this constant: raising one without the other either drops
    /// children a bigger buffer could have held, or writes past the array. The second is a bounds-check throw under an OLC read latch, which is exactly the
    /// place the code goes out of its way never to throw.
    /// </remarks>
    [Test]
    [VerifiesRule("SQ-05")]
    public void CapacityIsTheDepthBoundTheInlineArrayGave()
    {
        Assert.That(QueryStackPool.Capacity, Is.EqualTo(256));

        // Capacity + 1: the tail int is the ownership token, not a DFS slot. PushChild bounds itself by Capacity, so the extra
        // element is unreachable from the traversal.
        Assert.That(QueryStackPool.Rent(out _), Has.Length.EqualTo(257),
            "a rented stack must hold the 256-slot bound PushChild tests against, plus its token");
    }

    /// <summary>
    /// Two rents outstanding at once are two distinct arrays — the property nested queries rest on.
    /// </summary>
    /// <remarks>
    /// This is the test that fails if the pool is ever simplified to a single <c>[ThreadStatic]</c> buffer. A shared buffer does not crash: the inner query
    /// overwrites the outer's traversal state, and the outer then resumes from a stack describing a different subtree — so it returns a subset, silently.
    /// That is an <c>SQ-01</c> false negative reached through <c>SQ-05</c>.
    /// </remarks>
    [Test]
    [VerifiesRule("SQ-05")]
    public void TwoLiveRentsAreDistinctBuffers()
    {
        var outer = QueryStackPool.Rent(out _);
        var inner = QueryStackPool.Rent(out var innerToken);

        Assert.That(inner, Is.Not.SameAs(outer), "two enumerators live at once must not share one traversal stack");

        // And the third, after one is handed back, may legitimately reuse it — that is the pooling working, not a fault.
        QueryStackPool.Return(inner, innerToken);
        Assert.That(QueryStackPool.Rent(out _), Is.SameAs(inner), "a returned buffer should be reissued rather than reallocated");
    }

    /// <summary>
    /// Returning one buffer twice does not put it in the free list twice.
    /// </summary>
    /// <remarks>
    /// The simple half of the guard: the buffer is still parked, so the token has already been zeroed and the second return fails on that. See
    /// <see cref="StaleReturnAfterReRentIsIgnored"/> for the half an identity scan would miss.
    /// </remarks>
    [Test]
    [VerifiesRule("SQ-05")]
    public void DoubleReturnDoesNotAliasTheNextTwoRents()
    {
        var buffer = QueryStackPool.Rent(out var token);
        QueryStackPool.Return(buffer, token);
        QueryStackPool.Return(buffer, token);

        var first = QueryStackPool.Rent(out _);
        var second = QueryStackPool.Rent(out _);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.SameAs(buffer), "the first rent should still get the pooled buffer back");
            Assert.That(second, Is.Not.SameAs(buffer), "a double return must not hand the same array to two live enumerators");
        });
    }

    /// <summary>
    /// A stale return that arrives AFTER the buffer has been re-rented is ignored, so it cannot park a live buffer.
    /// </summary>
    /// <remarks>
    /// <b>This is the case an identity scan cannot catch, and the reason the token exists.</b> A copy of an enumerator that had already rented — the shape
    /// <c>GetEnumerator() =&gt; this</c> creates — returns the buffer; another query rents it; then the original disposes and returns it again. At that
    /// moment the buffer is legitimately out on loan, so scanning the free list finds nothing and would park a stack a live enumerator is still writing to.
    /// The next rent then hands one array to two enumerators, and each silently truncates the other's results.
    /// </remarks>
    [Test]
    [VerifiesRule("SQ-05")]
    public void StaleReturnAfterReRentIsIgnored()
    {
        var buffer = QueryStackPool.Rent(out var firstToken);
        QueryStackPool.Return(buffer, firstToken);

        // Somebody else now legitimately owns it.
        var reRented = QueryStackPool.Rent(out _);
        Assert.That(reRented, Is.SameAs(buffer), "precondition: the pool reissued the same array, which is what makes the stale return dangerous");

        // The original copy, unaware, hands it back a second time with its now-defunct token.
        QueryStackPool.Return(buffer, firstToken);

        Assert.That(QueryStackPool.Rent(out _), Is.Not.SameAs(buffer),
            "a stale return parked a buffer that was still live — the next rent now aliases another enumerator's traversal stack");
    }

    /// <summary>A token the pool never issued, or one for a different buffer, is refused.</summary>
    [Test]
    public void ForeignTokensAreRefused()
    {
        var buffer = QueryStackPool.Rent(out var token);
        QueryStackPool.Return(buffer, token + 12345);

        Assert.That(QueryStackPool.Rent(out _), Is.Not.SameAs(buffer), "a buffer must not be pooled on the strength of a token that does not own it");
    }

    /// <summary>A buffer the pool did not issue, or one of the wrong length, is refused rather than pooled.</summary>
    [Test]
    public void ForeignBuffersAreRefused()
    {
        var wrongLength = new int[QueryStackPool.Capacity / 2];
        QueryStackPool.Return(null, 1);
        QueryStackPool.Return(wrongLength, 1);

        Assert.That(QueryStackPool.Rent(out _), Is.Not.SameAs(wrongLength), "a short buffer must never reach PushChild, which trusts Capacity");
    }

    /// <summary>
    /// The pool retains a bounded number of buffers; beyond that it still works, it just stops pooling.
    /// </summary>
    /// <remarks>
    /// Nesting deeper than the retained count is legal — the point of the bound is that a pathological nest cannot make one thread hold an unbounded number
    /// of 1 KB arrays for the rest of the process.
    /// </remarks>
    [Test]
    public void RentsBeyondTheRetainedCountStillSucceed()
    {
        const int Depth = 32;

        var live = new List<int[]>();
        var tokens = new List<int>();
        for (var i = 0; i < Depth; i++)
        {
            live.Add(QueryStackPool.Rent(out var token));
            tokens.Add(token);
        }

        // Reference identity, explicitly. NUnit's Is.Unique compares collections STRUCTURALLY, and every freshly rented stack is 256 zeroes — so the
        // structural comparison calls them all equal and the test fails on buffers that are in fact distinct objects.
        var distinct = new HashSet<int[]>(ReferenceEqualityComparer.Instance);
        foreach (var buffer in live)
        {
            distinct.Add(buffer);
        }

        Assert.That(distinct, Has.Count.EqualTo(Depth), "every concurrently-live rent must be a distinct buffer, however deep the nesting goes");

        for (var i = 0; i < Depth; i++)
        {
            QueryStackPool.Return(live[i], tokens[i]);
        }
    }

    /// <summary>
    /// <c>AC-3</c>: the enumerator is still a <c>ref struct</c>.
    /// </summary>
    /// <remarks>
    /// The zero-allocation guarantee and the stack-only escape safety both come from this, and the rejected alternative in #916 — heap-allocating the tree
    /// enumerator — is exactly the change that would forfeit it. Cheap to assert, and the assertion is the thing the issue actually promised.
    /// </remarks>
    [Test]
    public void EnumeratorIsStillARefStruct()
    {
        Assert.That(typeof(AabbClusterEnumerator).IsByRefLike, Is.True, "AabbClusterEnumerator must stay a ref struct — AC-3");
    }

    /// <summary>
    /// <c>AC-6</c>: the enumerator's size is recorded as a number, not as "smaller".
    /// </summary>
    /// <remarks>
    /// <para><b>Measured, both sides.</b> <c>2 624</c> bytes at the commit before #916 and <c>1 544</c> after, taken with the same
    /// <see cref="Unsafe.SizeOf{T}"/> call on this machine — not derived from adding up field widths, because the padding between them is the JIT's business
    /// and guessing it is how a "recorded number" becomes a wrong one. Of the 1 080 removed, 1 016 is the tree's inline DFS stack (O1) and 72 the duplicated
    /// <c>SpatialNodeDescriptor</c> (O3).</para>
    /// <para><b>What is left, and why it is not this issue's subject.</b> Two <c>ChunkAccessor</c> copies are ~896 of the remainder — 448 each, one for the
    /// cluster segment and one inside the tree enumerator. #916 does not name them and they are not touched here; they are the obvious next question, and
    /// the ceiling is set so that removing them later still passes.</para>
    /// <para><see cref="Unsafe.SizeOf{T}"/> accepts a <c>ref struct</c> on .NET 10 (the <c>allows ref struct</c> anti-constraint), which is what makes this a
    /// real assertion rather than a comment.</para>
    /// </remarks>
    [Test]
    public void EnumeratorSizeIsRecorded()
    {
        const int SizeBefore916 = 2_624;
        const int Ceiling = 1_700;

        var size = Unsafe.SizeOf<AabbClusterEnumerator>();
        TestContext.Out.WriteLine($"AabbClusterEnumerator = {size} bytes (was {SizeBefore916} before #916)");

        Assert.That(size, Is.LessThanOrEqualTo(Ceiling),
            $"AabbClusterEnumerator grew to {size} bytes, from the {SizeBefore916} #916 started with. It is returned by value twice and copied into the "
            + "foreach local, so its size is paid on every spatial query — putting a kilobyte back must be a deliberate decision, not a quiet one.");
    }
}
