using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Conservation of <c>DirtyCounter</c> marks between <see cref="ChangeSet"/> (per-UoW) and the checkpoint
/// (background, one <c>DecrementDirty</c> per page per cycle).
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract under test</b>, stated by <c>ChangeSet.ReleaseExcessDirtyMarks</c> itself: for a page with
/// tracked mark count <c>N</c> it issues exactly <c>N-1</c> decrements, so "the page contributes exactly one
/// outstanding mark from this UoW. The next checkpoint cycle's single <c>DecrementDirty</c> brings DC back to its
/// pre-UoW baseline."
/// </para>
/// <para>
/// That balances for ONE UoW per page per checkpoint cycle. It is silent about K of them. These tests pin both
/// arms, because the difference between them is a page that is permanently unevictable: DC never returns to zero,
/// the clock-sweep can never reclaim the page, and the cache eventually starves
/// (<c>PageCacheBackpressureTimeout</c>) after tens of minutes of ordinary running.
/// </para>
/// <para>
/// Deliberately built from <see cref="ChangeSet"/> and <c>PagedMMF</c> directly rather than by driving the demo or
/// the tick fence: the question is an arithmetic property of the accounting, and a test that has to run a
/// simulation for minutes to answer it is a detector, not a proof. Everything here is deterministic and has no
/// timing component.
/// </para>
/// </remarks>
/// <remarks>
/// <b>QUARANTINED, deliberately red, tracked by #824.</b> These arms assert the CORRECT accounting, which the
/// engine does not yet implement — the checkpoint acks one mark per page per cycle while K units of work legitimately
/// retain one each, so DirtyCounter inflates by K-1 every cycle and the page can never be evicted. They are landed
/// red on purpose: the alternative is that whoever takes #824 starts with a demo that needs thirty minutes to tell
/// them they are wrong, instead of a 119 ms oracle that tells them immediately.
/// <para>
/// Do NOT make these pass by acking the observed DirtyCounter at capture. That turns all four green and breaks
/// FlipByteInPage_IsDetected, Truncation_IsDetected and CleanShutdownReopenDoesNotRederive — it consumes marks still
/// owned by in-flight units of work, which is #385's lost write. Both gates have to hold at once; see #824.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Quarantine")]
internal sealed class ChangeSetDirtyMarkConservationTests : TestBase<ChangeSetDirtyMarkConservationTests>
{
    /// <summary>
    /// Drives ONE real checkpoint cycle and waits for it. Deliberately the real cycle rather than a hand-simulated
    /// "decrement once" — the ack arithmetic IS the thing under test, so a test that models it can only ever agree
    /// with whatever the model says.
    /// </summary>
    private static void RunCheckpoint(DatabaseEngine dbe)
    {
        var cm = dbe.CheckpointManager;
        Assert.That(cm, Is.Not.Null, "these tests require the checkpoint manager");
        cm.ForceCheckpoint();
        Assert.That(cm.WaitForCheckpoint(TimeSpan.FromSeconds(5)), Is.True, "checkpoint cycle did not complete");
    }

    /// <summary>
    /// A real, resident data page. Synthetic indices index the live page-state array out of range and tear the host
    /// down instead of testing anything — the neighbouring guard fixture learned that the hard way.
    /// </summary>
    private static int ResidentDataPage(DatabaseEngine dbe)
    {
        var (_, _, firstDirty) = dbe.MMF.CountDirtyPages();
        Assert.That(firstDirty, Is.GreaterThanOrEqualTo(0), "engine init should leave at least one resident dirty page to work with");
        return firstDirty;
    }

    /// <summary>
    /// One UoW: the documented contract holds exactly. Mark, release, one checkpoint ack — back to baseline.
    /// </summary>
    /// <remarks>
    /// This arm exists so the K&gt;1 failure below cannot be dismissed as the harness mismodelling the protocol.
    /// If this one is red, the test is wrong, not the engine.
    /// </remarks>
    [Test]
    public void SingleUow_ReturnsDirtyCounterToBaseline()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var page = ResidentDataPage(dbe);
        var baseline = dbe.MMF.DirtyCounterOf(page);

        var cs = new ChangeSet(dbe.MMF);
        cs.AddByMemPageIndex(page);
        cs.RegisterReDirty(page);           // a re-dirty within the same UoW — the case N-1 exists to drain
        cs.ReleaseExcessDirtyMarks();

        Assert.That(dbe.MMF.DirtyCounterOf(page), Is.EqualTo(baseline + 1),
            "after release a UoW must leave exactly one outstanding mark, whatever it did internally");

        RunCheckpoint(dbe);

        Assert.That(dbe.MMF.DirtyCounterOf(page), Is.Zero,
            "one checkpoint cycle must return DC to zero, leaving the page evictable");
    }

    /// <summary>
    /// K UoWs touching the SAME page inside ONE checkpoint cycle. Each leaves one mark; the cycle acks once.
    /// DC must still return to baseline — a checkpoint interval is wall-clock (30 s by default) and says nothing
    /// about how many transactions touched a page inside it.
    /// </summary>
    /// <remarks>
    /// Occupancy/bitmap pages make K large by construction: every transaction that grows a segment marks them, so
    /// they are the first to become permanently unevictable. Measured in the SpaceBattle demo, a page reached 26
    /// outstanding marks, and the dirty-page count climbed monotonically to cache exhaustion in ~30 minutes.
    /// Raising checkpoint frequency reduced the residue 22x (868 dirty pages at 1 cycle, 39 at 1001) — the
    /// signature of a per-cycle imbalance rather than an absolute leak.
    /// </remarks>
    [Test]
    public void MultipleUowsInOneCheckpointCycle_ReturnDirtyCounterToBaseline([Values(2, 8, 26)] int k)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var page = ResidentDataPage(dbe);
        var baseline = dbe.MMF.DirtyCounterOf(page);

        for (var i = 0; i < k; i++)
        {
            var cs = new ChangeSet(dbe.MMF);
            cs.AddByMemPageIndex(page);
            cs.ReleaseExcessDirtyMarks();
        }

        Assert.That(dbe.MMF.DirtyCounterOf(page), Is.EqualTo(baseline + k),
            "each unit of work legitimately retains one mark until a checkpoint captures its changes");

        RunCheckpoint(dbe);                 // ONE cycle — it captured all K, so it must ack all K

        Assert.That(dbe.MMF.DirtyCounterOf(page), Is.Zero,
            $"{k} UoWs touched one page inside a single checkpoint cycle; each left one mark and the cycle acked "
            + "once, so DC is left permanently inflated. The page can never be evicted (PS-01), and the page cache "
            + "starves once enough pages reach this state.");
    }
}
