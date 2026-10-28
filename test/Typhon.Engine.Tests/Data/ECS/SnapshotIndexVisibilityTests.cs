using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// #1247: the readers that find entities through an index or a slot, at a snapshot, and the slot lifecycle under them.

[Component("Typhon.Test.SIV.Rank", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct SivRank
{
    [Index(AllowMultiple = true)] public int Tier;
    public int Score;
}

[Archetype]
partial class SivRankArch : Archetype<SivRankArch>
{
    public static readonly Comp<SivRank> Rank = Register<SivRank>();
}

[Component("Typhon.Test.SIV.SvRank", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SivSvRank
{
    [Index(AllowMultiple = true)] public int Tier;
    public int Score;
}

[Archetype]
partial class SivSvArch : Archetype<SivSvArch>
{
    public static readonly Comp<SivSvRank> Rank = Register<SivSvRank>();
}

[TestFixture]
[NonParallelizable]
class SnapshotIndexVisibilityTests : TestBase<SnapshotIndexVisibilityTests>
{
    private DatabaseEngine Open()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SivRank>();
        dbe.RegisterComponentFromAccessor<SivSvRank>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static List<EntityId> SpawnRank(DatabaseEngine dbe, int count, int tier)
    {
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids.Add(tx.Spawn<SivRankArch>(SivRankArch.Rank.Set(new SivRank { Tier = tier, Score = i })));
        }

        Assert.That(tx.Commit(), Is.True);
        return ids;
    }

    private static List<EntityId> SpawnSv(DatabaseEngine dbe, int count, int tier)
    {
        var ids = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids.Add(tx.Spawn<SivSvArch>(SivSvArch.Rank.Set(new SivSvRank { Tier = tier, Score = i })));
        }

        Assert.That(tx.Commit(), Is.True);
        return ids;
    }

    private static void Destroy(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(id);
        Assert.That(tx.Commit(), Is.True);
    }

    // ── The slot gate (item 4 and the null id) ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The cleanup removes a destroyed entity's record between the scan's occupancy read and its record probe — the reader's snapshot above the destroy, the
    /// reader the only live transaction. A missing record used to read as visible: the slot, its id cleared by the release, came back as Entity.Null.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-09")]
    public void AScanWhoseProbeFindsTheRecordGone_EmitsNothingForTheSlot()
    {
        using var dbe = Open();
        var ids = SpawnRank(dbe, 10, 5);
        var victim = ids[3];

        using var stamped = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var published = new ManualResetEventSlim();
        using var inHook = new ManualResetEventSlim();
        using var cleaned = new ManualResetEventSlim();
        long readerTsn = 0;
        List<EntityId> result = null;
        Exception readerFault = null, writerFault = null;

        var writer = new Thread(() =>
        {
            try
            {
                using var w = dbe.CreateQuickTransaction();
                w.Destroy(victim);
                dbe.CommitAfterAppendProbe = () =>
                {
                    dbe.CommitAfterAppendProbe = null;
                    stamped.Set();
                    release.Wait();
                };
                Assert.That(w.Commit(), Is.True);
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
            finally
            {
                published.Set();
            }
        });

        var reader = new Thread(() =>
        {
            try
            {
                stamped.Wait();
                using var r = dbe.CreateQuickTransaction();
                readerTsn = r.TSN;
                QueryPathProbe.Forced = ClusterScanPath.FullScan;
                QueryPathProbe.BeforeHeadEvaluateHook = () =>
                {
                    QueryPathProbe.BeforeHeadEvaluateHook = null;
                    release.Set();
                    published.Wait();
                    inHook.Set();
                    cleaned.Wait();
                };
                result = r.Query<SivRankArch>().WhereField<SivRank>(x => x.Tier == 5).Execute().ToList();
            }
            catch (Exception ex)
            {
                readerFault = ex;
            }
            finally
            {
                QueryPathProbe.BeforeHeadEvaluateHook = null;
                QueryPathProbe.Reset();
                inHook.Set();
            }
        });

        writer.Start();
        reader.Start();
        Assert.That(inHook.Wait(10_000), Is.True);
        // The destroyer's own dispose usually drains its cleanup already (the reader is above the stamp); the flush covers the case where it did not.
        dbe.FlushDeferredCleanups(readerTsn);
        Assert.That(dbe.EcsCleanupQueueSize, Is.Zero, "precondition: the cleanup ran inside the scan");
        cleaned.Set();
        reader.Join();
        writer.Join();
        Assert.That(writerFault, Is.Null, writerFault?.ToString());
        Assert.That(readerFault, Is.Null, readerFault?.ToString());
        Assert.That(result, Does.Not.Contain(default(EntityId)));
        Assert.That(result, Is.EquivalentTo(ids.Where(id => id != victim)));
    }

    /// <summary>
    /// The gate itself, past the id: an occupied slot read before the cleanup, whose record the cleanup removed before the probe. The cleanup removes a
    /// record only once the entity is dead to every snapshot at or above MinTSN, so a missing record is never visible.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-09")]
    public unsafe void TheSlotGate_ReadsAMissingRecordAsNotVisible()
    {
        using var dbe = Open();
        var victim = SpawnRank(dbe, 1, 5)[0];
        Destroy(dbe, victim);
        using var reader = dbe.CreateQuickTransaction();
        dbe.FlushDeferredCleanups(reader.TSN);
        var meta = ArchetypeRegistry.GetMetadata<SivRankArch>();
        var es = dbe._archetypeStates[meta.ArchetypeId];
        var record = stackalloc byte[meta._entityRecordSize];
        var accessor = es.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            Assert.That(es.EntityMap.TryGet(victim.EntityKey, record, ref accessor), Is.False, "precondition: the cleanup removed the record");
            Assert.That(SnapshotSlot.IsVisibleAtSnapshot((long)victim.RawValue, true, es, record, meta._entityRecordSize, reader.TSN, dbe.CommitCards,
                ref accessor), Is.False);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// A SingleVersion destroy releases its slot at publish, clearing the id: a scan that read the slot occupied before the publish reads a zero id after
    /// it. The gate never admits one, gated or not — the archetype promises no isolation, but a null id is no entity at all.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-09")]
    public unsafe void TheSlotGate_NeverAdmitsAZeroId()
    {
        using var dbe = Open();
        SpawnRank(dbe, 1, 5);
        var meta = ArchetypeRegistry.GetMetadata<SivRankArch>();
        var es = dbe._archetypeStates[meta.ArchetypeId];
        var record = stackalloc byte[meta._entityRecordSize];
        using var reader = dbe.CreateQuickTransaction();   // its epoch scope holds the accessor's
        var accessor = es.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            Assert.That(SnapshotSlot.IsVisibleAtSnapshot(0, false, null, record, meta._entityRecordSize, reader.TSN, dbe.CommitCards, ref accessor),
                Is.False, "ungated: a SingleVersion or Transient archetype");
            Assert.That(SnapshotSlot.IsVisibleAtSnapshot(0, true, es, record, meta._entityRecordSize, reader.TSN, dbe.CommitCards, ref accessor), Is.False,
                "gated");
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // ── The cleanup's slot release (N1) ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A SingleVersion destroy's cleanup, queued behind an older transaction, runs while a later spawn holds the destroyed entity's freed slot claimed and not
    /// yet occupied (its PREPARE done, its PUBLISH not). The cleanup took that slot for a hidden destroy's and released it: the spawn committed, alive, and no
    /// query found it.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-09")]
    public void ASingleVersionDestroysCleanup_NeverReleasesTheSlotASpawnHoldsSince()
    {
        using var dbe = Open();
        var a = SpawnSv(dbe, 1, 5)[0];

        using var blockerOpen = new ManualResetEventSlim();
        using var blockerGo = new ManualResetEventSlim();
        using var blockerDone = new ManualResetEventSlim();
        var blocker = new Thread(() =>
        {
            using (dbe.CreateQuickTransaction())
            {
                blockerOpen.Set();
                blockerGo.Wait();
            }

            blockerDone.Set();
        });
        blocker.Start();
        blockerOpen.Wait();

        Destroy(dbe, a);
        Assert.That(dbe.EcsCleanupQueueSize, Is.GreaterThan(0), "precondition: the destroy's cleanup waits behind the blocker");

        EntityId b;
        using (var w = dbe.CreateQuickTransaction())
        {
            b = w.Spawn<SivSvArch>(SivSvArch.Rank.Set(new SivSvRank { Tier = 5, Score = 2 }));
            dbe.CommitAfterAppendProbe = () =>
            {
                dbe.CommitAfterAppendProbe = null;
                blockerGo.Set();       // the blocker's dispose drains the cleanup inside this spawn's PREPARE → PUBLISH window
                blockerDone.Wait();
            };
            Assert.That(w.Commit(), Is.True);
        }

        blocker.Join();
        Assert.That(dbe.EcsCleanupQueueSize, Is.Zero, "precondition: the cleanup ran");
        using var check = dbe.CreateQuickTransaction();
        Assert.That(check.Query<SivSvArch>().WhereField<SivSvRank>(x => x.Tier == 5).Execute(), Is.EquivalentTo(new[] { b }));
        Assert.That(check.Query<SivSvArch>().Execute(), Is.EquivalentTo(new[] { b }));
    }

    // ── A destroy is not an update (N3) ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A destroy's tombstone moves no HEAD bytes and no index key — its slot is hidden whole, its entries' removal announced by the destroy stamp — so it
    /// must not fold the VIS-12 update watermark, which sends every older reader through a snapshot fill of the cluster.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-12")]
    public void ADestroy_LeavesTheUpdateWatermarkAlone()
    {
        using var dbe = Open();
        var ids = SpawnRank(dbe, 10, 5);
        var cs = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<SivRankArch>().ArchetypeId].ClusterState;
        var count = cs.UpdateCount;
        var stamp = cs.MaxUpdateStamp;
        using var reader = dbe.CreateQuickTransaction();

        Destroy(dbe, ids[3]);

        Assert.That(cs.MaxDestroyStamp, Is.GreaterThan(reader.TSN), "precondition: the destroy is newer than the reader");
        Assert.That(cs.UpdateCount, Is.EqualTo(count));
        Assert.That(cs.MaxUpdateStamp, Is.EqualTo(stamp));
        Assert.That(reader.Query<SivRankArch>().WhereField<SivRank>(x => x.Tier == 5).Execute(), Is.EquivalentTo(ids), "the older reader still sees it");
    }
}
