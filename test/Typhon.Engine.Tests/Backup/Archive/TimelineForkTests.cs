using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Q4 (impl/1418 §3.6, WA-13): writes the WAL cannot replay — a component whose layout changes, a component or archetype new to an existing database, a
/// completed bulk load — fork the timeline before they reach the data file, in one meta flip naming the parent and the origin; the lineage is kept, so
/// the next point is incremental. Archiving off, or nothing changed: no fork.
/// </summary>
[NonParallelizable]
class TimelineForkTests : ArchiveTestBase<TimelineForkTests>
{
    private const string Marker = "BKP-11: a schema write before the fork";

    private static DatabaseEngine Register(IServiceScope scope, Action<DatabaseEngine> register)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        register(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>The verifier: no data-file page past the meta pair was written before the fork's flip.</summary>
    private static void AssertNoWriteBeforeTheFork(List<(int Page, bool Forked)> writes)
    {
        Assert.That(writes.Any(w => w.Forked), Is.True, "premise: the registration wrote the data file after the fork");
        var early = writes.Where(w => !w.Forked && w.Page > 1).ToList();
        Assert.That(early, Is.Empty, $"{Marker}: page(s) {string.Join(", ", early.Select(w => w.Page))}");
    }

    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-11")]
    public void ANewComponentAndArchetype_ForkTheTimeline_BeforeTheFirstDataFileWrite_OnceForTheRegistration()
    {
        Guid genesis;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope, _ => { });
            genesis = dbe.TimelineId;
            Spawn(dbe, 5);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Assert.That(dbe.TimelineId, Is.EqualTo(genesis));
            var forked = false;
            var forks = 0;
            var writes = new List<(int, bool)>();
            dbe.MMF.DataFileWriteProbe = (first, _) => writes.Add((first, Volatile.Read(ref forked)));
            dbe.AfterTimelineForkProbe = _ =>
            {
                forks++;
                Volatile.Write(ref forked, true);
            };

            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
            Assert.That(forks, Is.Zero, "an unchanged registration forks nothing");
            dbe.RegisterComponentFromAccessor<EvoDiskWalV1>();   // new to this database: a component and an archetype
            dbe.InitializeArchetypes();
            dbe.MMF.DataFileWriteProbe = null;

            AssertNoWriteBeforeTheFork(writes);
            Assert.That(forks, Is.EqualTo(1), "the new component and its archetype fork once: nothing was appended between them");
            Assert.That(dbe.TimelineId, Is.Not.EqualTo(genesis));
            Assert.That(dbe.TimelineParentId, Is.EqualTo(genesis));
            Assert.That(dbe.TimelineOriginLsn, Is.EqualTo(dbe.WalManager.SegmentManager.ArchiveSnapshot(0, 0).ActiveFirstLsn - 1));
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Assert.That(dbe.TimelineParentId, Is.EqualTo(genesis), "the parent and the origin flipped with the timeline, durably");
            Assert.That(dbe.TimelineId, Is.Not.EqualTo(genesis));
        }
    }

    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-11")]
    public void AComponentWhoseLayoutChanges_ForksBeforeItsMigration()
    {
        Guid before;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope, d => d.RegisterComponentFromAccessor<EvoDiskWalV1>());
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.Spawn<EvoDiskWalArch>(EvoDiskWalArch.Comp.Set(new EvoDiskWalV1(7, 1.5f)));
            t.Commit();
            before = dbe.TimelineId;
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            var forked = false;
            var writes = new List<(int, bool)>();
            dbe.MMF.DataFileWriteProbe = (first, _) => writes.Add((first, Volatile.Read(ref forked)));
            dbe.AfterTimelineForkProbe = _ => Volatile.Write(ref forked, true);
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
            dbe.RegisterComponentFromAccessor<EvoDiskWalV2>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
            dbe.MMF.DataFileWriteProbe = null;
            AssertNoWriteBeforeTheFork(writes);
            Assert.That(dbe.TimelineParentId, Is.EqualTo(before));
        }
    }

    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-11")]
    public void ACompletedBulkLoad_ForksAtASegmentBoundary_BeforeItsBulkEnd()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var parent = dbe.TimelineId;
        long bulkBegin;
        using (var bulk = dbe.BeginBulkLoad())
        {
            for (var i = 0; i < 100; i++)
            {
                bulk.Spawn<CompAArch>(CompAArch.A.Set(new CompA(i)));
            }

            bulkBegin = bulk.BulkBeginLsn;
            bulk.CompleteBulkLoad();
        }

        Assert.That(dbe.TimelineParentId, Is.EqualTo(parent));
        Assert.That(dbe.TimelineId, Is.Not.EqualTo(parent));
        var origin = dbe.TimelineOriginLsn;
        Assert.That(origin, Is.GreaterThanOrEqualTo(bulkBegin), "the bulk's anchor is on the parent");
        var sealedAtFork = Sealed(dbe).Single(s => s.LastLsn == origin);
        Assert.That(sealedAtFork.LastLsn, Is.EqualTo(origin), "the parent's segments end exactly at the origin");
        Assert.That(dbe.WalManager.DurableLsn, Is.GreaterThan(origin), "BulkEnd is on the child");
    }

    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-06")]
    public void AFork_KeepsTheLineage_TheNextPointIsIncremental()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var store = new FakeBackupStore();
        Capture(dbe, store, BackupKind.Full);
        Spawn(dbe, 3);
        dbe.ForkTimeline(TimelineReason.SchemaChange, switchSegment: true);
        Spawn(dbe, 3, 10);
        var point = Capture(dbe, store);
        Assert.That(point.Kind, Is.EqualTo(BackupKind.Incremental), "BKP-06 accepts a head on the timeline the database forked from");
    }
}

/// <summary>Archiving off: the timeline is the database's own business, and nothing forks.</summary>
[NonParallelizable]
class TimelineForkOffTests : ArchiveTestBase<TimelineForkOffTests>
{
    protected override bool ArchivingOn => false;

    [Test]
    [CancelAfter(20_000)]
    public void ArchivingOff_ASchemaChangeAndABulkLoad_DoNotFork()
    {
        Guid genesis;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            genesis = dbe.TimelineId;
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
            dbe.RegisterComponentFromAccessor<EvoDiskWalV1>();
            dbe.InitializeArchetypes();
            using (var bulk = dbe.BeginBulkLoad())
            {
                bulk.Spawn<CompAArch>(CompAArch.A.Set(new CompA(1)));
                bulk.CompleteBulkLoad();
            }

            Assert.That(dbe.TimelineId, Is.EqualTo(genesis));
            Assert.That(dbe.TimelineParentId, Is.EqualTo(Guid.Empty));
        }
    }
}
