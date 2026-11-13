using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
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
        DatabaseEngine.RecoveryFlushThresholdPercentForTest = -1;
        DatabaseEngine.EntityMapBuildEntriesForTest = 0;
        DatabaseEngine.VerifyRecordRootsForTest = false;
        DatabaseEngine.ChainHeadCacheBudgetForTest = 0;
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
    [TestCase(DatabaseEngine.RecoveryPass.IndexStage)]
    [TestCase(DatabaseEngine.RecoveryPass.IndexBuild)]
    [TestCase(DatabaseEngine.RecoveryPass.IndexWriteBack)]
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
    /// #1665: the EntityMap built in bucket ranges, a walk of the clusters each, when the archetype is larger than a range — the first build stopped inside
    /// its second range, the second completing. Every committed item and value comes back, the HEADs copied by the rebuild's own walk, and the reservation
    /// took the rebuild's chain-root scan instead of scanning again.
    /// </summary>
    [Test]
    [VerifiesRule("AP-12")]
    [CancelAfter(60_000)]
    public void AnEntityMapBuiltInBucketRanges_RestoresEveryItem()
    {
        var expected = BuildAndCrash();
        DatabaseEngine.EntityMapBuildEntriesForTest = 48;

        var stopped = 0;
        DatabaseEngine.RecoveryBatchPointProbe = (p, _) =>
        {
            if (p == DatabaseEngine.RecoveryPass.EntityMap && ++stopped == 8)
            {
                throw new InvalidOperationException("crash inside the entity-map build");
            }
        };
        using (var scope = ServiceProvider.CreateScope())
        {
            Assert.Catch(() => RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>()), "premise: the build stopped half way");
        }

        DatabaseEngine.RecoveryBatchPointProbe = null;
        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the second open recovered");
        Assert.That(reopened.LastOpenEntityMapBuildPartitions, Is.GreaterThanOrEqualTo(5), "premise: the items' map was built in several bucket ranges");
        Assert.That(reopened.LastOpenVersionedHeadRebuildCount, Is.GreaterThanOrEqualTo(1), "the HEAD rebuild ran, in the map rebuild's walk");
        Assert.That(reopened.ChainHeadScansReusedAtOpen, Is.GreaterThanOrEqualTo(1), "the reservation took the map rebuild's chain-root scan");

        // The open's time line: each phase once, in order of first entry, the passes this open ran among them.
        var phases = reopened.LastOpenPhases.Select(p => p.Name).ToList();
        Assert.That(phases, Is.Unique, "a phase entered several times sums into one");
        Assert.That(phases, Is.SupersetOf(new[] { "Archetypes", "EntityMap.Walk", "EntityMap.Stage", "EntityMap.Insert", "Replay", "Scrub", "IndexRebuild" }));
        Assert.That(phases.IndexOf("EntityMap.Walk"), Is.LessThan(phases.IndexOf("Replay")), "the map rebuilt before the replay");
        Assert.That(reopened.LastOpenPhases.All(p => p.Ms >= 0 && p.PagesRead >= 0 && p.PagesWritten >= 0 && p.Entries >= 1), Is.True);
        AssertItems(reopened, expected);
        Assert.That(reopened.RunStorageIntegrityCheck().Issues, Is.Empty, "the storage integrity check");
    }

    /// <summary>
    /// #1665: a staging range too small for one bucket's entries cannot be halved further and fails the open, rather than walking the clusters forever;
    /// the next open, with room, recovers. Then a clean close and reopen: the bulk-built map's persisted counts load and serve every item.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ABucketLargerThanAStagingRange_FailsTheOpen_AndABuiltMapReopensClean()
    {
        // More checkpointed items than the map's 256 initial buckets, so some bucket holds two: a range of one entry cannot take it.
        const int Count = 600;
        var expected = new Dictionary<EntityId, MkItem?>();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Count; i++)
                {
                    var item = new MkItem { ItemNo = i, OwnerNo = 1, LastTradeSeq = 10_000 + i };
                    expected[tx.Spawn<MkItemArch>(MkItemArch.Item.Set(in item))] = item;
                }

                Assert.That(tx.Commit(), Is.True, "the build");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            dbe.SimulateHardCrash();
        }

        DatabaseEngine.EntityMapBuildEntriesForTest = 1;
        using (var scope = ServiceProvider.CreateScope())
        {
            var failure = Assert.Catch(() => RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>()), "the open refused");
            Assert.That(failure.ToString(), Does.Contain("entries a staging range holds"), "refused for the bucket, not for something else");
        }

        DatabaseEngine.EntityMapBuildEntriesForTest = 0;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var recovered = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            Assert.That(recovered.CrashRecoveryAtOpen, Is.True, "premise: the second open recovered");
            AssertItemValues(recovered, expected);
        }

        using var scope3 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope3.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.False, "premise: a clean reopen, the map loaded as the build persisted it");
        AssertItemValues(reopened, expected);
        Assert.That(reopened.RunStorageIntegrityCheck().Issues, Is.Empty, "the storage integrity check, over the loaded map");
    }

    /// <summary>
    /// #1665: the bucket ranges are cut evenly over the buckets, so entities crowded into a few buckets overflow a range; the range is halved until its
    /// entities fit, and the map still holds every item. Staged by destroying every item outside the map's lowest quarter of buckets.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ARangeWhoseEntitiesOverflowTheBuffer_IsHalved_AndTheMapHoldsEveryItem()
    {
        const int Count = 400;
        var expected = new Dictionary<EntityId, MkItem?>();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var map = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<MkItemArch>().ArchetypeId].EntityMap;
            var ids = new List<EntityId>(Count);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Count; i++)
                {
                    var item = new MkItem { ItemNo = i, OwnerNo = 1, LastTradeSeq = 30_000 + i };
                    var id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(in item));
                    ids.Add(id);
                    expected[id] = item;
                }

                Assert.That(tx.Commit(), Is.True, "the build");
            }

            // The rebuilt map has N0 buckets, as this one does: the bucket each item lands in now is the one the rebuild sorts it by.
            Assert.That(map.BucketCount, Is.EqualTo(map.N0), "premise: the map never split");
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                foreach (var id in ids.Where(id => map.BucketIndexOf(id.EntityKey) >= map.N0 / 4))
                {
                    tx.Destroy(id);
                    expected.Remove(id);
                }

                Assert.That(tx.Commit(), Is.True, "the destroys");
            }

            Assert.That(expected.Count, Is.InRange(60, 160), "premise: about a quarter of the items left, in a quarter of the buckets");
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            dbe.SimulateHardCrash();
        }

        // Ranges cut for 38 entries each over the whole bucket span: the first holds every survivor.
        DatabaseEngine.EntityMapBuildEntriesForTest = 48;
        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
        Assert.That(reopened._archetypeStates[ArchetypeRegistry.GetMetadata<MkItemArch>().ArchetypeId].EntityMap.BucketCount,
            Is.EqualTo(reopened._archetypeStates[ArchetypeRegistry.GetMetadata<MkItemArch>().ArchetypeId].EntityMap.N0), "premise: rebuilt at N0 buckets");
        Assert.That(reopened.LastOpenEntityMapRangesHalved, Is.GreaterThanOrEqualTo(1), "a range overflowed, and was halved");
        AssertItemValues(reopened, expected);
        Assert.That(reopened.RunStorageIntegrityCheck().Issues, Is.Empty, "the storage integrity check");
    }

    /// <summary>
    /// #1665: the reservation takes the EntityMap rebuild's chain-root scan only when it still describes the segment, and only as much of it as the budget
    /// holds. A chunk allocated since the scan, a chain collapsed, or roots past the budget, and the reservation scans again.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void TheReservation_ReusesTheRebuildsChainRoots_OnlyWhenTheyStillHold()
    {
        BuildAndCrash();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
        var meta = ArchetypeRegistry.GetMetadata<MkItemArch>();
        var table = dbe._archetypeStates[meta.ArchetypeId].SlotToComponentTable[meta.GetSlot(MkItemArch.Item._componentTypeId)];

        // The recovery's frees wait for a checkpoint (CK-17): taken now, so no chunk of the segment is freed between the scans below.
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var fresh = ComponentRevisionManager.EnumerateVersionedChainHeads(table, null, acceptNamedRoots: true).Values.ToHashSet();
        Assert.That(fresh, Is.Not.Empty, "premise: the items have chains");

        int[] Reserve(Action between, bool collapsed = false)
        {
            dbe.CrashChainHeadsOf(table);
            dbe.CompactCrashChainHeadsForReservation();
            between?.Invoke();
            return dbe.ChainHeadsForReservation(table, collapsed).ToArray();
        }

        try
        {
            var reused = dbe.ChainHeadScansReusedAtOpen;
            Assert.That(Reserve(null), Is.EquivalentTo(fresh), "the cached roots");
            Assert.That(dbe.ChainHeadScansReusedAtOpen, Is.EqualTo(reused + 1), "taken from the cache");

            var segment = table.CompRevTableSegment;
            // A cleared chunk reads as a root of EntityPK 0, which only a new scan can find.
            var chunk = 0;
            Assert.That(Reserve(() => chunk = segment.AllocateChunk(true)), Does.Contain(chunk), "the roots, scanned again");
            segment.FreeChunk(chunk);
            Assert.That(dbe.ChainHeadScansReusedAtOpen, Is.EqualTo(reused + 1), "a chunk allocated since the scan: scanned again");

            Assert.That(Reserve(null, collapsed: true), Is.EquivalentTo(fresh));
            Assert.That(dbe.ChainHeadScansReusedAtOpen, Is.EqualTo(reused + 1), "a chain collapsed since the scan: scanned again");

            DatabaseEngine.ChainHeadCacheBudgetForTest = (long)fresh.Count * sizeof(int) - 1;
            Assert.That(Reserve(null), Is.EquivalentTo(fresh));
            Assert.That(dbe.ChainHeadScansReusedAtOpen, Is.EqualTo(reused + 1), "roots past the budget: dropped, and scanned again");
        }
        finally
        {
            DatabaseEngine.ChainHeadCacheBudgetForTest = 0;
        }
    }

    /// <summary>
    /// #1665: before the replay, the reservation takes each re-derived archetype's live chain roots from the records its EntityMap rebuild wrote, instead of
    /// walking the clusters and looking every entity up in the map — the same roots, checked here against that walk. Past the memory budget they are
    /// dropped and the archetype is walked.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("AP-12")]
    [CancelAfter(60_000)]
    public void TheReservation_TakesTheLiveRootsTheRebuildWrote_WithinTheBudget(bool pastTheBudget)
    {
        var expected = BuildAndCrash();
        DatabaseEngine.VerifyRecordRootsForTest = true;
        if (pastTheBudget)
        {
            DatabaseEngine.ChainHeadCacheBudgetForTest = 1;
        }

        using var scope = ServiceProvider.CreateScope();
        using var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: the open recovered");
        Assert.That(dbe.RecordRootsReusedAtOpen, pastTheBudget ? Is.Zero : Is.GreaterThanOrEqualTo(1), "the archetypes whose roots the rebuild gave");
        AssertItems(dbe, expected);
        Assert.That(dbe.RunStorageIntegrityCheck().Issues, Is.Empty, "the storage integrity check");
    }

    private static void AssertItemValues(DatabaseEngine dbe, Dictionary<EntityId, MkItem?> expected)
    {
        using var read = dbe.CreateReadOnlyTransaction();
        var wrong = 0;
        foreach (var (id, item) in expected)
        {
            if (!read.IsAlive(id) || read.Open(id).Read(MkItemArch.Item).LastTradeSeq != item.Value.LastTradeSeq)
            {
                wrong++;
            }
        }

        Assert.That(wrong, Is.Zero, "items missing or with the wrong value");
        Assert.That(read.Query<MkItemArch>().Count(), Is.EqualTo(expected.Count), "the items alive, and no other");
    }

    /// <summary>
    /// A crash open restores each Versioned HEAD from its chain when the persisted cluster slot is stale — what a crash between a commit and the tick fence
    /// leaves — here staged by zeroing every item's slot and checkpointing it. The rebuild runs in the EntityMap rebuild's walk since #1665, which no
    /// test covered: a crash open whose HEAD copy was dropped passed every recovery test.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CS-03")]
    public unsafe void ACrashOpen_RestoresStaleHeadsFromTheirChains()
    {
        const int Count = 100;
        var ids = new List<EntityId>(Count);
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Count; i++)
                {
                    ids.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = i, OwnerNo = 1, LastTradeSeq = -1 - i })));
                }

                Assert.That(tx.Commit(), Is.True, "the build");
            }

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Count; i++)
                {
                    tx.OpenMut(ids[i]).Set(MkItemArch.Item, new MkItem { ItemNo = i, OwnerNo = 3, LastTradeSeq = 20_000 + i });
                }

                Assert.That(tx.Commit(), Is.True, "the update");
            }

            // Every item's slot zeroed in its cluster page and checkpointed: the data file holds stale HEADs that no logged transaction restores.
            Assert.That(ItemSlots(dbe, ids, zero: true).Count(s => s.OwnerNo == 3), Is.EqualTo(Count), "premise: each slot held its HEAD");
            Assert.That(ItemSlots(dbe, ids, zero: false).Count(s => s.OwnerNo == 0), Is.EqualTo(Count), "premise: each slot zeroed");
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint of the stale slots");
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: a crash open");
        var slots = ItemSlots(reopened, ids, zero: false);
        var stale = 0;
        for (var i = 0; i < Count; i++)
        {
            if (slots[i].ItemNo != i || slots[i].OwnerNo != 3 || slots[i].LastTradeSeq != 20_000 + i)
            {
                stale++;
            }
        }

        Assert.That(stale, Is.Zero, "items whose cluster slot holds a stale HEAD");
    }

    /// <summary>The <see cref="MkItem"/> each of <paramref name="ids"/> holds in its cluster slot, zeroed after it is read when <paramref name="zero"/>.</summary>
    private static unsafe MkItem[] ItemSlots(DatabaseEngine dbe, List<EntityId> ids, bool zero)
    {
        var meta = ArchetypeRegistry.GetMetadata<MkItemArch>();
        var state = dbe._archetypeStates[meta.ArchetypeId];
        var layout = state.ClusterState.Layout;
        var slot = meta.GetSlot(MkItemArch.Item._componentTypeId);
        var size = layout.ComponentSize(slot);
        var items = new MkItem[ids.Count];
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var changeSet = dbe.MMF.CreateChangeSet();
        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        var map = state.EntityMap.Segment.CreateChunkAccessor();
        var clusters = state.ClusterState.ClusterSegment.CreateChunkAccessor(changeSet);
        try
        {
            for (var i = 0; i < ids.Count; i++)
            {
                Assert.That(state.EntityMap.TryGet(ids[i].EntityKey, record, ref map), Is.True, $"{ids[i]}'s record");
                var clusterBase = clusters.GetChunkAddress(ClusterEntityRecordAccessor.GetClusterChunkId(record), zero);
                var value = new Span<byte>(clusterBase + layout.ComponentOffset(slot) + ClusterEntityRecordAccessor.GetSlotIndex(record) * size, size);
                items[i] = System.Runtime.InteropServices.MemoryMarshal.Read<MkItem>(value);
                if (zero)
                {
                    value.Clear();
                }
            }
        }
        finally
        {
            clusters.Dispose();
            map.Dispose();
            changeSet.SaveChanges();
        }

        return items;
    }

    /// <summary>
    /// #1306: a crash during the crash open's entity-map rebuild leaves a partial map on disk. The rebuild once snapshotted enabled bits from the persisted
    /// map, and the next open's snapshot missed the entities not yet re-inserted; it takes them from the clusters now (ENABLE-01), so the partial map is
    /// never read.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("ENABLE-01")]
    public void ACrashDuringTheEntityMapRebuild_KeepsEnabledBits()
    {
        const int Count = 64;
        var ids = new List<EntityId>(Count);
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Count; i++)
                {
                    var id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = i, OwnerNo = 1, LastTradeSeq = -1 - i }));
                    tx.OpenMut(id).Enable(MkItemArch.Lore, new MkLore());
                    ids.Add(id);
                }

                Assert.That(tx.Commit(), Is.True, "the build");
            }

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < Count; i += 2)
                {
                    tx.OpenMut(ids[i]).Disable(MkItemArch.Lore);
                }

                Assert.That(tx.Commit(), Is.True, "the disables");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            dbe.SimulateHardCrash();
        }

        var stopped = 0;
        DatabaseEngine.RecoveryBatchPointProbe = (p, _) =>
        {
            if (p == DatabaseEngine.RecoveryPass.EntityMap && ++stopped == 2)
            {
                throw new InvalidOperationException("crash inside the entity-map rebuild");
            }
        };
        using (var scope = ServiceProvider.CreateScope())
        {
            Assert.Catch(() => RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>()), "premise: the rebuild stopped half way");
        }

        DatabaseEngine.RecoveryBatchPointProbe = null;
        Assert.That(stopped, Is.GreaterThanOrEqualTo(2), "premise: the rebuild made two batch points");

        using var scope3 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope3.ServiceProvider.GetRequiredService<DatabaseEngine>());
        using var read = reopened.CreateReadOnlyTransaction();
        var wrong = 0;
        for (var i = 0; i < Count; i++)
        {
            if (read.Open(ids[i]).IsEnabled(MkItemArch.Lore) != (i % 2 != 0))
            {
                wrong++;
            }
        }

        Assert.That(wrong, Is.Zero, "items whose lore reads back with the wrong enabled state");
    }

    /// <summary>
    /// Two recoveries stopped in a row — the second replays the window over the first's partial output and leaves its own — then a third: every committed
    /// value, nothing else. The market failed this with any two stops (59 to 87 problems, items held by two live entities) and passed it with one.
    /// </summary>
    [TestCase(DatabaseEngine.RecoveryPass.Replay, DatabaseEngine.RecoveryPass.Scrub)]
    [TestCase(DatabaseEngine.RecoveryPass.Scrub, DatabaseEngine.RecoveryPass.IndexBuild)]
    [TestCase(DatabaseEngine.RecoveryPass.IndexStage, DatabaseEngine.RecoveryPass.Replay)]
    [TestCase(DatabaseEngine.RecoveryPass.IndexBuild, DatabaseEngine.RecoveryPass.IndexWriteBack)]
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
    [TestCase(DatabaseEngine.RecoveryPass.Scrub, DatabaseEngine.RecoveryPass.IndexBuild)]
    [TestCase(DatabaseEngine.RecoveryPass.IndexStage, DatabaseEngine.RecoveryPass.Replay)]
    [TestCase(DatabaseEngine.RecoveryPass.IndexBuild, DatabaseEngine.RecoveryPass.IndexWriteBack)]
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
                if (p is DatabaseEngine.RecoveryPass.IndexStage or DatabaseEngine.RecoveryPass.IndexBuild or DatabaseEngine.RecoveryPass.IndexWriteBack)
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

    /// <summary>
    /// A batch point's own flush-only cycle failing fatally fails the open at that batch point (CK-08): its failure is classified as a cycle's (CK-06),
    /// not thrown raw out of the recovery, and the check after the flush fails the open before the pass goes on.
    /// </summary>
    [Test]
    [VerifiesRule("CK-08")]
    [CancelAfter(60_000)]
    public void AFatalFlushOnlyCycleAtABatchPoint_FailsTheOpenThere()
    {
        var expected = BuildAndCrash();
        DatabaseEngine.RecoveryFlushThresholdPercentForTest = 0;
        var batchPoints = 0;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            DatabaseEngine.RecoveryBatchPointProbe = (_, _) =>
            {
                batchPoints++;
                dbe.CheckpointManager.CycleFaultInjector ??= () => throw new InvalidOperationException("simulated fatal flush-only cycle");
            };
            var e = Assert.Throws<DurabilityException>(() => RegisterMarket(dbe), "the open whose flush-only cycle failed");
            Assert.That(e.ErrorCode, Is.EqualTo(TyphonErrorCode.RecoveryCheckpointFailed));
        }

        DatabaseEngine.RecoveryBatchPointProbe = null;
        DatabaseEngine.RecoveryFlushThresholdPercentForTest = -1;
        Assert.That(batchPoints, Is.EqualTo(1), "the open failed at the batch point whose flush failed");
        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
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
