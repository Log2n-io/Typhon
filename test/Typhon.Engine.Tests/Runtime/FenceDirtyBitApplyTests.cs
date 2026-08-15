using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Unit coverage for <see cref="ArchetypeClusterState.ApplyDirtyBitDeltas"/> — the method that folds a Migrate chunk's buffered
/// <see cref="DirtyBitDelta"/> entries into an archetype's <c>FenceDirtyBits</c>, and the verifier for rule MD-02.
/// </summary>
/// <remarks>
/// <para>
/// It had no direct coverage at all before this fixture, which is how MD-02 came to specify a design — per-flip <c>Interlocked</c> from the workers —
/// that the implementation never shipped. Nothing mechanically compared the rule to the code, so the two drifted from the same PR that introduced both.
/// These tests pin the behaviour that actually exists: destination bits set, source bits cleared, neighbouring slots in the same word untouched, the
/// on-demand grow when a destination chunkId exceeds the pre-sized array, and runs composing rather than overwriting.
/// </para>
/// <para>
/// They drive the method directly on a bare <see cref="ArchetypeClusterState"/> — deterministic and sub-millisecond, with the lock uncontended.
/// The concurrent path is covered by <c>ParallelFenceTests</c>.
/// </para>
/// </remarks>
[TestFixture]
public sealed class FenceDirtyBitApplyTests
{
    private static ArchetypeClusterState StateWithBits(int length)
    {
        var cs = ArchetypeClusterState.CreateActiveListOnlyForTests();
        cs.FenceDirtyBits = new long[length];
        return cs;
    }

    private static DirtyBitDelta Delta(int srcChunkId, int srcSlot, int dstChunkId, int dstSlot) => new()
    {
        ArchetypeId = 0,
        SrcChunkId = srcChunkId,
        SrcClearMask = srcSlot < 0 ? 0L : 1L << srcSlot,
        DstChunkId = dstChunkId,
        DstSetMask = dstSlot < 0 ? 0L : 1L << dstSlot,
    };

    /// <summary>Seeds one entity live in cluster 2 slot 5, migrating to cluster 4 slot 9.</summary>
    private static ArchetypeClusterState SeededMigration()
    {
        var cs = StateWithBits(8);
        cs.FenceDirtyBits[2] = 1L << 5;
        return cs;
    }

    /// <summary>
    /// MD-02's post-condition for <see cref="SeededMigration"/>, factored out so the mutant below can drive this exact assertion pair with an input that
    /// violates the rule. Keep the message strings stable — the mutant matches on them as its positive evidence.
    /// </summary>
    private static void AssertMigrationBitsMoved(ArchetypeClusterState cs)
    {
        Assert.That(cs.FenceDirtyBits[2], Is.Zero, "source slot bit must be cleared");
        Assert.That(cs.FenceDirtyBits[4], Is.EqualTo(1L << 9), "destination slot bit must be set");
    }

    [Test]
    [VerifiesRule("MD-02")]
    public void ApplyDeltas_SetsDestinationBitAndClearsSourceBit()
    {
        var cs = SeededMigration();

        cs.ApplyDirtyBitDeltas(new List<DirtyBitDelta> { Delta(srcChunkId: 2, srcSlot: 5, dstChunkId: 4, dstSlot: 9) }, 0, 1);

        AssertMigrationBitsMoved(cs);
    }

    /// <summary>
    /// Genuineness proof for the verifier above: the violating input is a migration whose buffered delta is never applied — what a dropped buffer, a
    /// skipped flush or a lost bucket produces. MD-02 exists to exclude that case, so its verifier has to be able to see it.
    /// </summary>
    [Test]
    [RuleMutant("MD-02")]
    public void ApplyDeltas_Mutant_UnappliedDeltaIsDetected()
    {
        // The marker is the SOURCE assertion, not the destination one: with nothing applied, the un-cleared source bit is what the verifier trips on
        // first. Matching on the destination message would make the mutant pass for the wrong reason — which is the failure AssertDetects is built to
        // reject, and did reject when this was first written.
        RuleMutants.AssertDetects(
            "MD-02",
            "source slot bit must be cleared",
            () => AssertMigrationBitsMoved(SeededMigration())); // deltas buffered but never applied
    }

    /// <summary>Neighbouring bits in the same word belong to other entities — a clear must not take them with it.</summary>
    [Test]
    public void ApplyDeltas_LeavesOtherSlotsInTheSameWordUntouched()
    {
        var cs = StateWithBits(8);
        cs.FenceDirtyBits[1] = (1L << 3) | (1L << 7) | (1L << 40);

        cs.ApplyDirtyBitDeltas(new List<DirtyBitDelta> { Delta(1, 7, 1, 8) }, 0, 1);

        Assert.That(cs.FenceDirtyBits[1], Is.EqualTo((1L << 3) | (1L << 8) | (1L << 40)));
    }

    /// <summary>
    /// Prep pre-sizes generously so this normally never fires, but that bound is an empirical over-estimate rather than a proof —
    /// <c>PrepareArchetypeFence</c> says as much, having been raised after the strict bound was observed to under-shoot. The fallback grow is therefore
    /// load-bearing under exactly the conditions nobody has characterised, which is the case worth pinning.
    /// </summary>
    [Test]
    public void ApplyDeltas_GrowsWhenDestinationChunkIdExceedsTheArray()
    {
        var cs = StateWithBits(4);

        cs.ApplyDirtyBitDeltas(new List<DirtyBitDelta> { Delta(0, 1, dstChunkId: 37, dstSlot: 2) }, 0, 1);

        Assert.That(cs.FenceDirtyBits.Length, Is.GreaterThan(37), "array must grow to cover the destination chunk id");
        Assert.That(cs.FenceDirtyBits[37], Is.EqualTo(1L << 2), "destination bit must land in the grown array");
    }

    [Test]
    public void ApplyDeltas_AllocatesWhenTheArrayIsNull()
    {
        var cs = ArchetypeClusterState.CreateActiveListOnlyForTests();
        Assert.That(cs.FenceDirtyBits, Is.Null, "precondition: no array yet");

        cs.ApplyDirtyBitDeltas(new List<DirtyBitDelta> { Delta(-1, -1, dstChunkId: 3, dstSlot: 11) }, 0, 1);

        Assert.That(cs.FenceDirtyBits, Is.Not.Null);
        Assert.That(cs.FenceDirtyBits[3], Is.EqualTo(1L << 11));
    }

    /// <summary>
    /// One archetype's bits are built from many chunks' buffers, each applied in its own call under the latch. Successive runs must therefore compose
    /// rather than overwrite — an implementation that assigned instead of OR-ing would pass a single-run test and lose every chunk but the last.
    /// </summary>
    [Test]
    public void ApplyDeltas_SuccessiveRunsAccumulate()
    {
        var cs = StateWithBits(8);

        cs.ApplyDirtyBitDeltas(new List<DirtyBitDelta> { Delta(-1, -1, 5, 1) }, 0, 1);
        cs.ApplyDirtyBitDeltas(new List<DirtyBitDelta> { Delta(-1, -1, 5, 2) }, 0, 1);
        cs.ApplyDirtyBitDeltas(new List<DirtyBitDelta> { Delta(-1, -1, 5, 3) }, 0, 1);

        Assert.That(cs.FenceDirtyBits[5], Is.EqualTo((1L << 1) | (1L << 2) | (1L << 3)));
    }

    /// <summary>Only the caller's own run may be applied — the buffer is shared, grouped by archetypeId, and each archetype gets one contiguous slice.</summary>
    [Test]
    public void ApplyDeltas_AppliesOnlyTheRequestedRange()
    {
        var cs = StateWithBits(8);
        var buffer = new List<DirtyBitDelta>
        {
            Delta(-1, -1, 1, 1),   // before the run — must not be applied
            Delta(-1, -1, 2, 2),   // in the run
            Delta(-1, -1, 3, 3),   // after the run — must not be applied
        };

        cs.ApplyDirtyBitDeltas(buffer, offset: 1, count: 1);

        Assert.That(cs.FenceDirtyBits[1], Is.Zero);
        Assert.That(cs.FenceDirtyBits[2], Is.EqualTo(1L << 2));
        Assert.That(cs.FenceDirtyBits[3], Is.Zero);
    }

    [Test]
    public void ApplyDeltas_ZeroOrNegativeCount_IsNoOpAndDoesNotAllocate()
    {
        var cs = ArchetypeClusterState.CreateActiveListOnlyForTests();

        cs.ApplyDirtyBitDeltas(new List<DirtyBitDelta> { Delta(-1, -1, 3, 1) }, 0, 0);
        cs.ApplyDirtyBitDeltas(new List<DirtyBitDelta> { Delta(-1, -1, 3, 1) }, 0, -1);

        Assert.That(cs.FenceDirtyBits, Is.Null, "a no-op run must not allocate the array");
    }

    /// <summary>
    /// A source chunk id past the end of the array is ignored rather than growing it: growth is driven by destinations, and a source beyond the array
    /// has no bit to clear by construction. Pinned because the bounds check is what stops the clear pass from throwing after a grow sized on a
    /// different maximum.
    /// </summary>
    [Test]
    public void ApplyDeltas_OutOfRangeSource_IsIgnored()
    {
        var cs = StateWithBits(4);

        Assert.DoesNotThrow(() => cs.ApplyDirtyBitDeltas(new List<DirtyBitDelta> { Delta(999, 1, 2, 6) }, 0, 1));
        Assert.That(cs.FenceDirtyBits[2], Is.EqualTo(1L << 6));
    }
}
