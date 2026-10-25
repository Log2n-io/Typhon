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

    /// <summary>
    /// Two schema changes at runtime with a point between them, the active segment empty throughout (the point's switch skips an empty segment): both
    /// forks compute the same origin. Folded into the first, the second would leave the point and the records written after the change on one timeline,
    /// and a restore past it would replay new-layout records onto the point's old-layout image. A point stamped since a fork makes the next one fork.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-11")]
    public void TwoSchemaChanges_WithAPointBetweenThem_ForkTwice_EvenAtTheSameOrigin()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            Register(scope, _ => { });
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope, _ => { });
            var origins = new List<long>();
            dbe.AfterTimelineForkProbe = _ => origins.Add(dbe.TimelineOriginLsn);

            dbe.RegisterComponentFromAccessor<EvoDiskWalV1>();
            Assert.That(origins, Has.Count.EqualTo(1), "premise: a component new to the database forks");
            var first = dbe.TimelineId;

            CapturePoint(dbe, BackupKind.Full);
            var points = StoreView.Open(StorePath, StoreFileSystem.Instance, null).Catalog.Packs;
            Assert.That(points.Any(p => p.TimelineId == first), Is.True, "premise: the point is on the first fork's timeline");

            dbe.RegisterComponentFromAccessor<CompB>();
            Assert.That(origins, Has.Count.EqualTo(2), $"{Marker}: the second schema change was folded into the first fork, past a point on it");
            Assert.That(origins[1], Is.EqualTo(origins[0]), "premise: both forks are at the same origin (the active segment never changed)");
            Assert.That(dbe.TimelineId, Is.Not.EqualTo(first));
            Assert.That(dbe.TimelineParentId, Is.EqualTo(first));
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

    /// <summary>
    /// The timelines are one published value (D4): a fork publishes a new one and leaves the one a reader holds as it was, so a reader's id, parent and
    /// origin are always of one fork. (A torn 16-byte id is not reproducible on demand; the shape that excludes it is what this holds.)
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public void AFork_PublishesANewTimelineValue_AndLeavesTheOneAReaderHolds()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var before = dbe.Timelines;
        var held = before.Current;
        dbe.ForkTimeline(TimelineReason.SchemaChange, switchSegment: true);
        var after = dbe.Timelines;
        Assert.That(after, Is.Not.SameAs(before), "a fork publishes a new value");
        Assert.That(before.Current, Is.EqualTo(held), "the value a reader holds never changes");
        Assert.That((after.Current.ParentId, after.Ancestors[^1]), Is.EqualTo((held.TimelineId, held)), "the new value names the old one as its parent");
        Assert.That(dbe.StampTimelineForPoint(), Is.EqualTo(after.Current));
    }

    /// <summary>
    /// WA-22 two forks deep: a segment written before both, held by the archive floor across a restart, belongs to the oldest timeline. The meta keeps
    /// the ancestors a local segment may belong to, so the restarted engine files it there, not under the parent of the timeline it reopened on.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-11")]
    public void ASegmentHeldAcrossTwoForksAndARestart_IsFiledUnderItsOwnTimeline()
    {
        Guid genesis, first, second;
        WalSealedSegment held;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope, _ => { });
            genesis = dbe.TimelineId;
            held = SpawnAndSeal(dbe, 3);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope, d => d.RegisterComponentFromAccessor<EvoDiskWalV1>());
            first = dbe.TimelineId;
            SpawnAndSeal(dbe, 3, 10);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope, d =>
            {
                d.RegisterComponentFromAccessor<EvoDiskWalV1>();
                d.RegisterComponentFromAccessor<CompB>();
            });
            second = dbe.TimelineId;
            Assert.That(new[] { genesis, first, second }.Distinct().Count(), Is.EqualTo(3), "premise: two forks");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope, d =>
            {
                d.RegisterComponentFromAccessor<EvoDiskWalV1>();
                d.RegisterComponentFromAccessor<CompB>();
            });
            Assert.That(dbe.TimelineId, Is.EqualTo(second), "premise: no schema change at this open");
            Assert.That(Sealed(dbe).Any(s => s.Id == held.Id), Is.True, "premise: the archive floor kept the first session's segment");
            Assert.That(dbe.TimelineFor(held.LastLsn).TimelineId, Is.EqualTo(genesis), "the segment belongs to the timeline it was written on");

            var archiver = NewArchiver(dbe);
            Tick(archiver, 0);
            Tick(archiver, 1);
            Assert.That(WalArchiveStore.Load(StoreFileSystem.Instance, StorePath, genesis, trustIndex: false).Segments.Any(s => s.SegmentId == held.Id),
                Is.True, "archived under its own timeline");
            var firstFile = WalArchiveStore.ReadTimelineFile(StoreFileSystem.Instance, StorePath, first);
            Assert.That(firstFile?.ParentTimelineId, Is.EqualTo(genesis), "the middle timeline's file names its parent");
        }
    }

    /// <summary>
    /// A restored database's WAL starts with the restored copy of its parent's history, never archived again (its restore's origin). A fork and a reopen
    /// later, the timeline it reopens on is the fork's: the restore is one of its ancestors, still known, and so is where the restored history ends.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public void ARestoredDatabase_ForkedThenReopened_StillKnowsWhereItsRestoredHistoryEnds()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope, _ => { });
            Spawn(dbe, 3);
            CapturePoint(dbe, BackupKind.Full);
        }

        var dir = System.IO.Path.Combine(TestStorage.Root, nameof(TimelineForkTests), CurrentDatabaseName + ".restored");
        if (System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.Delete(dir, true);
        }

        BackupOperations.Restore(StorePath, PointSelector.Latest, dir);
        using var provider = ProviderFor(dir);
        long origin;
        using (var scope = provider.CreateScope())
        {
            origin = Register(scope, _ => { }).RestoreOriginLsn;
            Assert.That(origin, Is.GreaterThan(0), "premise: the restored WAL holds the parent's history up to the point");
        }

        using (var scope = provider.CreateScope())
        {
            var dbe = Register(scope, d => d.RegisterComponentFromAccessor<EvoDiskWalV1>());
            Assert.That(dbe.TimelineFor(long.MaxValue).Reason, Is.EqualTo(TimelineReason.SchemaChange), "premise: the restored database forked");
        }

        using (var scope = provider.CreateScope())
        {
            var dbe = Register(scope, d => d.RegisterComponentFromAccessor<EvoDiskWalV1>());
            Assert.That(dbe.RestoreOriginLsn, Is.EqualTo(origin), "the restore is an ancestor of the timeline it reopened on");
        }
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
