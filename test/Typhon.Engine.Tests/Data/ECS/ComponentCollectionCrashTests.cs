using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1283: the component-collection segments are found at open through the bootstrap's stride → root-page map. A collection whose segment was created
/// in the session that crashed must still be found by the reopen.
/// </summary>
[TestFixture]
class ComponentCollectionCrashTests : TestBase<ComponentCollectionCrashTests>
{
    // The reopen must find the WAL it crashed with, or it is not a crash recovery.
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    // No periodic checkpoint: the test's own checkpoint is the only one. The log goes under the bundle, which the teardown deletes.
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
        o.Wal.WalDirectory = null;
    }

    /// <summary>
    /// A collection written and checkpointed — durable in the data file, out of the log — in the session that first used its element type, then a hard
    /// crash: the reopen reads it back.
    /// </summary>
    [Test]
    [VerifiesRule("CK-10")]
    [CancelAfter(30_000)]
    public void ACollectionWhoseSegmentTheCrashedSessionCreated_SurvivesTheCrash()
    {
        EntityId id;
        int segmentRoot;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoCcBagV1>();
            dbe.InitializeArchetypes();

            using (var t = dbe.CreateQuickTransaction())
            {
                var bag = new EvoCcBagV1(42);
                using (var cca = t.CreateComponentCollectionAccessor(ref bag.Items))
                {
                    for (var i = 0; i < 10; i++)
                    {
                        cca.Add(100 + i);
                    }
                }

                id = t.Spawn<EvoCcUnit>(EvoCcUnit.Bag.Set(in bag));
                Assert.That(t.Commit(), Is.True, "the spawn");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            segmentRoot = dbe.GetComponentCollectionVSBS<int>().Segment.RootPageIndex;
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        reopened.RegisterComponentFromAccessor<EvoCcBagV1>();
        reopened.InitializeArchetypes();
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        Assert.That(reopened.GetComponentCollectionVSBS<int>().Segment.RootPageIndex, Is.EqualTo(segmentRoot), "the reopen found the collection's segment");

        using var read = reopened.CreateQuickTransaction();
        var readBag = read.Open(id).Read(EvoCcUnit.Bag);
        Assert.That(readBag.A, Is.EqualTo(42), "premise: the component survived");
        using var accessor = read.CreateComponentCollectionAccessor(ref readBag.Items);
        var items = new int[accessor.ElementCount];
        accessor.GetAllElements(items);
        Assert.That(items, Is.EqualTo(new[] { 100, 101, 102, 103, 104, 105, 106, 107, 108, 109 }), "the collection, after the crash");
    }

    /// <summary>
    /// The crash-open scrub of a chain whose revisions hold a collection never added to releases no buffer for them (#1291): it released buffer id 0,
    /// decrementing chunk 0 — the reserved sentinel — as a reference count, under chunk 0's exclusive lock.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void TheScrubOfRevisionsWithAnEmptyCollection_ReleasesNoBuffer()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompE_Eng>();
            RegisterComponents(dbe);
            dbe.InitializeArchetypes();

            using (var t = dbe.CreateQuickTransaction())
            {
                var a = new CompA(2);
                var e = new CompE_Eng(1);
                id = t.Spawn<CompAEArch>(CompAEArch.A.Set(in a), CompAEArch.E.Set(in e));
                Assert.That(t.Commit(), Is.True, "the spawn");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");

            // Updates durable in the log only: the recovery replays them into the chain, and the scrub collapses it to its head.
            using (var uow = dbe.CreateUnitOfWork())
            {
                for (var i = 0; i < 2; i++)
                {
                    using var tx = uow.CreateTransaction(CommitDiscipline.Commit);
                    var entity = tx.OpenMut(id);
                    var e = entity.Read(CompAEArch.E);
                    e.A = 10 + i;
                    entity.Set(CompAEArch.E, e);
                    Assert.That(tx.Commit(), Is.True, "the update");
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        reopened.RegisterComponentFromAccessor<CompE_Eng>();
        RegisterComponents(reopened);
        reopened.InitializeArchetypes();
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using (var t = reopened.CreateQuickTransaction())
        {
            Assert.That(t.Open(id).Read(CompAEArch.E).A, Is.EqualTo(11), "premise: the updates were replayed");
        }

        using var guard = EpochGuard.Enter(reopened.EpochManager);
        var accessor = reopened.GetComponentCollectionVSBS<int>().Segment.CreateChunkAccessor();
        var refCounter = accessor.GetChunk<VariableSizedBufferRootHeader>(0).RefCounter;
        accessor.Dispose();
        Assert.That(refCounter, Is.Zero, "chunk 0, as a reference count");
    }

    /// <summary>
    /// Reads of a collection never added to, then a crash: the open completes. Each read left a shared hold on chunk 0, which reached the disk with the
    /// next write of its page; the crash-open scrub then released buffer id 0, and waited for chunk 0's exclusive lock on a hold no thread would give
    /// back — the open threw a lock timeout (#1291).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ReadsOfAnEmptyCollectionBeforeACrash_LeaveTheOpenAlone()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompE_Eng>();
            RegisterComponents(dbe);
            dbe.InitializeArchetypes();

            using (var t = dbe.CreateQuickTransaction())
            {
                var a = new CompA(2);
                var e = new CompE_Eng(1);
                id = t.Spawn<CompAEArch>(CompAEArch.A.Set(in a), CompAEArch.E.Set(in e));
                Assert.That(t.Commit(), Is.True, "the spawn");
            }

            using (var t = dbe.CreateReadOnlyTransaction())
            {
                var e = t.Open(id).Read(CompAEArch.E);
                using var cca = t.CreateComponentCollectionAccessor(ref e.Collection);
                Assert.That(cca.ElementCount, Is.Zero, "the read");
            }

            // A collection filled on chunk 0's page, and checkpointed: the page reaches the disk with whatever chunk 0's lock word holds.
            using (var t = dbe.CreateQuickTransaction())
            {
                var a = new CompA(3);
                var e = new CompE_Eng(2);
                using (var cca = t.CreateComponentCollectionAccessor(ref e.Collection))
                {
                    cca.Add(7);
                }

                t.Spawn<CompAEArch>(CompAEArch.A.Set(in a), CompAEArch.E.Set(in e));
                Assert.That(t.Commit(), Is.True, "the filled spawn");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    var entity = tx.OpenMut(id);
                    var e = entity.Read(CompAEArch.E);
                    e.A = 10;
                    entity.Set(CompAEArch.E, e);
                    Assert.That(tx.Commit(), Is.True, "the update");
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        reopened.RegisterComponentFromAccessor<CompE_Eng>();
        RegisterComponents(reopened);
        reopened.InitializeArchetypes();
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using var read = reopened.CreateQuickTransaction();
        Assert.That(read.Open(id).Read(CompAEArch.E).A, Is.EqualTo(10), "the update, replayed");
    }
}
