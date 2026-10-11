using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// A crash during a crash open's recovery converges (AP-12, plan 06 step 2.4). A recovery larger than the cache writes as it goes — each pass saves its
/// batches and a flush-only cycle writes what they owe (CK-08) — so a second crash leaves a data file holding part of the first recovery's output under
/// the old CheckpointLSN, and the next recovery replays the same window over it. Each test stops the first recovery inside one pass, at its second batch
/// point, and checks the second recovery against what was committed.
/// </summary>
[TestFixture]
[NonParallelizable]
internal sealed class CrashDuringRecoveryTests : TestBase<CrashDuringRecoveryTests>
{
    private const int Built = 240;
    private const int Updated = 120;
    private const int Destroyed = 40;
    private const int Spawned = 60;

    // The reopen must find the WAL it crashed with; no periodic checkpoint, the test writes what reaches the data file.
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
        o.Wal.WalDirectory = null;
    }

    [SetUp]
    public void SmallBatches() => DatabaseEngine.RecoveryBatchSize = 8;

    [TearDown]
    public void DefaultBatches()
    {
        DatabaseEngine.RecoveryBatchSize = DatabaseEngine.DefaultRecoveryBatchSize;
        DatabaseEngine.RecoveryBatchPointProbe = null;
    }

    private static DatabaseEngine RegisterMarket(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<MkItem>();
        dbe.RegisterComponentFromAccessor<MkLore>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>The recovery stopped at its second batch point of <paramref name="pass"/>, then a second recovery: every committed value, nothing else.</summary>
    [TestCase(DatabaseEngine.RecoveryPass.EntityMap)]
    [TestCase(DatabaseEngine.RecoveryPass.VersionedHeads)]
    [TestCase(DatabaseEngine.RecoveryPass.Replay)]
    [TestCase(DatabaseEngine.RecoveryPass.Scrub)]
    [TestCase(DatabaseEngine.RecoveryPass.Indexes)]
    [VerifiesRule("AP-12")]
    [CancelAfter(60_000)]
    public void ACrashDuringAPass_ConvergesOnTheNextOpen(DatabaseEngine.RecoveryPass pass)
    {
        var expected = BuildAndCrash();

        // The first recovery, stopped inside the pass as a crash would stop it.
        var stopped = 0;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            DatabaseEngine.RecoveryBatchPointProbe = (p, _) =>
            {
                if (p == pass && ++stopped == 2)
                {
                    throw new InvalidOperationException($"crash inside {pass}");
                }
            };
            Assert.Catch(() => RegisterMarket(dbe), "premise: the first recovery stopped inside the pass");
        }

        Assert.That(stopped, Is.GreaterThanOrEqualTo(2), $"premise: {pass} made two batch points");

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the second open recovered");
        AssertItems(reopened, expected);
        Assert.That(reopened.RunStorageIntegrityCheck().Issues, Is.Empty, "the storage integrity check, after the second recovery");
    }

    /// <summary>
    /// Two recoveries stopped in a row — the second replays the window over the first's partial output and leaves its own — then a third: every committed
    /// value, nothing else. The market failed this with any two stops (59 to 87 problems, items held by two live entities) and passed it with one.
    /// </summary>
    [TestCase(DatabaseEngine.RecoveryPass.Replay, DatabaseEngine.RecoveryPass.Scrub)]
    [TestCase(DatabaseEngine.RecoveryPass.Scrub, DatabaseEngine.RecoveryPass.Indexes)]
    [TestCase(DatabaseEngine.RecoveryPass.Indexes, DatabaseEngine.RecoveryPass.Replay)]
    [TestCase(DatabaseEngine.RecoveryPass.Replay, DatabaseEngine.RecoveryPass.Replay)]
    [TestCase(DatabaseEngine.RecoveryPass.EntityMap, DatabaseEngine.RecoveryPass.Scrub)]
    [VerifiesRule("AP-12")]
    [CancelAfter(60_000)]
    public void TwoCrashesDuringRecovery_ConvergeOnTheThirdOpen(DatabaseEngine.RecoveryPass first, DatabaseEngine.RecoveryPass second)
    {
        var expected = BuildAndCrash();
        foreach (var pass in new[] { first, second })
        {
            var stopped = 0;
            DatabaseEngine.RecoveryBatchPointProbe = (p, _) =>
            {
                if (p == pass && ++stopped == 2)
                {
                    throw new InvalidOperationException($"crash inside {pass}");
                }
            };
            using (var scope = ServiceProvider.CreateScope())
            {
                Assert.Catch(() => RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>()), $"premise: the recovery stopped inside {pass}");
            }

            DatabaseEngine.RecoveryBatchPointProbe = null;
        }

        using var scope3 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope3.ServiceProvider.GetRequiredService<DatabaseEngine>());
        AssertItems(reopened, expected);
        Assert.That(reopened.RunStorageIntegrityCheck().Issues, Is.Empty, "the storage integrity check, after the third recovery");
    }

    /// <summary>
    /// The market's shape, two recoveries stopped in a row, then a third. Items carry their lore (SingleVersion); the window is many small transactions —
    /// each item updated several times, and some consumed: destroyed and crafted anew in one transaction, the new entity under the same number with its
    /// lore enabled. The market recovered such items held by two live entities.
    /// </summary>
    [TestCase(DatabaseEngine.RecoveryPass.Replay, DatabaseEngine.RecoveryPass.Scrub)]
    [TestCase(DatabaseEngine.RecoveryPass.Scrub, DatabaseEngine.RecoveryPass.Indexes)]
    [TestCase(DatabaseEngine.RecoveryPass.Indexes, DatabaseEngine.RecoveryPass.Replay)]
    [TestCase(DatabaseEngine.RecoveryPass.Replay, DatabaseEngine.RecoveryPass.Replay)]
    [VerifiesRule("AP-12")]
    [CancelAfter(60_000)]
    public void TwoCrashesDuringRecovery_OfConsumedItems_ConvergeOnTheThirdOpen(DatabaseEngine.RecoveryPass first, DatabaseEngine.RecoveryPass second)
    {
        var expected = BuildConsumesAndCrash();
        foreach (var pass in new[] { first, second })
        {
            var stopped = 0;
            DatabaseEngine.RecoveryBatchPointProbe = (p, _) =>
            {
                if (p == pass && ++stopped == 2)
                {
                    throw new InvalidOperationException($"crash inside {pass}");
                }
            };
            using (var scope = ServiceProvider.CreateScope())
            {
                Assert.Catch(() => RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>()), $"premise: the recovery stopped inside {pass}");
            }

            DatabaseEngine.RecoveryBatchPointProbe = null;
        }

        using var scope3 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope3.ServiceProvider.GetRequiredService<DatabaseEngine>());
        AssertItemsByNumber(reopened, expected);
        Assert.That(reopened.RunStorageIntegrityCheck().Issues, Is.Empty, "the storage integrity check, after the third recovery");
    }

    // Built with lore and checkpointed; then many small logged transactions: updates, and consumes (destroy + spawn under the same number + its lore
    // enabled). Returns, per item number, the live entity and its committed value.
    private Dictionary<long, (EntityId Id, MkItem Item)> BuildConsumesAndCrash()
    {
        var expected = new Dictionary<long, (EntityId Id, MkItem Item)>();
        using var scope = ServiceProvider.CreateScope();
        var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Built; i++)
            {
                var item = new MkItem { ItemNo = i, OwnerNo = 1, LastTradeSeq = -1 - i };
                var id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(in item));
                tx.OpenMut(id).Enable(MkItemArch.Lore, LoreOf(i));
                expected[i] = (id, item);
            }

            Assert.That(tx.Commit(), Is.True, "the build");
        }

        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
        var seq = 10_000L;
        using (var uow = dbe.CreateUnitOfWork())
        {
            for (var round = 0; round < 3; round++)
            {
                for (var no = 0L; no < Built; no += 2)
                {
                    using var tx = uow.CreateTransaction(CommitDiscipline.Commit);
                    var (id, item) = expected[no];
                    item.OwnerNo = 2 + (no + round) % 7;
                    item.TradeCount++;
                    item.LastTradeSeq = ++seq;
                    if ((no / 2 + round) % 5 == 0)
                    {
                        tx.Destroy(id);
                        var crafted = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(in item));
                        tx.OpenMut(crafted).Enable(MkItemArch.Lore, LoreOf(no));
                        id = crafted;
                    }
                    else
                    {
                        tx.OpenMut(id).Set(MkItemArch.Item, item);
                    }

                    Assert.That(tx.Commit(), Is.True, "a logged transfer");
                    expected[no] = (id, item);
                }
            }

            uow.Flush();
        }

        dbe.SimulateHardCrash();
        return expected;
    }

    private static MkLore LoreOf(long itemNo)
    {
        var lore = new MkLore();
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<MkLore, long>(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref lore, 1));
        for (var w = 0; w < words.Length; w++)
        {
            words[w] = itemNo * 1_000 + w;
        }

        return lore;
    }

    private static void AssertItemsByNumber(DatabaseEngine dbe, Dictionary<long, (EntityId Id, MkItem Item)> expected)
    {
        using var read = dbe.CreateReadOnlyTransaction();
        foreach (var (no, (id, item)) in expected)
        {
            Assert.That(read.IsAlive(id), Is.True, $"item {no}: its entity {id} alive");
            var got = read.Open(id).Read(MkItemArch.Item);
            Assert.That((got.ItemNo, got.OwnerNo, got.LastTradeSeq), Is.EqualTo((item.ItemNo, item.OwnerNo, item.LastTradeSeq)), $"item {no}");
            Assert.That(read.Open(id).Read(MkItemArch.Lore).W07, Is.EqualTo(no * 1_000 + 7), $"item {no}: its lore");
        }

        Assert.That(read.Query<MkItemArch>().Count(), Is.EqualTo(expected.Count), "one live entity per item, and no other");
    }

    /// <summary>
    /// An open stopped during its recovery has already started its WAL writer, which left a new segment with no record in it. The next open's scan took
    /// its frontier from that last segment — 0 — so it fell back to CheckpointLSN, and the writer after it began a segment inside the window's LSNs: two
    /// such opens, and the market lost 177 to 485 durable transfers (LOG-08). The frontier is the highest LSN any segment holds.
    /// </summary>
    [Test]
    [VerifiesRule("LOG-08")]
    [CancelAfter(60_000)]
    public void AnOpenStoppedDuringItsRecovery_LeavesTheNextOpensWalFrontierAtTheWindowsEnd()
    {
        BuildAndCrash();
        long frontier;
        DatabaseEngine.RecoveryBatchPointProbe = (p, _) => throw new InvalidOperationException("crash inside the recovery");
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            frontier = dbe.LastRecoveryResult.LastValidLSN;
            Assert.Catch(() => RegisterMarket(dbe), "premise: the recovery stopped");
        }

        DatabaseEngine.RecoveryBatchPointProbe = null;
        Assert.That(frontier, Is.GreaterThan(0), "premise: the window's last LSN");
        using var scope2 = ServiceProvider.CreateScope();
        var reopened = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Assert.That(reopened.LastRecoveryResult.LastValidLSN, Is.EqualTo(frontier), "the frontier past the empty segment the stopped open left");
    }

    /// <summary>
    /// A cycle a crash open runs before its seal is flush-only (CK-08): forced from inside the replay, it writes the owed pages and leaves CheckpointLSN
    /// where it was. A full one there advanced it over a half-recovered file, and a second crash replayed nothing over it.
    /// </summary>
    [Test]
    [VerifiesRule("CK-08")]
    [CancelAfter(60_000)]
    public unsafe void ACycleDuringRecovery_IsFlushOnly_AndLeavesCheckpointLsnAlone()
    {
        var expected = BuildAndCrash();
        using var scope = ServiceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        long before, during = -1;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            before = DurabilityWatermarks.ReadCheckpointLsn(dbe.MMF);
        }

        DatabaseEngine.RecoveryBatchPointProbe = (p, _) =>
        {
            if (p == DatabaseEngine.RecoveryPass.Replay && during < 0)
            {
                dbe.CheckpointManager.RunCheckpointCycle(dbe.WalManager.DurableLsn, Typhon.Profiler.CheckpointReason.Forced);
                during = DurabilityWatermarks.ReadCheckpointLsn(dbe.MMF);
            }
        };
        RegisterMarket(dbe);
        DatabaseEngine.RecoveryBatchPointProbe = null;
        Assert.That((during, dbe.CheckpointManager.TotalFlushOnlyCycles > 0), Is.EqualTo((before, true)), "the cycle forced inside the replay: flush-only");
        AssertItems(dbe, expected);
    }

    /// <summary>
    /// The Versioned HEAD rebuild writes each cluster's HEADs through an accessor with no ChangeSet: nothing recorded the page as owing a write, and
    /// evicted — a batch point makes it evictable mid-pass — it reloaded the stale HEADs (PS-10). The pages it rebuilt are owed when the replay starts.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public unsafe void TheClustersTheHeadRebuildWrote_OweTheirWrite()
    {
        var expected = BuildAndCrash();
        EntityId any = default;
        foreach (var (id, item) in expected)
        {
            if (item != null && item.Value.LastTradeSeq < 0)
            {
                any = id;
                break;
            }
        }

        using var scope = ServiceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        bool? owed = null;
        DatabaseEngine.RecoveryBatchPointProbe = (p, _) =>
        {
            if (p == DatabaseEngine.RecoveryPass.Replay && owed == null)
            {
                // The cluster of an item the window leaves alone: only the HEAD rebuild wrote it, whatever the replay's order.
                var es = dbe._archetypeStates[dbe.GetMetaByRouting(any.ArchetypeId).ArchetypeId];
                var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
                var m = es.EntityMap.Segment.CreateChunkAccessor();
                Assert.That(es.EntityMap.TryGet(any.EntityKey, record, ref m), Is.True, "premise: the item's record");
                m.Dispose();
                var a = es.ClusterState.ClusterSegment.CreateChunkAccessor();
                try
                {
                    owed = dbe.MMF.HasWritebackDebt(dbe.MMF.MemPageIndexOf(a.GetChunkAddress(ClusterEntityRecordAccessor.GetClusterChunkId(record))));
                }
                finally
                {
                    a.Dispose();
                }
            }
        };
        RegisterMarket(dbe);
        DatabaseEngine.RecoveryBatchPointProbe = null;
        Assert.That(dbe.LastOpenVersionedHeadRebuildCount, Is.GreaterThan(0), "premise: the HEAD rebuild ran");
        Assert.That(owed, Is.True, "a cluster the HEAD rebuild wrote, owed when the replay starts");
    }

    /// <summary>
    /// A checkpointer that latches a fatal failure during a crash open fails the open (CK-08): the open succeeded unsealed, on an engine that never
    /// checkpointed again. Latched inside the replay, the open fails at the next batch point — before the index pass — and the next open recovers whole.
    /// </summary>
    [Test]
    [VerifiesRule("CK-08")]
    [CancelAfter(60_000)]
    public void AFatalCheckpointerDuringRecovery_FailsTheOpen_AndTheNextOpenRecovers()
    {
        var expected = BuildAndCrash();
        var injected = new InvalidOperationException("simulated fatal checkpoint fault");
        var indexBatchPoints = 0;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            var latched = false;
            DatabaseEngine.RecoveryBatchPointProbe = (p, _) =>
            {
                if (p == DatabaseEngine.RecoveryPass.Indexes)
                {
                    indexBatchPoints++;
                }

                if (p == DatabaseEngine.RecoveryPass.Replay && !latched)
                {
                    latched = true;
                    dbe.CheckpointManager.CycleFaultInjector = () => throw injected;
                    dbe.CheckpointManager.RunCheckpointCycle(dbe.WalManager.DurableLsn, Typhon.Profiler.CheckpointReason.Forced);
                    dbe.CheckpointManager.CycleFaultInjector = null;
                    Assert.That(dbe.CheckpointManager.HasFatalError, Is.True, "premise: the cycle latched a fatal failure");
                }
            };
            var e = Assert.Throws<DurabilityException>(() => RegisterMarket(dbe), "the open whose checkpointer failed");
            Assert.That((e.ErrorCode, e.InnerException), Is.EqualTo((TyphonErrorCode.RecoveryCheckpointFailed, (Exception)injected)));
        }

        DatabaseEngine.RecoveryBatchPointProbe = null;
        Assert.That(indexBatchPoints, Is.Zero, "the open failed at the next batch point, not at its seal");
        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the failed open closed as a crash");
        AssertItems(reopened, expected);
    }

    /// <summary>
    /// The seal's own cycle failing fatally fails the open too (CK-08): a timeout leaves the recovery to the next open; a fatal failure fails it.
    /// </summary>
    [Test]
    [VerifiesRule("CK-08")]
    [CancelAfter(60_000)]
    public void AFatalSealCycle_FailsTheOpen_AndTheNextOpenRecovers()
    {
        var expected = BuildAndCrash();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            DatabaseEngine.RecoveryBatchPointProbe = (_, _) => dbe.CheckpointManager.CycleFaultInjector ??= () =>
            {
                // The flush-only cycles pass; the seal's, the first full one, fails.
                if (!dbe.CheckpointManager.RecoveryInProgress)
                {
                    throw new InvalidOperationException("simulated fatal seal cycle");
                }
            };
            var e = Assert.Throws<DurabilityException>(() => RegisterMarket(dbe), "the open whose seal cycle failed");
            Assert.That(e.ErrorCode, Is.EqualTo(TyphonErrorCode.RecoveryCheckpointFailed));
        }

        DatabaseEngine.RecoveryBatchPointProbe = null;
        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the failed open closed as a crash");
        AssertItems(reopened, expected);
    }

    /// <summary>The same scenario recovered once, without a stop: the control that every assertion above holds of a recovery that ran to its end.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void AnUninterruptedRecovery_HoldsEveryCommittedValue()
    {
        var expected = BuildAndCrash();
        using var scope = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.RecoveryBatchPoints, Is.GreaterThan(10), "premise: the passes made batch points");
        AssertItems(reopened, expected);
        Assert.That(reopened.RunStorageIntegrityCheck().Issues, Is.Empty, "the storage integrity check");
    }

    // Items built and checkpointed; then, in the log only, updates, destroys and spawns; then a crash. Returns each item's committed value, or null for
    // one destroyed.
    private Dictionary<EntityId, MkItem?> BuildAndCrash()
    {
        var expected = new Dictionary<EntityId, MkItem?>();
        using var scope = ServiceProvider.CreateScope();
        var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Built; i++)
            {
                var item = new MkItem { ItemNo = i, OwnerNo = 1, LastTradeSeq = -1 - i };
                var id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(in item));
                ids.Add(id);
                expected[id] = item;
            }

            Assert.That(tx.Commit(), Is.True, "the build");
        }

        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
        using (var uow = dbe.CreateUnitOfWork())
        {
            using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
            {
                for (var i = 0; i < Updated; i++)
                {
                    var item = new MkItem { ItemNo = i, OwnerNo = 2 + i % 7, Price = i, TradeCount = 1, LastTradeSeq = 10_000 + i };
                    tx.OpenMut(ids[i]).Set(MkItemArch.Item, item);
                    expected[ids[i]] = item;
                }

                for (var i = Updated; i < Updated + Destroyed; i++)
                {
                    tx.Destroy(ids[i]);
                    expected[ids[i]] = null;
                }

                for (var i = 0; i < Spawned; i++)
                {
                    var item = new MkItem { ItemNo = Built + i, OwnerNo = 9, LastTradeSeq = 20_000 + i };
                    expected[tx.Spawn<MkItemArch>(MkItemArch.Item.Set(in item))] = item;
                }

                Assert.That(tx.Commit(), Is.True, "the logged window");
            }

            uow.Flush();
        }

        dbe.SimulateHardCrash();
        return expected;
    }

    private static void AssertItems(DatabaseEngine dbe, Dictionary<EntityId, MkItem?> expected)
    {
        using var read = dbe.CreateReadOnlyTransaction();
        var alive = 0;
        foreach (var (id, item) in expected)
        {
            if (item == null)
            {
                Assert.That(read.IsAlive(id), Is.False, $"{id} destroyed");
                continue;
            }

            alive++;
            Assert.That(read.IsAlive(id), Is.True, $"{id} alive");
            var got = read.Open(id).Read(MkItemArch.Item);
            Assert.That((got.ItemNo, got.OwnerNo, got.LastTradeSeq), Is.EqualTo((item.Value.ItemNo, item.Value.OwnerNo, item.Value.LastTradeSeq)), $"{id}");
        }

        Assert.That(read.Query<MkItemArch>().Count(), Is.EqualTo(alive), "the items alive, and no other");
        Assert.That(read.Query<MkItemArch>().WhereField<MkItem>(x => x.LastTradeSeq >= 10_000).Count(), Is.EqualTo(Updated + Spawned),
            "the unique index: the window's keys");
    }
}
