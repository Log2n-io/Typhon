using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The two page-cache obligations, tested apart: the mutator's <c>DirtyCounter</c> marks, which must balance exactly,
/// and the page's writeback debt, which must survive until a write actually captures it.
/// </summary>
/// <remarks>
/// <para>
/// These were one field, and that is the whole story of #824 and #385. Mutator marks arrive K times per checkpoint
/// cycle — once per unit of work — while the checkpoint writes a page once and acks once. So any scheme where the
/// checkpoint decrements the mutator's counter is wrong in one of two directions: ack one and K-1 marks are stranded
/// for ever, so the page can never be evicted and the cache starves after tens of minutes (#824); ack all K and marks
/// belonging to units of work still in flight are destroyed, so a page goes evictable with unwritten bytes (#385).
/// Neither is reachable now, because neither party touches the other's field.
/// </para>
/// <para>
/// Built directly from <see cref="ChangeSet"/> and <c>PagedMMF</c> rather than by driving the tick fence: these are
/// arithmetic properties of the accounting, and a test that needs a simulation to answer them is a detector, not a
/// proof. Everything here is deterministic and has no timing component.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
internal sealed class ChangeSetDirtyMarkConservationTests : TestBase<ChangeSetDirtyMarkConservationTests>
{
    /// <summary>
    /// Drives ONE real checkpoint cycle and waits for it. Deliberately the real cycle rather than a hand-simulated
    /// "discharge the debt" — the ack protocol IS the thing under test, so a test that models it can only ever agree
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
        var page = dbe.MMF.FirstResidentPage();
        Assert.That(page, Is.GreaterThanOrEqualTo(0), "engine init should leave at least one resident page to work with");
        return page;
    }

    /// <summary>
    /// A quiescent engine owes nothing and holds no marks.
    /// </summary>
    /// <remarks>
    /// This is the whole conservation claim in one assertion, and it is the one that would have caught both leaks on the
    /// day they were written. It is deliberately taken at rest — with no unit of work open and the checkpoint idle — because
    /// a live count cannot distinguish a leaked mark from a busy one, which is why both #817 and #824 survived years of a
    /// green suite. At rest there is no such thing as busy.
    /// </remarks>
    [Test]
    [VerifiesRule("PS-05")]
    [VerifiesRule("PS-10")]
    public void AQuiescentEngineHoldsNoMarksAndOwesNoWrites()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RunCheckpoint(dbe);

        Assert.Multiple(() =>
        {
            Assert.That(dbe.MMF.CountPagesWithDirtyMarks(), Is.Zero,
                "a mark outstanding with nothing running is a mark whose owner never released it");
            Assert.That(dbe.MMF.CountPagesWithWritebackDebt(), Is.Zero,
                "a page owed with nothing running is a page the checkpoint failed to write — it can never be evicted");
        });
    }

    // ── Arm 1: mutator marks are conserved, whatever the shape of the work ───────────────────────────────────────────

    /// <summary>
    /// A unit of work releases exactly what it took, so the counter is back where it started the moment it finishes —
    /// no checkpoint involved, because the checkpoint has no business in this number.
    /// </summary>
    [Test]
    [VerifiesRule("PS-05")]
    public void OneUnitOfWork_ReleasesEveryMarkItTook()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var page = ResidentDataPage(dbe);
        var baseline = dbe.MMF.DirtyCounterOf(page);

        var cs = new ChangeSet(dbe.MMF);
        cs.AddByMemPageIndex(page);
        cs.RegisterReDirty(page);           // re-dirty inside the same unit of work
        cs.RegisterReDirty(page);
        Assert.That(dbe.MMF.DirtyCounterOf(page), Is.EqualTo(baseline + 3), "three marks taken");

        cs.ReleaseDirtyMarks();

        Assert.That(dbe.MMF.DirtyCounterOf(page), Is.EqualTo(baseline),
            "a unit of work releases every mark it took — retaining one 'for the checkpoint' is what leaked");
    }

    /// <summary>
    /// K units of work over one page, no checkpoint between them. The counter returns to baseline regardless of K:
    /// that independence is the property #824 lacked, and the one that decides whether the page cache survives a long
    /// run.
    /// </summary>
    /// <remarks>
    /// Occupancy/bitmap pages make K large by construction — every transaction that grows a segment marks them — so
    /// they were the first to become permanently unevictable. Measured in the SpaceBattle demo, one page reached 26
    /// outstanding marks and the dirty-page count climbed monotonically to cache exhaustion in about thirty minutes.
    /// Raising the checkpoint frequency reduced the residue 22x, which is the signature of a per-cycle imbalance
    /// rather than an absolute leak.
    /// </remarks>
    [Test]
    [VerifiesRule("PS-05")]
    public void ManyUnitsOfWork_LeaveNoResidue_WhateverTheirCount([Values(1, 2, 8, 26)] int k)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var page = ResidentDataPage(dbe);
        var baseline = dbe.MMF.DirtyCounterOf(page);

        for (var i = 0; i < k; i++)
        {
            var cs = new ChangeSet(dbe.MMF);
            cs.AddByMemPageIndex(page);
            cs.RegisterReDirty(page);
            cs.ReleaseDirtyMarks();
        }

        Assert.That(dbe.MMF.DirtyCounterOf(page), Is.EqualTo(baseline),
            $"{k} units of work touched one page; the counter must not depend on how many");
    }

    /// <summary>Rollback is the same accounting as commit — it returns the marks, no more and no less.</summary>
    [Test]
    [VerifiesRule("PS-05")]
    public void Rollback_ReleasesEveryMarkItTook()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var page = ResidentDataPage(dbe);
        var baseline = dbe.MMF.DirtyCounterOf(page);

        var cs = new ChangeSet(dbe.MMF);
        cs.AddByMemPageIndex(page);
        cs.RegisterReDirty(page);
        cs.Reset();

        Assert.That(dbe.MMF.DirtyCounterOf(page), Is.EqualTo(baseline));
    }

    // ── Arm 2: writeback debt outlives the marks, and only a durable write clears it ─────────────────────────────────

    /// <summary>
    /// The page stays owed after its unit of work has released every mark, and stops being owed only once a checkpoint
    /// has written it. This is the protection the retained mark used to approximate.
    /// </summary>
    [Test]
    [VerifiesRule("PS-10")]
    public void ReleasingMarks_LeavesThePageOwed_UntilACheckpointWritesIt()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var page = ResidentDataPage(dbe);

        var cs = new ChangeSet(dbe.MMF);
        cs.AddByMemPageIndex(page);
        cs.ReleaseDirtyMarks();

        Assert.That(dbe.MMF.DirtyCounterOf(page), Is.Zero, "no marks outstanding");
        Assert.That(dbe.MMF.HasWritebackDebt(page), Is.True,
            "the bytes are not on disk yet, so the page must not be evictable — that obligation is the debt, not the mark");

        RunCheckpoint(dbe);

        Assert.That(dbe.MMF.HasWritebackDebt(page), Is.False,
            "a cycle that wrote and fsynced the page discharges its debt, and only then may it be evicted");
    }

    /// <summary>
    /// A modification that lands after the checkpoint sampled the page leaves it owed, so the next cycle rewrites it.
    /// CP-04's re-dirty defence, falling out of the generation comparison instead of needing a counter floor to
    /// survive a decrement.
    /// </summary>
    /// <remarks>
    /// Modelled by publishing a stale capture — exactly what a cycle that snapshotted the page before the modification
    /// would publish after its fsync. If <c>MarkCaptured</c> honoured it, the page would go clean while holding bytes
    /// that reached no disk, which is the lost write in #385 and the double-alloc in #301.
    /// </remarks>
    [Test]
    [VerifiesRule("PS-10")]
    [VerifiesRule("CP-04")]
    public void AModificationAfterTheCapture_KeepsThePageOwed()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var page = ResidentDataPage(dbe);

        // Settle the page by hand rather than by running a cycle: the subject here is the ordering rule between a capture
        // and a modification, and driving it through a real checkpoint would only add a scheduler to a question that has
        // no timing in it. The neighbouring arms already prove a real cycle discharges a real debt.
        dbe.MMF.MarkCaptured(page, dbe.MMF.WritebackGenOf(page));
        Assert.That(dbe.MMF.HasWritebackDebt(page), Is.False, "precondition: the page starts settled");

        var staleCapture = dbe.MMF.WritebackGenOf(page);   // what an in-flight cycle would have sampled
        dbe.MMF.MarkPageModified(page);                    // ... and here the writer changes the page underneath it
        dbe.MMF.MarkCaptured(page, staleCapture);          // ... and the cycle completes, publishing its stale sample

        Assert.That(dbe.MMF.HasWritebackDebt(page), Is.True,
            $"the write covered the older bytes, so it cannot settle the newer ones — the page stays owed "
            + $"(page {page}, stale {staleCapture}, writeback {dbe.MMF.WritebackGenOf(page)}, captured {dbe.MMF.CapturedGenOf(page)})");
    }

    /// <summary>A stale capture can never walk the durable watermark backwards.</summary>
    [Test]
    [VerifiesRule("PS-10")]
    public void CaptureIsMonotonic()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var page = ResidentDataPage(dbe);

        dbe.MMF.MarkPageModified(page);
        var newer = dbe.MMF.WritebackGenOf(page);
        dbe.MMF.MarkCaptured(page, newer);
        Assert.That(dbe.MMF.HasWritebackDebt(page), Is.False);

        dbe.MMF.MarkCaptured(page, newer - 1);             // a slower writer's older sample arriving late

        Assert.That(dbe.MMF.CapturedGenOf(page), Is.EqualTo(newer),
            "an out-of-order publication must not resurrect an obligation that a newer write already discharged");
        Assert.That(dbe.MMF.HasWritebackDebt(page), Is.False);
    }
}
