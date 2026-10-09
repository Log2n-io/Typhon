using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// CK-02 write ordering (#585): inside one checkpoint write pass, every protected page must be persisted BEFORE any plain data page is written.
/// </summary>
/// <remarks>
/// <para>
/// A protected segment-directory page (CK-05) is persisted as write → fsync → slot flip, and that fsync is <b>file-wide</b>, not page-scoped. Reached partway
/// through a checkpoint batch it therefore makes every plain data page written earlier in the same pass durable — while the cycle's flush2 barrier
/// (<c>RequestFlush</c> + <c>WaitForDurable</c>) has not run yet, because <c>CheckpointManager</c> issues it once the batch's writes return (CK-15). Any commit
/// that appended and published between the step-1 barrier and that page's capture would then sit in the data file with its WAL record still in the ring buffer:
/// "captured ⊆ durable" inverted, i.e. a phantom partial write of a never-durable transaction, which Typhon cannot undo.
/// </para>
/// <para>
/// The fix hoists protected pages to the front of the batch, so their fsync can only ever precede this pass's plain writes. <c>SavePages</c> already reaches the
/// same guarantee via a dedicated pre-pass; the checkpoint path expresses it as an ordering to keep its in-place written-front/skipped-back partition intact.
/// </para>
/// <para>
/// <b>How the ordering is observed.</b> The engine evaluates the invariant itself and exposes <c>CheckpointProtectedAfterPlainWriteCount</c>. Reconstructing the
/// order from outside does not work: <c>PageWriteInterceptor</c> also fires on the direct and async structural write paths, so a background <c>SavePages</c>
/// overlapping the pass is indistinguishable from the pass's own plain writes — an earlier version of this test read exactly that and mis-attributed it.
/// </para>
/// <para>
/// <b>Why the write pass is driven directly.</b> <c>ForceCheckpoint</c> only signals a background thread, so a test that hooks around it races the cycle and
/// usually measures an empty batch. Calling <c>CollectDirtyMemPageIndices</c> + <c>WritePagesForCheckpoint</c> is the same code the cycle runs, minus the
/// timing. Dirty counters are intentionally left alone (the real caller decrements them after its fsync), so the pages simply stay dirty for the next cycle.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class CheckpointProtectedPageOrderingTests : TestBase<CheckpointProtectedPageOrderingTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    [Test]
    [CancelAfter(60_000)]
    public void CheckpointWritePass_PersistsEveryProtectedPage_BeforeTheFirstPlainPageWrite()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();

        var mmf = dbe.MMF;
        long protectedPersisted = 0;
        var round = 0;

        // Segment create/grow is what stamps a directory twin (LogicalSegment.CreateOrGrow → GetOrAllocateDirectoryTwin) and leaves it dirty. Growing then
        // immediately running the write pass keeps that page in the same batch as ordinary component pages — the interleaving the bug needs. Repeat until such a
        // batch actually occurs: the structural SavePages path also persists twins, so a given round may find them already clean.
        while (protectedPersisted == 0 && round < 40)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < 400; i++)
                {
                    var c = new CompA(round * 1000 + i, round, i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in c));
                }
                tx.Commit();
            }

            round++;

            var dirty = mmf.CollectDirtyMemPageIndices();
            if (dirty.Length == 0)
            {
                continue;
            }

            var before = mmf.CheckpointProtectedPagePersistCount;
            mmf.WritePagesForCheckpoint(dirty, dbe.StagingBufferPool, out _);
            protectedPersisted = mmf.CheckpointProtectedPagePersistCount - before;
        }

        // Precondition — with no protected page in any pass the assertion below is vacuous.
        Assert.That(protectedPersisted, Is.GreaterThan(0),
            "precondition: no checkpoint batch contained a protected directory page, so this test would prove nothing");

        Assert.That(mmf.CheckpointProtectedAfterPlainWriteCount, Is.Zero,
            "a protected page was persisted AFTER a plain data page in the same pass — its file-wide fsync then makes that page durable ahead of the "
            + "flush2 barrier (CK-02, #585)");
    }

    /// <summary>A pass of dirty pages holding at least one protected directory page and plain pages, ordered for the checkpoint.</summary>
    private static (int[] Dirty, int ProtectedCount) OrderedPassWithProtectedPages(DatabaseEngine dbe)
    {
        var mmf = dbe.MMF;
        int[] dirty = [];
        var protectedCount = 0;
        for (var round = 0; protectedCount == 0 && round < 40; round++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < 400; i++)
                {
                    var c = new CompA(round * 1000 + i, round, i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in c));
                }

                tx.Commit();
            }

            dirty = mmf.CollectDirtyMemPageIndices();
            protectedCount = mmf.OrderForCheckpointWrite(dirty);
        }

        Assert.That(protectedCount, Is.GreaterThan(0), "precondition: no pass held a protected directory page, so this test would prove nothing");
        Assert.That(dirty.Length, Is.GreaterThan(protectedCount), "precondition: the pass also holds plain pages");
        return (dirty, protectedCount);
    }

    /// <summary>The verifier: given the whole pass, a wave meant for plain pages leaves every protected page owed, at the back, and persists none.</summary>
    private static void AssertAPlainWaveLeavesProtectedPages(DatabaseEngine dbe, int[] dirty, int protectedCount, CheckpointWriteFilter filter)
    {
        var mmf = dbe.MMF;
        var persistedBefore = mmf.CheckpointProtectedPagePersistCount;
        var deferredBefore = Interlocked.Read(ref mmf.CheckpointPagesDeferredForOrder);
        mmf.WritePagesForCheckpoint(dirty, dbe.StagingBufferPool, out var written, filter: filter);

        Assert.That(mmf.CheckpointProtectedPagePersistCount, Is.EqualTo(persistedBefore), "a plain wave persisted a protected page");
        Assert.That(Interlocked.Read(ref mmf.CheckpointPagesDeferredForOrder) - deferredBefore, Is.EqualTo(protectedCount),
            "a plain wave did not leave every protected page for the next pass");
        for (var i = written; i < dirty.Length; i++)
        {
            Assert.That(mmf.IsProtectedFilePageForTests(mmf.GetFilePageIndex(dirty[i])), Is.True, "a plain page was left unwritten by the plain wave");
        }
    }

    /// <summary>
    /// The checkpoint's own pass (CK-16): ordered protected pages first, then by file position; and a parallel wave, which writes plain pages only, leaves a
    /// protected page it meets for the next pass instead of running its file-wide fsync beside plain writes its batch has not settled.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CK-16")]
    public void APass_IsOrderedProtectedFirst_AndAPlainWaveLeavesProtectedPagesForTheNextPass()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        var (dirty, protectedCount) = OrderedPassWithProtectedPages(dbe);

        var mmf = dbe.MMF;
        var previous = -1;
        for (var i = 0; i < dirty.Length; i++)
        {
            var filePage = mmf.GetFilePageIndex(dirty[i]);
            var isProtected = filePage > 0 && mmf.IsProtectedFilePageForTests(filePage);
            Assert.That(isProtected, Is.EqualTo(i < protectedCount), $"page {i} of the pass (file page {filePage}) is out of the protected-first order");
            if (i >= protectedCount)
            {
                Assert.That(filePage, Is.GreaterThan(previous), $"plain page {i} of the pass is out of file order");
                previous = filePage;
            }
        }

        AssertAPlainWaveLeavesProtectedPages(dbe, dirty, protectedCount, CheckpointWriteFilter.PlainOnly);
    }

    /// <summary>The verifier can fail: a wave writing every kind of page — a single call over the pass, before CK-16 — persists the protected ones.</summary>
    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("CK-16")]
    public void Mutant_AWaveWritingEveryKind_IsReported()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        var (dirty, protectedCount) = OrderedPassWithProtectedPages(dbe);

        RuleMutants.AssertDetects("CK-16", "a plain wave persisted a protected page",
            () => AssertAPlainWaveLeavesProtectedPages(dbe, dirty, protectedCount, CheckpointWriteFilter.All));
    }
}
