using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A revision chain's indices are 16-bit (#1382). An entity written faster than the oldest snapshot lets its chain compact grew past short.MaxValue
/// entries: the count wrapped negative, every reader misread the chain, and the compaction's stackalloc of a negative length overflowed the stack — the
/// market with 4 desks under 16 writers crashed the process. A full chain now refuses the append before writing anything, and takes writes again once the
/// snapshot that kept it full ends.
/// </summary>
[TestFixture]
[NonParallelizable] // ComponentRevisionManager.MaxChainRevisionIndex and TimeoutOptions.Current are process-wide
class RevisionChainFullTests : TestBase<RevisionChainFullTests>
{
    // A reopen must find the WAL it crashed with; no periodic checkpoint, the test says when one runs.
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
        o.Wal.WalDirectory = null;
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CHAIN-03")]
    public void AFullChain_RefusesTheAppend_AndTakesWritesAgainOnceTheSnapshotEnds()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(1, 0)), StatsArch.Tag.Set(new StatsTag(1)));
            Assert.That(tx.Commit(), Is.True);
        }

        var max = ComponentRevisionManager.MaxChainRevisionIndex;
        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.RevisionChainLockTimeout = TimeSpan.FromMilliseconds(100);   // the refused first write waits this long for a compaction
        TimeoutOptions.Current = timeouts;
        ComponentRevisionManager.MaxChainRevisionIndex = 4 * ComponentRevisionManager.CompRevCountInNext;
        try
        {
            var refusals = ComponentRevisionManager.ChainFullRefusals;
            var lastCommitted = 0;
            Exception refused = null;
            var reader = dbe.CreateReadOnlyTransaction();   // the oldest snapshot: every revision after it is kept
            try
            {
                for (var i = 1; i <= 200 && refused == null; i++)
                {
                    try
                    {
                        using var tx = dbe.CreateQuickTransaction();
                        tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(1, i));
                        Assert.That(tx.Commit(), Is.True);
                        lastCommitted = i;
                    }
                    catch (ResourceExhaustedException e)
                    {
                        refused = e;
                    }
                }
            }
            finally
            {
                reader.Dispose();
            }

            Assert.That(refused, Is.Not.Null, "the chain filled while the snapshot kept every revision");
            Assert.That(ComponentRevisionManager.ChainFullRefusals - refusals, Is.GreaterThan(0));
            using (var check = dbe.CreateReadOnlyTransaction())
            {
                Assert.That(check.Open(id).TryRead<StatsRanked>(out var value), Is.True, "the full chain still reads");
                Assert.That(value.Score, Is.EqualTo(lastCommitted), "the refused write left no trace");
            }

            // The snapshot ended: the next transactions' cleanups compact the chain, and the writes go through again.
            var wrote = false;
            for (var i = 0; i < 50 && !wrote; i++)
            {
                try
                {
                    using var tx = dbe.CreateQuickTransaction();
                    tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(1, 1_000 + i));
                    wrote = tx.Commit();
                }
                catch (ResourceExhaustedException)
                {
                }
            }

            Assert.That(wrote, Is.True, "writes again once the snapshot that kept the chain full ended");
        }
        finally
        {
            ComponentRevisionManager.MaxChainRevisionIndex = max;
            TimeoutOptions.Current = saved;
        }
    }

    /// <summary>
    /// #1209: a chain full on the disk, then an update in the log's window and a crash. The replay appended through the same guard as a live write, with no
    /// restart possible at open, and the refusal failed the open — for a history no snapshot could read any more. The open collapses the chain first.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CHAIN-03")]
    public void AChainFullOnDisk_ThenAReplayedUpdate_StillOpens()
    {
        const int Updates = 40;
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                id = tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(1, 0)), StatsArch.Tag.Set(new StatsTag(1)));
                Assert.That(tx.Commit(), Is.True);
            }

            // The oldest snapshot keeps every revision: the chain holds the spawn and each update when the checkpoint writes it.
            using (dbe.CreateReadOnlyTransaction())
            {
                for (var i = 1; i <= Updates; i++)
                {
                    using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                    tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(1, i));
                    Assert.That(tx.Commit(), Is.True);
                }

                Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
                using var last = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                last.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(1, 1_000));
                Assert.That(last.Commit(), Is.True, "the update in the window");
            }

            dbe.SimulateHardCrash();
        }

        // The chain on disk holds the spawn and every update: counted full from here, so the replayed one cannot be appended to it as it stands.
        var max = ComponentRevisionManager.MaxChainRevisionIndex;
        ComponentRevisionManager.MaxChainRevisionIndex = 1 + Updates + ComponentRevisionManager.CompRevCountInNext;
        try
        {
            var collapsed = RecoveryApplier.ChainsCollapsedForReplay;
            using var scope2 = ServiceProvider.CreateScope();
            var reopened = Open(scope2);
            Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
            Assert.That(RecoveryApplier.ChainsCollapsedForReplay - collapsed, Is.EqualTo(1), "premise: the replay met the full chain");
            using var read = reopened.CreateReadOnlyTransaction();
            Assert.That(read.Open(id).TryRead<StatsRanked>(out var value), Is.True);
            Assert.That(value.Score, Is.EqualTo(1_000), "the replayed update");
            reopened.Dispose();
        }
        finally
        {
            ComponentRevisionManager.MaxChainRevisionIndex = max;
        }
    }

    private static DatabaseEngine Open(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();
        return dbe;
    }
}
