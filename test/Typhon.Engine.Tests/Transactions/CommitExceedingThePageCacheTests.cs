using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A commit that needs more of the page cache than the cache has is refused with <see cref="TransactionExceedsPageCacheException"/>, before its WAL append
/// and without waiting out the back-pressure timeout, and its rollback completes (#831). It used to wait the timeout out, throw a back-pressure timeout
/// that advised running fewer concurrent transactions, and leave its rollback undone: the rollback met the same wall, and so did the retry in Dispose.
/// </summary>
[TestFixture]
[NonParallelizable] // PagedMMF.RefusedOversizedCommits is a static.
class CommitExceedingThePageCacheTests : TestBase<CommitExceedingThePageCacheTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Spawn(Transaction tx, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var a = new CompA(i);
            tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
        }
    }

    /// <summary>
    /// 100 000 spawns of a 16-byte component in one commit, into an 8 MiB cache (1 024 pages): about twice what fits. 48 000 commit; 50 000 did not.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [Property("CacheSize", 8 * 1024 * 1024)]
    [VerifiesRule("PS-22")]
    [VerifiesRule("EP-02")]
    public void ACommitLargerThanTheCache_IsRefusedBeforeItsTimeout_AndRollsBackClean()
    {
        using var dbe = SetupEngine();
        var refused = Interlocked.Read(ref PagedMMF.RefusedOversizedCommits);
        using var tx = dbe.CreateQuickTransaction();
        Spawn(tx, 100_000);

        var sw = Stopwatch.StartNew();
        var thrown = Assert.Throws<TransactionExceedsPageCacheException>(() => tx.Commit());
        sw.Stop();
        Assert.DoesNotThrow(tx.Dispose, "the rollback completed: Dispose has nothing left to retry (the using disposes it again, a no-op)");

        Assert.That(sw.Elapsed, Is.LessThan(TimeoutOptions.Current.PageCacheBackpressureTimeout), "refused, not timed out");
        Assert.That(Interlocked.Read(ref PagedMMF.RefusedOversizedCommits) - refused, Is.EqualTo(1));
        Assert.That(thrown.ErrorCode, Is.EqualTo(TyphonErrorCode.StorageCapacityExceeded));
        Assert.That(thrown.IsTransient, Is.False, "the same commit fails the same way");
        Assert.That(thrown.CachePages, Is.EqualTo(1024));
        Assert.That(thrown.HeldPages, Is.GreaterThan(thrown.CachePages * 9 / 10), "it held the cache itself");

        Assert.That(dbe.MMF.CountPagesWithDirtyMarks(), Is.Zero, "every mark released (PS-05)");
        using (var read = dbe.CreateQuickTransaction())
        {
            Assert.That(read.Query<CompAArch>().Count(), Is.Zero, "nothing of the refused commit is visible");
        }

        using (var next = dbe.CreateQuickTransaction())
        {
            Spawn(next, 1_000);
            Assert.That(next.Commit(), Is.True, "the engine commits on");
        }

        using (var read = dbe.CreateQuickTransaction())
        {
            Assert.That(read.Query<CompAArch>().Count(), Is.EqualTo(1_000));
        }
    }

    /// <summary>
    /// Just past the limit — 50 000 spawns, where 48 000 commit — the commit is refused before its append, never left to fail in its publish: a publish
    /// that fails stops the engine (AP-03). That is what an epoch refresh between the placements did: it let this commit reach its append, with 542 pages
    /// marked, and its publish needed ~380 more. The publish's bound is cut to 3 s, so that regression fails fast.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [Property("CacheSize", 8 * 1024 * 1024)]
    public void ACommitJustOverTheLimit_IsRefusedBeforeItsAppend()
    {
        using var dbe = SetupEngine();
        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.CommitPublishBackpressureTimeout = TimeSpan.FromSeconds(3);
        TimeoutOptions.Current = timeouts;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            Spawn(tx, 50_000);
            Assert.Throws<TransactionExceedsPageCacheException>(() => tx.Commit());
            Assert.That(dbe.CommitPipelineFailure, Is.Null, "the engine still commits");
        }
        finally
        {
            TimeoutOptions.Current = saved;
        }
    }

    /// <summary>
    /// A publish larger than the cache commits: 20 000 updates of a component with three indexes, into 8 MiB. Its B+Tree writes touch pages PREPARE never
    /// did, so PREPARE's wall does not stop it, and it kept every mark it took: it filled the cache with its own write set, waited out its bound and
    /// stopped the engine (CommitPublishIncompleteException) — 10 000 already did. Under pressure it now gives its marks back between entries
    /// (RefreshBetweenCommitSteps), and the checkpoint writes and frees what it published. Every value and every index entry is then the new one. The
    /// publish's bound is cut to 5 s, so the old behaviour fails fast.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 8 * 1024 * 1024)]
    [VerifiesRule("EP-02")]
    public void APublishLargerThanTheCache_GivesBackItsMarks_AndCommits()
    {
        const int n = 20_000;
        const int moved = 10_000_000;
        using var dbe = SetupEngine();
        var ids = new EntityId[n];
        for (var s = 0; s < n; s += 1_000)
        {
            using var seed = dbe.CreateQuickTransaction();
            for (var i = s; i < s + 1_000; i++)
            {
                ids[i] = seed.Spawn<CompDArch>(CompDArch.D.Set(new CompD(i, i, i)));
            }

            Assert.That(seed.Commit(), Is.True);
        }

        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.CommitPublishBackpressureTimeout = TimeSpan.FromSeconds(5);
        TimeoutOptions.Current = timeouts;
        try
        {
            var refreshes = Interlocked.Read(ref PagedMMF.PublishStepRefreshes);
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < n; i++)
                {
                    tx.OpenMut(ids[i]).Set(CompDArch.D, new CompD(i + 0.5f, i + moved, i + 0.25));
                }

                Assert.That(tx.Commit(), Is.True);
            }

            Assert.That(dbe.CommitPipelineFailure, Is.Null, "the engine still commits");
            Assert.That(Interlocked.Read(ref PagedMMF.PublishStepRefreshes) - refreshes, Is.GreaterThan(0),
                "premise: the publish ran under pressure and gave its marks back");
        }
        finally
        {
            TimeoutOptions.Current = saved;
        }

        using var read = dbe.CreateQuickTransaction();
        Assert.That(read.Query<CompDArch>().WhereField<CompD>(d => d.B >= moved).Count(), Is.EqualTo(n), "every unique key moved");
        Assert.That(read.Query<CompDArch>().WhereField<CompD>(d => d.B < moved).Count(), Is.Zero, "no old key left");
        foreach (var i in new[] { 0, n / 2, n - 1 })
        {
            var d = read.Open(ids[i]).Read(CompDArch.D);
            Assert.That((d.A, d.B, d.C), Is.EqualTo((i + 0.5f, i + moved, i + 0.25)));
        }
    }

    /// <summary>The check refuses only a commit that cannot fit: 40 000 spawns into the same cache commit, under back-pressure, and none is refused.</summary>
    [Test]
    [CancelAfter(30_000)]
    [Property("CacheSize", 8 * 1024 * 1024)]
    public void ACommitThatFitsTheCache_IsNotRefused()
    {
        using var dbe = SetupEngine();
        var refused = Interlocked.Read(ref PagedMMF.RefusedOversizedCommits);
        using (var tx = dbe.CreateQuickTransaction())
        {
            Spawn(tx, 40_000);
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.That(Interlocked.Read(ref PagedMMF.RefusedOversizedCommits), Is.EqualTo(refused));
        using var read = dbe.CreateQuickTransaction();
        Assert.That(read.Query<CompAArch>().Count(), Is.EqualTo(40_000));
    }
}
