using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>SingleVersion and 112 bytes: 64 to a cluster, a cluster to a page, so every 64 entities is a page of their own.</summary>
[Component("Typhon.Test.HandleLife.Fat", 1, StorageMode = StorageMode.SingleVersion)]
unsafe struct HlFat
{
    [Index] public int Key;
    public int Check;
    public fixed byte Payload[104];
}

[Archetype]
class HlFatArch : Archetype<HlFatArch>
{
    public static readonly Comp<HlFat> Fat = Register<HlFat>();
}

[Archetype]
class HlFatArchB : Archetype<HlFatArchB>
{
    public static readonly Comp<HlFat> Fat = Register<HlFat>();
}

/// <summary>Transient: lives in pinned native blocks, never evicted. Makes <see cref="HlFatArchC"/> a mixed archetype.</summary>
[Component("Typhon.Test.HandleLife.Trans", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct HlTrans
{
    public int Value;
    public int Padding;
}

/// <summary>Mixed: its SingleVersion slot sits in the page cache, its Transient one in the transient store.</summary>
[Archetype]
class HlFatArchC : Archetype<HlFatArchC>
{
    public static readonly Comp<HlFat> Fat = Register<HlFat>();
    public static readonly Comp<HlTrans> Trans = Register<HlTrans>();
}

/// <summary>Versioned: <c>EnumerateIndex</c> needs a revision table (#1200).</summary>
[Component("Typhon.Test.HandleLife.Small", 1)]
[StructLayout(LayoutKind.Sequential)]
struct HlSmall
{
    [Index] public int Tag;
    public int Padding;
}

[Archetype]
class HlSmallArch : Archetype<HlSmallArch>
{
    public static readonly Comp<HlSmall> Small = Register<HlSmall>();
}

[Archetype]
class HlSmallArchB : Archetype<HlSmallArchB>
{
    public static readonly Comp<HlSmall> Small = Register<HlSmall>();
}

/// <summary>
/// #1199: an entity handle stays valid for its whole transaction, whatever the transaction does in between. Its transaction moves its epoch forward
/// mid-way — an index enumeration every 128 entities, a spawn run every 128 spawns — and the accessor that resolved the handle's cluster lets go of the
/// page when it gives the archetype's cache entry to another archetype or rotates its slots. The handle cached a raw pointer into that page; it used to keep reading through it after the
/// cache gave the slot to another page: reads returned another entity's values and a write landed in another entity.
/// </summary>
/// <remarks>
/// <para>Each test opens entity X, does what ordinary code does, then has another thread read more pages than the cache holds until the cache slot under
/// the handle's original pointer holds another page — the precondition is staged, not hoped for. Then it reads or writes through the handle.</para>
/// <para>The data is spread over three archetypes because no one archetype can outgrow this cache: a cluster segment grows by doubling, and growing to
/// the cache's size asks for more new pages than the cache holds (#1191).</para>
/// </remarks>
[TestFixture]
class EntityHandleLifetimeTests : TestBase<EntityHandleLifetimeTests>
{
    private const int CacheBytes = 4 * 1024 * 1024;
    private const int CachePages = CacheBytes / 8192;
    private const int FatPerArchetype = 15_000;
    private const int SmallEntities = 1_000;
    private const int X = 12_345;
    private const int Marker = 0x5EED_0001;
    private const int MaxPressurePasses = 8;

    private EntityId[] _fat;
    private EntityId[] _fatB;
    private EntityId[] _fatC;
    private EntityId[] _small;
    private EntityId _smallB;

    [TearDown]
    public void ResetProbes() => QueryPathProbe.Forced = ClusterScanPath.Planner;

    private static int Check(int key) => key * 7 + 3;

    private static EntityId[] SpawnFat<TArch>(DatabaseEngine dbe, Comp<HlFat> comp, int keyBase, bool withTrans = false) where TArch : Archetype<TArch>
    {
        var ids = new EntityId[FatPerArchetype];
        for (var b = 0; b < FatPerArchetype; b += 200)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = b; i < b + 200; i++)
            {
                var fat = new HlFat { Key = keyBase + i, Check = Check(keyBase + i) };
                var trans = new HlTrans { Value = i };
                ids[i] = withTrans ? tx.Spawn<TArch>(comp.Set(in fat), HlFatArchC.Trans.Set(in trans)) : tx.Spawn<TArch>(comp.Set(in fat));
            }

            tx.Commit();
        }

        return ids;
    }

    private static int ClusterPages<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch> =>
        dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState.ClusterSegment.Length;

    private DatabaseEngine Build()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<HlFat>();
        dbe.RegisterComponentFromAccessor<HlSmall>();
        dbe.RegisterComponentFromAccessor<HlTrans>();
        dbe.InitializeArchetypes();

        _fat = SpawnFat<HlFatArch>(dbe, HlFatArch.Fat, 0);
        _fatB = SpawnFat<HlFatArchB>(dbe, HlFatArchB.Fat, 100_000);
        _fatC = SpawnFat<HlFatArchC>(dbe, HlFatArchC.Fat, 200_000, withTrans: true);

        _small = new EntityId[SmallEntities];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < SmallEntities; i++)
            {
                var small = new HlSmall { Tag = i };
                _small[i] = tx.Spawn<HlSmallArch>(HlSmallArch.Small.Set(in small));
            }

            var other = new HlSmall { Tag = 2_000_000 };
            _smallB = tx.Spawn<HlSmallArchB>(HlSmallArchB.Small.Set(in other));
            tx.Commit();
        }

        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(20)), Is.True, "premise: the build is on disk, so its pages are evictable");
        var fatPages = ClusterPages<HlFatArch>(dbe) + ClusterPages<HlFatArchB>(dbe) + ClusterPages<HlFatArchC>(dbe);
        Assert.That(fatPages, Is.GreaterThan(CachePages), "premise: the fat clusters outnumber the cache's pages");
        return dbe;
    }

    /// <summary>
    /// Another thread evicts every page the cache may evict, then reads every fat cluster in a read-only transaction — more pages than the cache holds —
    /// until the cache slot that held <paramref name="filePage"/> holds another page.
    /// </summary>
    /// <remarks>
    /// Evicted first, not left to the clock: the scans read the handle's own page too, and a page read on every pass can keep its slot through all of
    /// them (a handle of <see cref="HlFatArchC"/> did, 1 run in 4). Checked after each archetype's scan, and only another page counts — the slot left
    /// free by the eviction would still hold the handle's bytes.
    /// </remarks>
    private static void EvictUnder(DatabaseEngine dbe, int memPage, int filePage)
    {
        var evicted = Task.Run(() =>
        {
            // A full scan, so every pass reads every cluster page. The probe is per thread: set here, on the thread that scans.
            QueryPathProbe.Forced = ClusterScanPath.FullScan;
            try
            {
                return Pressure();
            }
            finally
            {
                QueryPathProbe.Forced = ClusterScanPath.Planner;
            }
        }).GetAwaiter().GetResult();
        Assert.That(evicted, Is.True, $"premise: the slot under the handle's original pointer still holds file page {filePage} after {MaxPressurePasses} passes");

        bool Pressure()
        {
            for (var pass = 0; pass < MaxPressurePasses; pass++)
            {
                dbe.MMF.EvictEvictablePagesForTest();
                using var rtx = dbe.CreateReadOnlyTransaction();
                Assert.That(rtx.Query<HlFatArch>().WhereField<HlFat>(f => f.Key >= 0).Count(), Is.EqualTo(FatPerArchetype), "pressure scan A");
                if (HoldsAnotherPage())
                {
                    return true;
                }

                Assert.That(rtx.Query<HlFatArchB>().WhereField<HlFat>(f => f.Key >= 0).Count(), Is.EqualTo(FatPerArchetype), "pressure scan B");
                if (HoldsAnotherPage())
                {
                    return true;
                }

                Assert.That(rtx.Query<HlFatArchC>().WhereField<HlFat>(f => f.Key >= 0).Count(), Is.EqualTo(FatPerArchetype), "pressure scan C");
                if (HoldsAnotherPage())
                {
                    return true;
                }
            }

            return false;
        }

        bool HoldsAnotherPage()
        {
            var held = dbe.MMF.GetFilePageIndex(memPage);
            return held != filePage && held != -1;
        }
    }

    /// <summary>The cache slot and file page under a cluster-stored handle's cached pointer.</summary>
    private static unsafe (int MemPage, int FilePage) PageUnder(DatabaseEngine dbe, in EntityRef handle)
    {
        var memPage = dbe.MMF.MemPageIndexOf(handle._clusterBase);
        return (memPage, dbe.MMF.GetFilePageIndex(memPage));
    }

    /// <summary>
    /// Open an entity of as many other archetypes than <see cref="HlFatArch"/> as the cluster cache has ways: least recently used replaced, it gives
    /// <see cref="HlFatArch"/>'s entry to the last of them, and lets go of every page the entry held.
    /// </summary>
    private void PushHlFatArchOutOfTheClusterCache(EntityAccessor tx) => OpenOneOfEach(tx, _fatB[0], _fatC[0], _small[0], _smallB);

    /// <summary>The same for <see cref="HlFatArchC"/>.</summary>
    private void PushHlFatArchCOutOfTheClusterCache(EntityAccessor tx) => OpenOneOfEach(tx, _fat[0], _fatB[0], _small[0], _smallB);

    private static void OpenOneOfEach(EntityAccessor tx, params EntityId[] ofOtherArchetypes)
    {
        Assert.That(ofOtherArchetypes.Length, Is.GreaterThanOrEqualTo(EntityAccessor.ClusterCacheWays),
            "premise: one entity of each of as many other archetypes as the cluster cache has ways");
        for (var i = 0; i < EntityAccessor.ClusterCacheWays; i++)
        {
            _ = tx.Open(ofOtherArchetypes[i]);
        }
    }

    private static int Enumerate(DatabaseEngine dbe, Transaction tx, int count)
    {
        var indexRef = dbe.GetIndexRef<HlSmall, int>(s => s.Tag);
        var n = 0;
        using var e = tx.EnumerateIndex<HlSmall, int>(indexRef, 0, count - 1);
        while (e.MoveNext())
        {
            n++;
        }

        return n;
    }

    private static void SpawnSmall(Transaction tx, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var small = new HlSmall { Tag = 1_000_000 + i };
            tx.Spawn<HlSmallArch>(HlSmallArch.Small.Set(in small));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void ReadOnly_HandleReadsItsEntity_AfterAnEnumerationRefreshAndAnArchetypeSwitch()
    {
        var dbe = Build();
        using var tx = dbe.CreateReadOnlyTransaction();
        var a = tx.Open(_fat[X]);
        var (memPage, filePage) = PageUnder(dbe, a);

        PushHlFatArchOutOfTheClusterCache(tx);                   // the cluster cache lets go of X's page
        Assert.That(Enumerate(dbe, tx, 200), Is.EqualTo(200));   // past 128 entities, the enumeration moves the thread's epoch
        EvictUnder(dbe, memPage, filePage);

        var value = a.Read(HlFatArch.Fat);
        Assert.That(value.Key, Is.EqualTo(X), "the handle reads another entity");
        Assert.That(value.Check, Is.EqualTo(Check(X)));
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void ReadOnly_HandleReadsItsEntity_AfterReadingAHundredOtherClustersOfItsArchetype()
    {
        var dbe = Build();
        using var tx = dbe.CreateReadOnlyTransaction();
        var a = tx.Open(_fat[X]);
        var (memPage, filePage) = PageUnder(dbe, a);

        for (var k = 1; k <= 100; k++)
        {
            // each in another cluster: the cluster cache (32 slots, clock-evicted) goes round three times and lets go of X's page
            Assert.That(tx.Open(_fat[(X + k * 64) % FatPerArchetype]).Read(HlFatArch.Fat).Key, Is.EqualTo((X + k * 64) % FatPerArchetype));
        }

        Assert.That(Enumerate(dbe, tx, 200), Is.EqualTo(200));
        EvictUnder(dbe, memPage, filePage);

        Assert.That(a.Read(HlFatArch.Fat).Key, Is.EqualTo(X), "the handle reads another entity");
        Assert.That(a.TryRead(HlFatArch.Fat, out var viaTry) && viaTry.Key == X, Is.True, "TryRead reads another entity");
        Span<byte> raw = stackalloc byte[a.GetComponentSize(0)];
        Assert.That(a.ReadRaw(0, raw), Is.EqualTo(raw.Length));
        Assert.That(MemoryMarshal.Read<int>(raw), Is.EqualTo(X), "ReadRaw reads another entity");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void WriteTransaction_HandleReadsItsEntity_AfterASpawnRefreshAndAnArchetypeSwitch()
    {
        var dbe = Build();
        using var tx = dbe.CreateQuickTransaction();
        var a = tx.Open(_fat[X]);
        var (memPage, filePage) = PageUnder(dbe, a);

        PushHlFatArchOutOfTheClusterCache(tx);
        SpawnSmall(tx, 130);                                     // past 128 spawns, the transaction moves its epoch
        EvictUnder(dbe, memPage, filePage);

        Assert.That(a.Read(HlFatArch.Fat).Key, Is.EqualTo(X), "the handle reads another entity");
    }

    /// <summary>The write case: before #1199 the value landed in whatever page held the slot, and X kept its old value.</summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void WriteTransaction_SetThroughAHandle_LandsInItsEntity_AfterASpawnRefreshAndAnArchetypeSwitch()
    {
        var dbe = Build();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var m = tx.OpenMut(_fat[X]);
            var (memPage, filePage) = PageUnder(dbe, m);

            PushHlFatArchOutOfTheClusterCache(tx);
            SpawnSmall(tx, 130);
            EvictUnder(dbe, memPage, filePage);

            // No read first: Set alone has to find the page again.
            m.Set(HlFatArch.Fat, new HlFat { Key = Marker, Check = Check(X) });
            tx.Commit();
        }

        AssertOnlyXHoldsTheMarker(dbe);
    }

    private void AssertOnlyXHoldsTheMarker(DatabaseEngine dbe)
    {
        int xKey = 0, markers = 0, wrong = 0;
        Tally(dbe, _fat, HlFatArch.Fat, 0, ref xKey, ref markers, ref wrong);
        Tally(dbe, _fatB, HlFatArchB.Fat, 100_000, ref xKey, ref markers, ref wrong);
        Tally(dbe, _fatC, HlFatArchC.Fat, 200_000, ref xKey, ref markers, ref wrong);
        Assert.Multiple(() =>
        {
            Assert.That(xKey, Is.EqualTo(Marker), "the write through the handle did not reach its entity");
            Assert.That(markers, Is.Zero, "the write through the handle landed in another entity");
            Assert.That(wrong, Is.Zero, "other entities read wrong keys");
        });
    }

    /// <summary>
    /// A handle from an <see cref="ArchetypeAccessor{TArch}"/> holds its page through that accessor's slot. Disposing the accessor lets go of it; the handle
    /// then re-resolves through its transaction's cluster cache.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void ArchetypeAccessorHandle_ReadsAndSetsItsEntity_AfterItsAccessorIsDisposed()
    {
        var dbe = Build();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<HlFatArch>();
            var m = accessor.OpenMut(_fat[X]);
            var (memPage, filePage) = PageUnder(dbe, m);

            accessor.Dispose();
            SpawnSmall(tx, 130);
            EvictUnder(dbe, memPage, filePage);

            Assert.That(m.Read(HlFatArch.Fat).Key, Is.EqualTo(X), "the handle reads another entity");
            m.Set(HlFatArch.Fat, new HlFat { Key = Marker, Check = Check(X) });
            tx.Commit();
        }

        AssertOnlyXHoldsTheMarker(dbe);
    }

    /// <summary>The same handle, its accessor still alive: it lets go of the page after a hundred other clusters went through its slots.</summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void ArchetypeAccessorHandle_ReadsItsEntity_AfterItsAccessorReadAHundredOtherClusters()
    {
        var dbe = Build();
        using var tx = dbe.CreateReadOnlyTransaction();
        using var accessor = tx.For<HlFatArch>();
        var a = accessor.Open(_fat[X]);
        var (memPage, filePage) = PageUnder(dbe, a);

        for (var k = 1; k <= 100; k++)
        {
            Assert.That(accessor.Open(_fat[(X + k * 64) % FatPerArchetype]).Read(HlFatArch.Fat).Key, Is.EqualTo((X + k * 64) % FatPerArchetype));
        }

        Assert.That(Enumerate(dbe, tx, 200), Is.EqualTo(200));
        EvictUnder(dbe, memPage, filePage);

        Assert.That(a.Read(HlFatArch.Fat).Key, Is.EqualTo(X), "the handle reads another entity");
    }

    /// <summary>
    /// A Set on the Transient component of a mixed archetype writes into the transient store, whose pages never move. Being the entity's first write
    /// this tick, it also captures the SingleVersion index key for the fence — from the page-cache cluster, through the handle's other base. That base
    /// has to be re-resolved there too: the key captured is the one the fence moves the index entry away from.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void TransientSetThroughAHandle_CapturesItsEntitysIndexKey_AfterASpawnRefreshAndAnArchetypeSwitch()
    {
        var dbe = Build();
        var clusterState = dbe._archetypeStates[Archetype<HlFatArchC>.Metadata.ArchetypeId].ClusterState;
        var shadow = clusterState.IndexSlots[0].ShadowBuffers[0];
        using var tx = dbe.CreateQuickTransaction();
        var m = tx.OpenMut(_fatC[X]);
        var (memPage, filePage) = PageUnder(dbe, m);

        PushHlFatArchCOutOfTheClusterCache(tx);
        SpawnSmall(tx, 130);
        EvictUnder(dbe, memPage, filePage);

        var before = shadow.Count;
        m.Set(HlFatArchC.Trans, new HlTrans { Value = 7 });
        Assert.That(shadow.Count, Is.EqualTo(before + 1), "premise: the Set is the entity's first write this tick, so it captures its index key");
        Assert.That(shadow[before].EntityPK, Is.EqualTo(_fatC[X]));
        Assert.That(shadow[before].OldKey.RawValue, Is.EqualTo(200_000 + X), "the capture read another entity's key");
        Assert.That(m.Read(HlFatArchC.Trans).Value, Is.EqualTo(7));
    }

    /// <summary>
    /// Under Commit discipline a write is staged, seeded from the entity's current value; only the cluster location is kept for the publish at commit.
    /// A partial write through a handle whose page has been let go of must seed from its entity.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void CommitDiscipline_PartialWriteThroughAHandle_SeedsFromItsEntity_AfterASpawnRefreshAndAnArchetypeSwitch()
    {
        var dbe = Build();
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
        {
            var m = tx.OpenMut(_fat[X]);
            var (memPage, filePage) = PageUnder(dbe, m);

            PushHlFatArchOutOfTheClusterCache(tx);
            SpawnSmall(tx, 130);
            EvictUnder(dbe, memPage, filePage);

            m.WriteRef(HlFatArch.Fat).Check = Marker;   // only Check: Key comes from the seed
            tx.Commit();
        }

        using var rtx = dbe.CreateReadOnlyTransaction();
        var x = rtx.Open(_fat[X]).Read(HlFatArch.Fat);
        Assert.That(x.Check, Is.EqualTo(Marker), "the write did not reach its entity");
        Assert.That(x.Key, Is.EqualTo(X), "the staged copy was seeded from another entity");
    }

    /// <summary>
    /// A PTA worker's handle: the worker accessor resolves through its own cluster cache, and its epoch moves when the runtime flushes it at the end of a
    /// system.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void PointInTimeWorkerHandle_ReadsItsEntity_AfterAWorkerFlushAndAnArchetypeSwitch()
    {
        var dbe = Build();
        using var pta = PointInTimeAccessor.Create(dbe);
        var worker = pta.GetWorkerAccessor(0);
        var a = worker.Open(_fat[X]);
        var (memPage, filePage) = PageUnder(dbe, a);

        PushHlFatArchOutOfTheClusterCache(worker);
        pta.FlushWorker(0);
        EvictUnder(dbe, memPage, filePage);

        Assert.That(a.Read(HlFatArch.Fat).Key, Is.EqualTo(X), "the handle reads another entity");
    }

    /// <summary>
    /// The cluster cache replaces its least recently used entry, not its oldest: an archetype used again since its entry was made keeps it.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", CacheBytes)]
    public void ClusterCache_KeepsARecentlyUsedArchetype_WhenAnotherNeedsAnEntry()
    {
        Assume.That(EntityAccessor.ClusterCacheWays, Is.EqualTo(4), "staged for four ways");
        var dbe = Build();
        using var tx = dbe.CreateReadOnlyTransaction();
        var a = tx.Open(_fat[X]);                // HlFatArch: the oldest entry
        _ = tx.Open(_fatB[0]);
        _ = tx.Open(_fatC[0]);
        _ = tx.Open(_small[0]);                  // four entries, the cache is full
        _ = tx.Open(_fat[0]);                    // HlFatArch used again: no longer the least recently used
        _ = tx.Open(_smallB);                    // a fifth archetype: replaces HlFatArchB's entry, not HlFatArch's
        var generation = tx.ClusterGeneration.Value;

        // HlFatArch's entry still holds X's page: the handle resolves its base again from it, without creating an entry — which would replace
        // another and move the generation.
        Assert.That(a.Read(HlFatArch.Fat).Key, Is.EqualTo(X));
        Assert.That(tx.ClusterGeneration.Value, Is.EqualTo(generation), "HlFatArch lost its entry although it was used after HlFatArchB");
    }

    /// <summary>
    /// A PTA worker refreshes its epoch once per system (<see cref="PointInTimeAccessor.FlushWorker"/>), not every 128 opens like a transaction: each
    /// refresh moves the global epoch and sends every other worker's warm B+Tree accessors down their cold path.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void PointInTimeWorker_DoesNotRefreshItsEpochOnOpens_ATransactionDoes()
    {
        var dbe = Build();
        using (var tx = dbe.CreateReadOnlyTransaction())
        {
            var before = dbe.EpochManager.CurrentThreadPinnedEpoch;
            for (var i = 0; i < 300; i++)
            {
                _ = tx.Open(_fat[i]);
            }
            Assert.That(dbe.EpochManager.CurrentThreadPinnedEpoch, Is.Not.EqualTo(before), "premise: a transaction's opens refresh its epoch");
        }

        using var pta = PointInTimeAccessor.Create(dbe);
        var worker = pta.GetWorkerAccessor(0);
        var pinned = dbe.EpochManager.CurrentThreadPinnedEpoch;
        for (var i = 0; i < 300; i++)
        {
            _ = worker.Open(_fat[i]);
        }
        Assert.That(dbe.EpochManager.CurrentThreadPinnedEpoch, Is.EqualTo(pinned), "a worker's opens refreshed its epoch");
    }

    /// <summary>
    /// A worker's cluster cache is released when the runtime flushes the worker at the end of a system. Kept across systems and ticks, a slot a
    /// writable open mapped dirty would hold its page's ActiveChunkWriters above zero, and the checkpoint could not capture that page.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void PointInTimeWorker_Flush_ReleasesItsClusterCache()
    {
        var dbe = Build();
        using var pta = PointInTimeAccessor.Create(dbe);
        var worker = pta.GetWorkerAccessor(0);
        var m = worker.OpenMut(_fat[X]);
        var (memPage, _) = PageUnder(dbe, m);
        Assert.That(dbe.MMF.ActiveChunkWritersOf(memPage), Is.GreaterThan(0), "premise: the writable open mapped the page dirty");

        pta.FlushWorker(0);

        Assert.That(dbe.MMF.ActiveChunkWritersOf(memPage), Is.Zero, "the flushed worker still holds the page as written");
    }

    /// <summary>
    /// Two live handles of different archetypes, used in turn — an attacker and its target. Each archetype keeps its own entry in the cluster cache, so
    /// neither use lets go of a page and neither handle has to resolve its cluster again. With one entry, every alternation disposed and rebuilt it.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", CacheBytes)]
    public void HandlesOfTwoArchetypes_UsedInTurn_KeepTheirClusterPages()
    {
        var dbe = Build();
        using var tx = dbe.CreateQuickTransaction();
        var attacker = tx.OpenMut(_fat[X]);
        var target = tx.OpenMut(_fatB[X]);
        var generation = tx.ClusterGeneration.Value;

        for (var round = 0; round < 50; round++)
        {
            var a = attacker.Read(HlFatArch.Fat);
            var t = target.Read(HlFatArchB.Fat);
            Assert.That(a.Key, Is.EqualTo(X));
            Assert.That(t.Key, Is.EqualTo(100_000 + X));
            attacker.Set(HlFatArch.Fat, a);
            target.Set(HlFatArchB.Fat, t);
        }

        Assert.That(tx.ClusterGeneration.Value, Is.EqualTo(generation), "using the handles in turn let go of cluster pages");
    }

    /// <summary>
    /// #1189: one read-only transaction opens every entity of a data set larger than the cache. It refreshes its epoch every 128 opens, so it holds a
    /// bounded set of pages instead of every page it touched; before, its opens pinned the cache full and the next page load timed out on back-pressure.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void ReadOnlyTransaction_OpeningMoreEntitiesThanTheCacheHolds_HoldsABoundedSetOfPages()
    {
        var dbe = Build();
        var peak = 0;
        using (var tx = dbe.CreateReadOnlyTransaction())
        {
            foreach (var (ids, comp, keyBase) in new[] { (_fat, HlFatArch.Fat, 0), (_fatB, HlFatArchB.Fat, 100_000), (_fatC, HlFatArchC.Fat, 200_000) })
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    Assert.That(tx.Open(ids[i]).Read(comp).Key, Is.EqualTo(keyBase + i));
                    if ((i & 1023) == 1023)
                    {
                        dbe.MMF.GetMetrics().GetMemPageExtraInfo(out var census);
                        peak = Math.Max(peak, census.EpochProtectedPageCount);
                    }
                }
            }
        }

        TestContext.Out.WriteLine($"peak epoch-protected pages {peak} of {CachePages}");
        Assert.That(peak, Is.LessThan(CachePages / 4), "the read-only transaction kept what it read epoch-protected");
    }

    /// <summary>
    /// #1199, the fast path: a <see cref="ClusterRef{TArch}"/> and what it hands out are valid until its enumerator's next <c>MoveNext</c> or
    /// <c>Dispose</c>. Strict mode enforces it: a ref used after its step throws instead of reading a page no longer held for it.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    [Property("CacheSize", CacheBytes)]
    public void ClusterRef_UsedAfterItsEnumeratorMovedOn_ThrowsUnderStrictMode()
    {
        Assume.That(CheckConfig.Enabled, Is.True, "the check runs under strict mode only");
        var dbe = Build();
        using var tx = dbe.CreateReadOnlyTransaction();
        var accessor = tx.For<HlFatArch>();
        var clusters = accessor.GetClusterEnumerator();

        Assert.That(clusters.MoveNext(), Is.True);
        var first = clusters.Current;
        Assert.That(first.GetReadOnlySpan(HlFatArch.Fat).Length, Is.GreaterThan(0), "the current step's ref reads");
        Assert.That(clusters.MoveNext(), Is.True);
        Assert.That(Throws(first), Is.True, "a ClusterRef from an earlier step was usable");

        var second = clusters.Current;
        Assert.That(second.GetReadOnlySpan(HlFatArch.Fat).Length, Is.GreaterThan(0));
        clusters.Dispose();
        Assert.That(Throws(second), Is.True, "a ClusterRef was usable after its enumerator was disposed");
        accessor.Dispose();

        static bool Throws(ClusterRef<HlFatArch> cluster)
        {
            try
            {
                _ = cluster.GetReadOnlySpan(HlFatArch.Fat);
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Every entity's key, read back in short transactions: X's, how many others hold the marker, how many others are wrong.</summary>
    private static void Tally(DatabaseEngine dbe, EntityId[] ids, Comp<HlFat> comp, int keyBase, ref int xKey, ref int markers, ref int wrong)
    {
        for (var b = 0; b < ids.Length; b += 256)
        {
            using var rtx = dbe.CreateReadOnlyTransaction();
            for (var i = b; i < Math.Min(b + 256, ids.Length); i++)
            {
                var k = rtx.Open(ids[i]).Read(comp).Key;
                if (keyBase == 0 && i == X)
                {
                    xKey = k;
                }
                else if (k == Marker)
                {
                    markers++;
                }
                else if (k != keyBase + i)
                {
                    wrong++;
                }
            }
        }
    }
}
