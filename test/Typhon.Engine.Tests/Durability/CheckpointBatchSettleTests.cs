using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// A checkpoint pass settles its pages batch by batch (CK-15): the cache owes less after each batch's fsync, so a writer the cache holds back gets a page
/// long before the pass has written everything it collected.
/// </summary>
/// <remarks>
/// Settled only at the end, a pass under a write storm wrote the whole cache before freeing one page — 8 GiB at ~22k pages/s — and every writer waiting
/// for a slot timed out after 5 s (MarketHardeningTests, 75M items over 8 GiB). The tests shrink the batch and its sub-batches through the manager's seams,
/// so that a pass of a few thousand pages is written in parallel waves and settled many times.
/// </remarks>
[TestFixture]
[Property("CacheSize", 32 * 1024 * 1024)]
internal sealed class CheckpointBatchSettleTests : TestBase<CheckpointBatchSettleTests>
{
    private const int SubBatchPages = 4;
    private const int BatchPages = 16;   // waves of four sub-batches of four pages

    private static EntityId[] Spawn(DatabaseEngine dbe)
    {
        var ids = new EntityId[30 * 400];
        for (var round = 0; round < 30; round++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < 400; i++)
            {
                var c = new CompA(round * 1000 + i, round, i);
                ids[round * 400 + i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in c));
            }

            tx.Commit();
        }

        return ids;
    }

    /// <summary>Forces a cycle and returns the cache's writeback debt before it and at each of its fsyncs.</summary>
    private static (int OwedBefore, List<int> OwedAtFsync) ForceCycleSamplingDebt(DatabaseEngine dbe)
    {
        var mmf = dbe.MMF;
        var owedAtFsync = new List<int>();
        mmf.FlushToDiskInterceptor = () =>
        {
            lock (owedAtFsync)
            {
                owedAtFsync.Add(mmf.DebtPageCountForTests);
            }
        };

        var owedBefore = mmf.DebtPageCountForTests;
        try
        {
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(30)), Is.True, "the forced cycle did not cover");
        }
        finally
        {
            mmf.FlushToDiskInterceptor = null;
        }

        Assert.That(owedBefore, Is.GreaterThan(100), "premise: enough owed pages for several batches");
        return (owedBefore, owedAtFsync);
    }

    /// <summary>The verifier: the debt seen at successive fsyncs falls about once per batch, since each batch's pages are settled after its fsync.</summary>
    private static void AssertSettledBatchByBatch(int owedBefore, List<int> owedAtFsync)
    {
        var drops = 0;
        for (var i = 1; i < owedAtFsync.Count; i++)
        {
            if (owedAtFsync[i] < owedAtFsync[i - 1])
            {
                drops++;
            }
        }

        Assert.That(drops, Is.GreaterThanOrEqualTo(owedBefore / BatchPages / 2),
            $"the debt fell between only {drops} of {owedAtFsync.Count} fsyncs for {owedBefore} owed pages: the pass did not settle batch by batch "
            + $"(owed at each fsync: {string.Join(", ", owedAtFsync)})");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CK-15")]
    public void ACheckpointPass_FreesPages_BatchByBatch_ThroughParallelWaves()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        var ck = dbe.CheckpointManager;
        ck.WriteBatchMaxPagesForTest = BatchPages;
        ck.WriteSubBatchPagesForTest = SubBatchPages;
        Spawn(dbe);

        var wavesBefore = Volatile.Read(ref ck.ParallelWaveCount);
        var (owedBefore, owedAtFsync) = ForceCycleSamplingDebt(dbe);

        Assert.That(Volatile.Read(ref ck.ParallelWaveCount), Is.GreaterThan(wavesBefore), "premise: the pass was written in parallel waves");
        AssertSettledBatchByBatch(owedBefore, owedAtFsync);
        Assert.That(dbe.MMF.DebtPageCountForTests, Is.LessThan(owedBefore), "the cycle settled nothing");
    }

    /// <summary>The verifier can fail: a pass settled once, at its end — one batch larger than the pass — keeps the debt flat until its one fsync.</summary>
    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("CK-15")]
    public void Mutant_APassSettledOnlyAtItsEnd_IsReported()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        dbe.CheckpointManager.WriteBatchMaxPagesForTest = int.MaxValue;
        Spawn(dbe);

        var (owedBefore, owedAtFsync) = ForceCycleSamplingDebt(dbe);

        RuleMutants.AssertDetects("CK-15", "did not settle batch by batch", () => AssertSettledBatchByBatch(owedBefore, owedAtFsync));
    }

    /// <summary>
    /// With a test intercepting page writes, the waves are written one sub-batch after the other on the checkpoint thread: a simulated crash at write k
    /// must leave nothing written after it, which a writer still running beside the one that threw would break.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CK-15")]
    public void AnInterceptedPass_WritesNoParallelWave()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        var ck = dbe.CheckpointManager;
        ck.WriteBatchMaxPagesForTest = BatchPages;
        ck.WriteSubBatchPagesForTest = SubBatchPages;
        Spawn(dbe);

        long intercepted = 0;
        dbe.MMF.PageWriteInterceptor = _ => Interlocked.Increment(ref intercepted);
        var wavesBefore = Volatile.Read(ref ck.ParallelWaveCount);
        try
        {
            Assert.That(ck.ForceCheckpointAndWait(TimeSpan.FromSeconds(30)), Is.True, "the forced cycle did not cover");
        }
        finally
        {
            dbe.MMF.PageWriteInterceptor = null;
        }

        Assert.That(Interlocked.Read(ref intercepted), Is.GreaterThan(BatchPages * 2), "premise: the pass wrote enough pages for parallel waves");
        Assert.That(Volatile.Read(ref ck.ParallelWaveCount), Is.EqualTo(wavesBefore), "a wave was written in parallel while page writes were intercepted");
    }

    /// <summary>Pages written by parallel waves read back intact from a reopened file: every page's checksum is verified as it is loaded.</summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CK-15")]
    public void PagesWrittenByParallelWaves_ReadBackAfterAReopen()
    {
        EntityId[] ids;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            var ck = dbe.CheckpointManager;
            ck.WriteBatchMaxPagesForTest = BatchPages;
            ck.WriteSubBatchPagesForTest = SubBatchPages;
            ids = Spawn(dbe);

            var wavesBefore = Volatile.Read(ref ck.ParallelWaveCount);
            Assert.That(ck.ForceCheckpointAndWait(TimeSpan.FromSeconds(30)), Is.True, "the forced cycle did not cover");
            Assert.That(Volatile.Read(ref ck.ParallelWaveCount), Is.GreaterThan(wavesBefore), "premise: the pass was written in parallel waves");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateReadOnlyTransaction();
            for (var k = 0; k < ids.Length; k++)
            {
                var a = tx.Open(ids[k]).Read(CompAArch.A);
                if (a.A != (k / 400) * 1000 + k % 400 || a.B != k / 400)
                {
                    Assert.Fail($"entity {k} read back ({a.A}, {a.B}) after the reopen");
                }
            }
        }
    }
}
