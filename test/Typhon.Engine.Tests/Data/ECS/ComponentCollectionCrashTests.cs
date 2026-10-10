using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

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
}
