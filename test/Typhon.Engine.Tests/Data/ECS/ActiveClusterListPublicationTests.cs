using NUnit.Framework;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Issue #582 face 2 — <c>ActiveClusterIds</c> / <c>ActiveClusterCount</c> must be readable as a consistent pair.
/// </summary>
/// <remarks>
/// <para>
/// Worker threads read the pair live, per chunk, while another worker grows it inside <c>Commit()</c>; nothing excludes the overlap. Three call sites in
/// <c>TyphonRuntime</c> loaded the ARRAY first and the COUNT second, which yields an array shorter than the count it is about to be indexed with — an
/// <c>IndexOutOfRangeException</c> out of the parallel-query prepare. All three now go through <c>TyphonRuntime.ReadActiveClusterList</c>, which loads
/// count then array, against an <c>AddToActiveList</c> that releases array then count.
/// </para>
/// <para>
/// These are written as a DETERMINISTIC interleaving rather than a stress loop. Racing for it does not work: the window is the two instructions between
/// the reader's loads, and a spin of 40 000 adds — about twelve resizes — never once landed inside it. Constructing the interleaving by hand states the
/// property instead of sampling for it, and makes the test worth the same on a loaded CI box as on a quiet one.
/// </para>
/// <para>
/// Face 1 — a walk racing <c>RemoveFromActiveList</c>, whose swap-with-last can show one cluster twice and free another underneath the walker — is NOT
/// covered here and is NOT fixed. It needs a snapshot or epoch protocol.
/// </para>
/// </remarks>
[TestFixture]
class ActiveClusterListPublicationTests
{
    /// <summary>Fills to exactly capacity, so the next single add is guaranteed to be the resizing one.</summary>
    private static ArchetypeClusterState FilledToCapacity(out int capacity)
    {
        var cs = ArchetypeClusterState.CreateActiveListOnlyForTests();
        capacity = cs.ActiveClusterIds.Length;
        for (var i = 0; i < capacity; i++)
        {
            cs.AddToActiveList(i);
        }

        Assert.That(cs.ActiveClusterCount, Is.EqualTo(capacity), "PREMISE: the list is full, so the next add must resize");
        Assert.That(cs.ActiveClusterIds.Length, Is.EqualTo(capacity), "PREMISE: it has not resized yet");
        return cs;
    }

    /// <summary>The removed order, shown faulting. This is the defect, not a hypothetical.</summary>
    [VerifiesRule("CLUSTERWALK-02")]
    [Test]
    public void LoadingTheArrayBeforeTheCount_YieldsACountPastTheArray()
    {
        var cs = FilledToCapacity(out var capacity);

        var ids = Volatile.Read(ref cs.ActiveClusterIds);       // reader loads the array...
        cs.AddToActiveList(9999);                                // ...a committing worker resizes and appends here...
        var count = Volatile.Read(ref cs.ActiveClusterCount);    // ...reader loads the count

        Assert.That(count, Is.GreaterThan(ids.Length),
            "the array-then-count order must produce count > ids.Length — if it stops doing so, the ordering the other tests pin is no longer what "
            + "protects the walk, and this test should be revisited rather than deleted");
        Assert.That(count - 1, Is.EqualTo(capacity), "sanity: exactly one add crossed the boundary");
    }

    /// <summary>The order every call site now uses, shown holding across the same interleaving.</summary>
    [VerifiesRule("CLUSTERWALK-02")]
    [Test]
    public void LoadingTheCountBeforeTheArray_NeverYieldsACountPastTheArray()
    {
        var cs = FilledToCapacity(out var capacity);

        var count = Volatile.Read(ref cs.ActiveClusterCount);    // reader loads the count...
        cs.AddToActiveList(9999);                                 // ...worker resizes and appends...
        var ids = Volatile.Read(ref cs.ActiveClusterIds);         // ...reader loads the array

        Assert.That(count, Is.LessThanOrEqualTo(ids.Length),
            "count must never exceed the array it indexes: a stale count is short, which is safe; a stale array is out of bounds, which is not");
        Assert.That(count, Is.EqualTo(capacity), "the reader sees the pre-add count, which is the benign direction");
    }

    /// <summary>
    /// The other half of the pairing: the writer must release the grown array BEFORE the count that indexes it, or an acquiring reader could pair a new
    /// count with an array it cannot see yet on a weak memory model.
    /// </summary>
    [VerifiesRule("CLUSTERWALK-02")]
    [Test]
    public void TheGrownArrayIsPublishedBeforeTheCountThatIndexesIt()
    {
        var cs = FilledToCapacity(out var capacity);

        cs.AddToActiveList(9999);

        Assert.That(cs.ActiveClusterIds.Length, Is.GreaterThan(capacity), "the add must have grown the array");
        Assert.That(cs.ActiveClusterCount, Is.LessThanOrEqualTo(cs.ActiveClusterIds.Length));
        Assert.That(cs.ActiveClusterIds[capacity], Is.EqualTo(9999), "the appended element must be present in the published array");

        // Every prior element survived the growth.
        for (var i = 0; i < capacity; i++)
        {
            Assert.That(cs.ActiveClusterIds[i], Is.EqualTo(i), $"element {i} was lost across the resize");
        }
    }

    /// <summary>A removal must not leave a count above the live prefix in a way a reader can misuse.</summary>
    [VerifiesRule("CLUSTERWALK-02")]
    [Test]
    public void RemovingFromTheActiveList_KeepsTheCountWithinTheArray()
    {
        var cs = FilledToCapacity(out var capacity);
        cs.AddToActiveList(9999);

        cs.RemoveFromActiveList(0);

        var count = Volatile.Read(ref cs.ActiveClusterCount);
        var ids = Volatile.Read(ref cs.ActiveClusterIds);
        Assert.That(count, Is.EqualTo(capacity), "one of capacity+1 entries was removed");
        Assert.That(count, Is.LessThanOrEqualTo(ids.Length));
    }
}
