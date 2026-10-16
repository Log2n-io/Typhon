using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A revision or content chunk of a Versioned table, freed after the open, stays allocated until a checkpoint cycle that started after the free has
/// written every page it collected (CK-17): until then the data file may still name it. Held, it belongs to no one — a check that tells a live chain from
/// a released one must take it for released.
/// </summary>
[TestFixture]
internal sealed class FreeQuarantineTests : TestBase<FreeQuarantineTests>
{
    private DatabaseEngine _dbe;

    // No periodic checkpoint: only the test's forced ones release the held frees.
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

    [SetUp]
    public void SetUpEngine()
    {
        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<MkTrader>();
        _dbe.RegisterComponentFromAccessor<MkWallet>();
        _dbe.InitializeArchetypes();
    }

    [TearDown]
    public void TearDownEngine()
    {
        _dbe?.Dispose();
        _dbe = null;
    }

    private int AllocateRevision()
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var cs = _dbe.MMF.CreateChangeSet();
        var a = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment.CreateChunkAccessor(cs);
        try
        {
            return _dbe.GetComponentTable<MkWallet>().CompRevTableSegment.AllocateChunk(cs, ref a);
        }
        finally
        {
            a.Dispose();
            cs.ReleaseDirtyMarks();
        }
    }

    private void Free(int chunk)
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        _dbe.GetComponentTable<MkWallet>().CompRevTableSegment.FreeChunk(chunk);
    }

    private bool Allocated(int chunk)
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        return _dbe.GetComponentTable<MkWallet>().CompRevTableSegment.IsChunkAllocated(chunk);
    }

    /// <summary>
    /// A release that meets back-pressure waits with its epoch moved on (#1382): the release worker, waiting with its pin, held every page tagged while
    /// it waited — one of the oldest pins of an epoch-held cache. The chunk is still freed once.
    /// </summary>
    [Test]
    [VerifiesRule("EP-02")]
    [CancelAfter(10_000)]
    public void AReleaseMeetingBackpressure_MovesItsEpoch_AndFreesTheChunk()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var chunk = AllocateRevision();
        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the pages are clean");
        Free(chunk);

        // The pin each round sees, on this thread: the process-wide counters move with every other fixture's refreshes.
        var pins = new List<long>();
        var releaser = Environment.CurrentManagedThreadId;
        var rounds = 0;

        // The checkpoint above sealed a generation — empty, the free came after it — made it releasable and signaled the release worker. Woken late, as
        // under the gate's eight loaded shards, the worker left it releasable: the seal below sealed nothing (FreeQuarantine.Seal), the drain released the
        // empty generation, met no miss, and the chunk stayed held. So under the release lock, this thread first releases what the cycle left, then seals
        // the free by hand, makes it releasable and releases it; the worker, waiting for the lock, then finds nothing releasable.
        lock (_dbe.MMF.ReleaseLockForTests)
        {
            _dbe.MMF.DrainReleases();
            var frees = (IQuarantinedFrees)segment;
            frees.SealFrees();
            Assert.That(frees.MarkReleasable(), Is.True);
            _dbe.MMF.EvictEvictablePagesForTest();   // the release's bitmap page misses
            _dbe.MMF.ForceBackpressureForTest = () =>
            {
                if (Environment.CurrentManagedThreadId != releaser)
                {
                    return false;
                }

                pins.Add(_dbe.EpochManager.CurrentThreadPinnedEpoch);
                return ++rounds <= 2;
            };
            try
            {
                _dbe.MMF.DrainReleases();
            }
            finally
            {
                _dbe.MMF.ForceBackpressureForTest = null;
            }
        }

        Assert.That(rounds, Is.GreaterThan(2), "premise: the release missed, and met two rounds of back-pressure");
        Assert.That(pins[1], Is.GreaterThan(pins[0]), "the first round moved the release's epoch");
        Assert.That(pins[2], Is.GreaterThan(pins[1]), "and the second");
        Assert.That((Allocated(chunk), segment.IsChunkHeld(chunk)), Is.EqualTo((false, false)), "freed, held no more");
        Assert.That(PagedMMF.RefreshableWaitDepth, Is.Zero, "the release's refreshable scope, closed");
    }

    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(10_000)]
    public void AFreedChunk_IsHeldUntilACheckpointCoversIt()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var chunk = AllocateRevision();
        Free(chunk);
        Assert.That((Allocated(chunk), segment.IsChunkHeld(chunk)), Is.EqualTo((true, true)), "freed: allocated still, and held");
        Assert.That(segment.IsChunkLive(chunk), Is.False, "held: no one's");
        Assert.That(_dbe.MMF.QuarantinedBytes, Is.EqualTo(segment.Stride), "the bytes held");

        // Many allocations: none takes the held chunk.
        var taken = new HashSet<int>();
        for (var i = 0; i < 2 * segment.ChunkCountPerPage; i++)
        {
            taken.Add(AllocateRevision());
        }

        Assert.That(taken, Does.Not.Contain(chunk), "the held chunk, never handed out");

        Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "a checkpoint that started after the free");
        _dbe.MMF.DrainReleases();
        Assert.That((Allocated(chunk), segment.IsChunkHeld(chunk)), Is.EqualTo((false, false)), "covered: freed, held no more");
        Assert.That(_dbe.MMF.QuarantinedBytes, Is.Zero, "nothing held");
    }

    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(10_000)]
    public void ASecondFreeOfAHeldChunk_IsADoubleFree_NotASecondRelease()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var chunk = AllocateRevision();
        var before = segment.DoubleFreeCount;
        Free(chunk);
        Free(chunk);
        Assert.That(segment.DoubleFreeCount - before, Is.EqualTo(1), "the second free, counted");
        Assert.That(segment.QuarantinedFreeCount, Is.EqualTo(1), "held once: released twice in two cycles, it would be freed under its next owner");
    }

    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(10_000)]
    public void AHeldChainRoot_IsNotTakenForItsEntitysChain()
    {
        EntityId id;
        using (var tx = _dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = 1 }), MkTraderArch.Wallet.Set(new MkWallet { Credits = 100 }));
            Assert.That(tx.Commit(), Is.True);
        }

        var table = _dbe.GetComponentTable<MkWallet>();
        int root;
        using (var tx = _dbe.CreateReadOnlyTransaction())
        {
            root = WalletRootOf(id);
        }

        Free(root);
        var cs = _dbe.MMF.CreateChangeSet();
        try
        {
            using var guard = EpochGuard.Enter(_dbe.EpochManager);
            var frees = new List<DeferredCleanupManager.DeferredChunkFreeEntry>();
            Assert.That(_dbe.DeferredCleanupManager.ReleaseDestroyedEntityChain(table, root, (long)id.RawValue, cs, frees),
                Is.EqualTo(DeferredCleanupManager.ChainRelease.NotThisEntitys), "a held root: released already, its bytes no one's");
            Assert.That(frees, Is.Empty, "nothing queued a second time");
        }
        finally
        {
            cs.ReleaseDirtyMarks();
        }
    }

    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(10_000)]
    public void HeldFreesPastTheBudget_AskForACheckpoint()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var budget = _dbe.MMF.FreeQuarantineBudgetBytes;
        var asked = 0;
        _dbe.MMF.FreeQuarantineFull = () => asked++;
        var chunks = new List<int>();
        while ((long)chunks.Count * segment.Stride <= budget + 4L * segment.Stride)
        {
            chunks.Add(AllocateRevision());
        }

        foreach (var chunk in chunks)
        {
            Free(chunk);
        }

        Assert.That(asked, Is.EqualTo(1), "five frees past the budget: one checkpoint asked for, not one per free");
        Assert.That(_dbe.MMF.QuarantineCheckpointRequests, Is.EqualTo(1));
    }

    /// <summary>A free made after a cycle started is not sealed by it: that cycle may have collected before the free's unlinking write.</summary>
    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(10_000)]
    public void AFreeMadeAfterACycleStarted_WaitsForTheNextCycle()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var chunk = AllocateRevision();
        _dbe.MMF.SealQuarantinedFrees();
        Free(chunk);
        _dbe.MMF.ReleaseQuarantinedFrees();
        _dbe.MMF.DrainReleases();
        Assert.That(segment.IsChunkHeld(chunk), Is.True, "the cycle that started before the free released nothing of it");

        _dbe.MMF.SealQuarantinedFrees();
        _dbe.MMF.ReleaseQuarantinedFrees();
        _dbe.MMF.DrainReleases();
        Assert.That((segment.IsChunkHeld(chunk), Allocated(chunk)), Is.EqualTo((false, false)), "the next cycle's");
    }

    /// <summary>A cycle that did not write every page it collected (CK-03's gate) releases nothing; a later covered one releases what it sealed.</summary>
    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(10_000)]
    public void ACycleThatDidNotCover_ReleasesNothing()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var chunk = AllocateRevision();
        Free(chunk);
        _dbe.MMF.SealQuarantinedFrees();
        _dbe.MMF.DrainReleases();
        Assert.That(segment.IsChunkHeld(chunk), Is.True, "sealed, not covered: held");

        _dbe.MMF.SealQuarantinedFrees();
        _dbe.MMF.ReleaseQuarantinedFrees();
        _dbe.MMF.DrainReleases();
        Assert.That(Allocated(chunk), Is.False, "a later covered cycle released it");
    }

    /// <summary>A second free of a chunk sealed in the other generation is a double free, not a second hold — released twice, it was freed under its next owner.</summary>
    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(10_000)]
    public void ASecondFreeAcrossASeal_IsHeldOnce()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var chunk = AllocateRevision();
        var before = segment.DoubleFreeCount;
        Free(chunk);
        _dbe.MMF.SealQuarantinedFrees();
        Free(chunk);
        Assert.That((segment.DoubleFreeCount - before, segment.QuarantinedFreeCount), Is.EqualTo((1L, 1)), "held once, the second free counted");
    }

    /// <summary>
    /// Frees racing seals and releases: every chunk is freed exactly once in the end, none twice, none lost. A release that ran early or twice frees a chunk
    /// under its next owner, which the bitmap counts as a double free.
    /// </summary>
    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(30_000)]
    public void FreesRacingSealsAndReleases_FreeEveryChunkOnce()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var baseline = segment.AllocatedChunkCount;
        var doubleFrees = segment.DoubleFreeCount;
        var stop = 0;
        var cycles = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                _dbe.MMF.SealQuarantinedFrees();
                _dbe.MMF.ReleaseQuarantinedFrees();
                _dbe.MMF.DrainReleases();
            }
        });
        var frees = new Task[2];
        for (var f = 0; f < frees.Length; f++)
        {
            frees[f] = Task.Run(() =>
            {
                for (var i = 0; i < 3_000; i++)
                {
                    Free(AllocateRevision());
                }
            });
        }

        Task.WaitAll(frees);
        Volatile.Write(ref stop, 1);
        cycles.Wait();
        _dbe.ReleaseHeldFreesForTest();
        Assert.That(segment.DoubleFreeCount - doubleFrees, Is.Zero, "no chunk freed twice");
        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(baseline), "every chunk freed in the end");
    }

    /// <summary>
    /// A held free is freed by the engine's own path: the checkpoint thread's covered cycles signal the release worker, which frees it — no test drain. Every
    /// other test releases on its own thread; without the worker's signal, nothing would ever free a held chunk (review of CK-17).
    /// </summary>
    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(10_000)]
    public void AHeldFree_IsFreedByTheReleaseWorker_AfterACoveredCycle()
    {
        var segment = _dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        var chunk = AllocateRevision();
        Free(chunk);
        Assert.That(segment.IsChunkHeld(chunk), Is.True, "premise: held");
        for (var cycle = 0; cycle < 2; cycle++)
        {
            Assert.That(_dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(5)), Is.True, "a covered cycle");
        }

        // The worker frees the bitmap bit, then the hold, then gives the bytes back: all three awaited.
        var deadline = Environment.TickCount64 + 5_000;
        while ((Allocated(chunk) || segment.IsChunkHeld(chunk) || _dbe.MMF.QuarantinedBytes != 0) && Environment.TickCount64 < deadline)
        {
            Thread.Yield();
        }

        Assert.That((segment.IsChunkHeld(chunk), Allocated(chunk)), Is.EqualTo((false, false)), "freed by the release worker");
        Assert.That(_dbe.MMF.QuarantinedBytes, Is.Zero, "its bytes given back");
    }

    /// <summary>Held frees past the budget ask the engine's checkpoint manager for a cycle — the wiring, not a test seam.</summary>
    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(10_000)]
    public void HeldFreesPastTheBudget_AskTheCheckpointManagerForACycle()
    {
        _dbe.MMF.SetFreeQuarantineBudgetForTest(0);
        var cycles = _dbe.CheckpointManager.TotalCheckpoints;
        Free(AllocateRevision());
        var deadline = Environment.TickCount64 + 5_000;
        while (_dbe.CheckpointManager.TotalCheckpoints == cycles && Environment.TickCount64 < deadline)
        {
            Thread.Yield();
        }

        Assert.That(_dbe.CheckpointManager.TotalCheckpoints, Is.GreaterThan(cycles), "a cycle, asked by the quarantine");
    }

    private unsafe int WalletRootOf(EntityId id)
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var ct = _dbe.GetComponentTable<MkWallet>();
        var meta = _dbe.GetMetaByRouting(id.ArchetypeId);
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        var slot = 0;
        for (var s = 0; s < meta.ComponentCount; s++)
        {
            if (ReferenceEquals(es.SlotToComponentTable[s], ct))
            {
                slot = s;
            }
        }

        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        var a = es.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            es.EntityMap.TryGet(id.EntityKey, record, ref a);
        }
        finally
        {
            a.Dispose();
        }

        return ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, es.ClusterState.Layout.SlotToVersionedIndex[slot]);
    }
}
