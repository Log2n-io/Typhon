using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.OwnWalk.Sv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct OwnWalkSv
{
    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Component("Typhon.Test.OwnWalk.Tr", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct OwnWalkTr
{
    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Component("Typhon.Test.OwnWalk.V", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct OwnWalkV
{
    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Archetype]
partial class OwnWalkSvUnit : Archetype<OwnWalkSvUnit>
{
    public static readonly Comp<OwnWalkSv> Data = Register<OwnWalkSv>();
}

[Archetype]
partial class OwnWalkTrUnit : Archetype<OwnWalkTrUnit>
{
    public static readonly Comp<OwnWalkTr> Data = Register<OwnWalkTr>();
}

[Archetype]
partial class OwnWalkVUnit : Archetype<OwnWalkVUnit>
{
    public static readonly Comp<OwnWalkV> Data = Register<OwnWalkV>();
}

/// <summary>
/// SingleVersion and Transient in one archetype: the occupancy word lives in the persistent chunk, the Transient column in its lockstep chunk.
/// </summary>
[Archetype]
partial class OwnWalkMixUnit : Archetype<OwnWalkMixUnit>
{
    public static readonly Comp<OwnWalkSv> Sv = Register<OwnWalkSv>();
    public static readonly Comp<OwnWalkTr> Tr = Register<OwnWalkTr>();
}

[Component("Typhon.Test.OwnWalk.Parent", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct OwnWalkParentData
{
    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Component("Typhon.Test.OwnWalk.Child", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct OwnWalkChildData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<OwnWalkParent> Owner;

    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Archetype]
partial class OwnWalkParent : Archetype<OwnWalkParent>
{
    public static readonly Comp<OwnWalkParentData> Data = Register<OwnWalkParentData>();
}

[Archetype]
partial class OwnWalkChild : Archetype<OwnWalkChild>
{
    public static readonly Comp<OwnWalkChildData> Data = Register<OwnWalkChildData>();
}

[Component("Typhon.Test.OwnWalk.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct OwnWalkPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class OwnWalkSpatialUnit : Archetype<OwnWalkSpatialUnit>
{
    public static readonly Comp<OwnWalkPos> Pos = Register<OwnWalkPos>();
}

/// <summary>
/// #1482 — a transaction's cluster walk never yields an entity that transaction has destroyed, in every storage mode (CLUSTERWALK-04). Point access already
/// treats an own pending destroy as gone (<c>IsAlive</c>, <c>Open</c>, <c>OpenMut</c>, <c>TryOpen</c>); the walk read the raw occupancy word, which only
/// the PUBLISH clears (VIS-09), so one transaction gave two answers about one entity — the SWG <c>LairLife</c> crash.
/// <para>Scope: the transaction's OWN pending destroys. Another transaction's uncommitted destroy hides nothing; own pending spawns are not yielded by a raw
/// walk (documented limitation).</para>
/// </summary>
[TestFixture]
[NonParallelizable]
class ClusterWalkOwnDestroyTests : TestBase<ClusterWalkOwnDestroyTests>
{
    public enum Storage
    {
        SingleVersion,
        Transient,
        Versioned,
        Mixed,
    }

    /// <summary>The eight public entry points that hand out a <see cref="ClusterEnumerator{TArch}"/> bound to an accessor.</summary>
    public enum Entry
    {
        ArchetypeAccessorAll,
        ArchetypeAccessorRange,
        ArchetypeAccessorIds,
        ArchetypeAccessorRealm,
        EntityAccessorAll,
        EntityAccessorRange,
        EntityAccessorIds,
        EntityAccessorRealm,
    }

    private const int Population = 200;

    private static readonly Storage[] AllStorages = [Storage.SingleVersion, Storage.Transient, Storage.Versioned, Storage.Mixed];

    // A slot is reused only once its destroy RELEASED it at publish: SingleVersion and Transient archetypes. A Versioned one hides the slot until MinTSN passes
    // the destroy (VIS-09), and the destroyer's own open transaction keeps MinTSN below it.
    private static readonly Storage[] ReleasingStorages = [Storage.SingleVersion, Storage.Transient, Storage.Mixed];

    private static readonly Entry[] AllEntries = (Entry[])Enum.GetValues(typeof(Entry));

    // ═══════════════════════════════════════════════════════════════════════
    // The rule
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The verifier: after <c>tx.Destroy(victim)</c>, the walk through <c>tx</c> yields every other entity and not the victim — by <c>OccupancyBits</c>, by
    /// <c>ActiveBits</c>, and in the victim's cluster's <c>LiveCount</c> — while <c>tx.IsAlive(victim)</c> is false.
    /// </summary>
    [TestCaseSource(nameof(AllStorages))]
    [VerifiesRule("CLUSTERWALK-04")]
    public void AnOwnDestroy_IsAbsentFromTheTransactionsOwnWalk(Storage storage)
    {
        using var dbe = SetupEngine();
        var ids = SpawnCommitted(dbe, storage, Population);
        var victim = ids[Population / 2];
        var reference = WalkInFreshTransaction(dbe, storage);
        var victimChunk = reference.ChunkOf[victim];

        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(victim);
        Assert.That(tx.IsAlive(victim), Is.False, "precondition: point access treats the own pending destroy as gone");

        var walk = Walk(tx, storage, Entry.ArchetypeAccessorAll);

        AssertTheOwnDestroyIsAbsent(walk, reference, victim, victimChunk);
    }

    /// <summary>
    /// The mutant: the walk the verifier is handed ignores the own destroy — exactly the walk another transaction makes, which yields the victim. The
    /// verifier must reject it.
    /// </summary>
    [Test]
    [RuleMutant("CLUSTERWALK-04")]
    public void Mutant_AWalkThatIgnoresTheOwnDestroy_IsRejected()
    {
        using var dbe = SetupEngine();
        var ids = SpawnCommitted(dbe, Storage.SingleVersion, Population);
        var victim = ids[Population / 2];
        var reference = WalkInFreshTransaction(dbe, Storage.SingleVersion);

        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(victim);
        using var other = dbe.CreateQuickTransaction();
        var ignoring = Walk(other, Storage.SingleVersion, Entry.ArchetypeAccessorAll);

        RuleMutants.AssertDetects("CLUSTERWALK-04", OwnDestroyMarker,
            () => AssertTheOwnDestroyIsAbsent(ignoring, reference, victim, reference.ChunkOf[victim]));
    }

    private const string OwnDestroyMarker = "CLUSTERWALK-04";

    // Plain asserts: the mutant runs this through RuleMutants.AssertDetects, which recognises the verifier's own failure by its marker.
    private static void AssertTheOwnDestroyIsAbsent(WalkResult walk, WalkResult reference, EntityId victim, int victimChunk)
    {
        Assert.That(walk.ByOccupancy, Does.Not.Contain(victim), $"{OwnDestroyMarker}: OccupancyBits yields an entity its own transaction destroyed");
        Assert.That(walk.ByActive, Does.Not.Contain(victim), $"{OwnDestroyMarker}: ActiveBits yields an entity its own transaction destroyed");
        Assert.That(walk.LiveCount[victimChunk], Is.EqualTo(reference.LiveCount[victimChunk] - 1),
            $"{OwnDestroyMarker}: LiveCount of the victim's cluster counts the own pending destroy");
        Assert.That(walk.ByOccupancy.Count, Is.EqualTo(reference.ByOccupancy.Count - 1), $"{OwnDestroyMarker}: the walk must yield every other entity, once");
        AssertConsistent(walk);
    }

    /// <summary>
    /// A cluster whose every entity the transaction destroyed is empty to that transaction: <c>MoveNext</c> skips it, and the non-empty guarantee holds.
    /// </summary>
    [TestCaseSource(nameof(AllStorages))]
    [VerifiesRule("CLUSTERWALK-04")]
    public void AClusterWhoseEveryEntityIsOwnDestroyed_IsSkippedByMoveNext(Storage storage)
    {
        using var dbe = SetupEngine();
        SpawnCommitted(dbe, storage, Population);
        var reference = WalkInFreshTransaction(dbe, storage);
        Assert.That(reference.Visited.Count, Is.GreaterThanOrEqualTo(2), "precondition: at least two clusters");
        var emptied = reference.Visited[0];
        var members = reference.MembersOf(emptied);

        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in members)
        {
            tx.Destroy(id);
        }

        var walk = Walk(tx, storage, Entry.ArchetypeAccessorAll);

        Assert.That(walk.Visited, Does.Not.Contain(emptied), "MoveNext yields a cluster every entity of which the walking transaction destroyed");
        Assert.That(walk.ByOccupancy.Count, Is.EqualTo(Population - members.Count));
        AssertConsistent(walk);
    }

    /// <summary>
    /// A destroy made DURING the walk — while an earlier cluster is current — hides the entity from every cluster the walk reaches after it: the rule holds
    /// "from <c>tx.Destroy(id)</c> on", not from the walk's start. A design that takes the hidden set once, when the enumerator is created, fails this.
    /// </summary>
    [TestCaseSource(nameof(AllStorages))]
    [VerifiesRule("CLUSTERWALK-04")]
    public void ADestroyMadeDuringTheWalk_HidesTheEntityFromTheClustersAfterIt(Storage storage)
    {
        using var dbe = SetupEngine();
        SpawnCommitted(dbe, storage, Population);
        var reference = WalkInFreshTransaction(dbe, storage);
        Assert.That(reference.Visited.Count, Is.GreaterThanOrEqualTo(2), "precondition: at least two clusters");
        var lastChunk = reference.Visited[^1];
        var target = reference.MembersOf(lastChunk)[0];

        using var tx = dbe.CreateQuickTransaction();
        var yielded = WalkDestroyingAtFirstCluster(tx, storage, target);

        Assert.That(tx.IsAlive(target), Is.False, "precondition");
        Assert.That(yielded, Does.Not.Contain(target), "the walk yields, in a later cluster, an entity its transaction destroyed earlier in the same walk");
        Assert.That(yielded.Count, Is.EqualTo(Population - 1));
    }

    private static HashSet<EntityId> WalkDestroyingAtFirstCluster(Transaction tx, Storage storage, EntityId target) => storage switch
    {
        Storage.SingleVersion => WalkDestroyingAtFirstCluster<OwnWalkSvUnit>(tx, target),
        Storage.Transient => WalkDestroyingAtFirstCluster<OwnWalkTrUnit>(tx, target),
        Storage.Versioned => WalkDestroyingAtFirstCluster<OwnWalkVUnit>(tx, target),
        Storage.Mixed => WalkDestroyingAtFirstCluster<OwnWalkMixUnit>(tx, target),
        _ => throw new ArgumentOutOfRangeException(nameof(storage)),
    };

    private static HashSet<EntityId> WalkDestroyingAtFirstCluster<TArch>(Transaction tx, EntityId target) where TArch : class
    {
        var yielded = new HashSet<EntityId>();
        var first = true;
        foreach (var cluster in tx.GetClusterEnumerator<TArch>())
        {
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                yielded.Add(cluster.GetEntityId(BitOperations.TrailingZeroCount(bits)));
            }

            if (first)
            {
                first = false;
                tx.Destroy(target);
            }
        }

        return yielded;
    }

    /// <summary>Destroys made by <c>DestroyBatch</c> are own pending destroys like any other.</summary>
    [TestCaseSource(nameof(AllStorages))]
    [VerifiesRule("CLUSTERWALK-04")]
    public void ADestroyBatch_HidesEveryEntityOfTheBatchFromTheOwnWalk(Storage storage)
    {
        using var dbe = SetupEngine();
        var ids = SpawnCommitted(dbe, storage, Population);
        var batch = new[] { ids[3], ids[70], ids[71], ids[150] };

        using var tx = dbe.CreateQuickTransaction();
        tx.DestroyBatch(batch);

        var walk = Walk(tx, storage, Entry.ArchetypeAccessorAll);

        foreach (var id in batch)
        {
            Assert.That(tx.IsAlive(id), Is.False, "precondition");
            Assert.That(walk.ByOccupancy, Does.Not.Contain(id), $"the walk yields {id}, destroyed by its own DestroyBatch");
        }
        Assert.That(walk.ByOccupancy.Count, Is.EqualTo(Population - batch.Length));
        AssertConsistent(walk);
    }

    /// <summary>A cascade destroys the children in the same transaction: its walk of the CHILD archetype must not yield them either.</summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-04")]
    public void ACascadeDestroy_HidesTheChildrenFromTheOwnWalk()
    {
        using var dbe = SetupEngine();
        EntityId parent;
        var children = new List<EntityId>();
        var bystanders = new List<EntityId>();
        using (var t = dbe.CreateQuickTransaction())
        {
            parent = t.Spawn<OwnWalkParent>(OwnWalkParent.Data.Set(new OwnWalkParentData { Value = 1 }));
            var other = t.Spawn<OwnWalkParent>(OwnWalkParent.Data.Set(new OwnWalkParentData { Value = 2 }));
            for (var i = 0; i < 5; i++)
            {
                children.Add(t.Spawn<OwnWalkChild>(OwnWalkChild.Data.Set(new OwnWalkChildData { Owner = parent, Value = i })));
                bystanders.Add(t.Spawn<OwnWalkChild>(OwnWalkChild.Data.Set(new OwnWalkChildData { Owner = other, Value = 100 + i })));
            }

            t.Commit();
        }

        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(parent);
        foreach (var c in children)
        {
            Assert.That(tx.IsAlive(c), Is.False, "precondition: the cascade made the child an own pending destroy");
        }

        var walk = Walk<OwnWalkChild>(tx, Entry.ArchetypeAccessorAll);

        foreach (var c in children)
        {
            Assert.That(walk.ByOccupancy, Does.Not.Contain(c), $"the walk yields child {c}, destroyed by the cascade of its own transaction");
        }
        foreach (var b in bystanders)
        {
            Assert.That(walk.ByOccupancy, Does.Contain(b), "a child of another parent must still be yielded");
        }
        AssertConsistent(walk);
    }

    /// <summary>Every public entry point returning a transaction-bound <see cref="ClusterEnumerator{TArch}"/> honours the rule.</summary>
    [TestCaseSource(nameof(AllEntries))]
    [VerifiesRule("CLUSTERWALK-04")]
    public void EveryWalkEntryPoint_HidesAnOwnDestroy(Entry entry)
    {
        using var dbe = SetupEngine();
        var ids = SpawnCommitted(dbe, Storage.SingleVersion, Population);
        var victim = ids[Population / 3];

        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(victim);

        var walk = Walk<OwnWalkSvUnit>(tx, entry);

        Assert.That(walk.ByOccupancy, Does.Not.Contain(victim), $"{entry} yields an entity its own transaction destroyed");
        Assert.That(walk.ByOccupancy.Count, Is.EqualTo(Population - 1));
        AssertConsistent(walk);
    }

    /// <summary>
    /// The SWG <c>LairLife</c> shape, end to end: a system's side transaction destroys an entity, walks the archetype's clusters through that transaction and
    /// opens for writing every entity the walk yields. The open threw on the destroyed entity and aborted the tick.
    /// </summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-04")]
    public void ASystemsSideTransaction_NeverWalksIntoAnEntityItDestroyed()
    {
        using var dbe = SetupEngine();
        var ids = SpawnCommitted(dbe, Storage.SingleVersion, Population);
        var victim = ids[17];
        var walkedVictim = false;
        Exception failure = null;
        var yielded = 0;
        using var done = new ManualResetEventSlim(false);
        var ran = 0;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("LairLife", ctx =>
            {
                if (Interlocked.Exchange(ref ran, 1) != 0)
                {
                    return;
                }

                try
                {
                    using var side = ctx.CreateSideTransaction(DurabilityMode.Deferred);
                    side.Destroy(victim);
                    var picked = new List<EntityId>();
                    var accessor = side.For<OwnWalkSvUnit>();
                    foreach (var cluster in accessor.GetClusterEnumerator())
                    {
                        for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                        {
                            picked.Add(cluster.GetEntityId(BitOperations.TrailingZeroCount(bits)));
                        }
                    }
                    accessor.Dispose();

                    yielded = picked.Count;
                    walkedVictim = picked.Contains(victim);
                    foreach (var id in picked)
                    {
                        side.OpenMut(id);   // what LairLife does with what the walk chose
                    }

                    side.Commit();
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    done.Set();
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            Assert.That(done.Wait(TimeSpan.FromSeconds(10)), Is.True, "the system never ran");
            runtime.Shutdown();
        }

        Assert.That(walkedVictim, Is.False, "the side transaction's walk yielded the entity it had just destroyed");
        Assert.That(failure, Is.Null, $"acting on what the walk yielded failed: {failure}");
        Assert.That(yielded, Is.EqualTo(Population - 1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scope: what the rule does NOT hide
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Another transaction's uncommitted destroy hides nothing: for every other reader the entity is alive until that destroy commits (VIS-09).
    /// </summary>
    [TestCaseSource(nameof(AllStorages))]
    public void AnotherTransactionsUncommittedDestroy_IsStillYieldedToOtherWalks(Storage storage)
    {
        using var dbe = SetupEngine();
        var ids = SpawnCommitted(dbe, storage, Population);
        var victim = ids[42];

        using var destroyer = dbe.CreateQuickTransaction();
        destroyer.Destroy(victim);

        using var other = dbe.CreateQuickTransaction();
        var walk = Walk(other, storage, Entry.ArchetypeAccessorAll);

        Assert.That(other.IsAlive(victim), Is.True, "precondition: alive to another transaction");
        Assert.That(walk.ByOccupancy, Does.Contain(victim), "another transaction's uncommitted destroy must hide nothing");
        Assert.That(walk.ByOccupancy.Count, Is.EqualTo(Population));
    }

    /// <summary>After the commit the entity is gone from every walk, the destroyer's own included.</summary>
    [TestCaseSource(nameof(AllStorages))]
    public void ACommittedDestroy_IsGoneFromEveryWalk(Storage storage)
    {
        using var dbe = SetupEngine();
        var ids = SpawnCommitted(dbe, storage, Population);
        var victim = ids[99];

        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(victim);
        Assert.That(tx.Commit(), Is.True);

        var own = Walk(tx, storage, Entry.ArchetypeAccessorAll);
        var fresh = WalkInFreshTransaction(dbe, storage);

        Assert.That(own.ByOccupancy, Does.Not.Contain(victim), "the committed transaction's walk");
        Assert.That(fresh.ByOccupancy, Does.Not.Contain(victim), "a later transaction's walk");
        Assert.That(fresh.ByOccupancy.Count, Is.EqualTo(Population - 1));
    }

    /// <summary>A rolled-back destroy leaves the entity in every later walk.</summary>
    [TestCaseSource(nameof(AllStorages))]
    public void ARolledBackDestroy_LeavesTheEntityInEveryLaterWalk(Storage storage)
    {
        using var dbe = SetupEngine();
        var ids = SpawnCommitted(dbe, storage, Population);
        var victim = ids[5];

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(victim);
            Assert.That(tx.Rollback(), Is.True);
        }

        using var later = dbe.CreateQuickTransaction();
        var walk = Walk(later, storage, Entry.ArchetypeAccessorAll);

        Assert.That(later.IsAlive(victim), Is.True);
        Assert.That(walk.ByOccupancy, Does.Contain(victim), "a rolled-back destroy must hide nothing");
        Assert.That(walk.ByOccupancy.Count, Is.EqualTo(Population));
    }

    /// <summary>
    /// Documented limitation, green today and meant to stay so: an own pending spawn holds no published slot before PUBLISH, so a raw walk does not yield it.
    /// Point access does.
    /// </summary>
    [TestCaseSource(nameof(AllStorages))]
    public void AnOwnPendingSpawn_IsNotYieldedByTheRawWalk(Storage storage)
    {
        using var dbe = SetupEngine();
        SpawnCommitted(dbe, storage, Population);

        using var tx = dbe.CreateQuickTransaction();
        var spawned = SpawnOne(tx, storage, 9999);
        var walk = Walk(tx, storage, Entry.ArchetypeAccessorAll);

        Assert.That(tx.IsAlive(spawned), Is.True, "point access sees the own pending spawn");
        Assert.That(walk.ByOccupancy, Does.Not.Contain(spawned), "the raw walk is committed HEAD minus own destroys: no own spawn");
        Assert.That(walk.ByOccupancy.Count, Is.EqualTo(Population));
    }

    /// <summary>
    /// <c>ClusterRef.Set</c> on the slot of an own pending destroy refuses it, as <c>OpenMut</c> does: to the walking transaction the slot holds no entity.
    /// Both overloads, before any bookkeeping.
    /// </summary>
    [TestCase(Storage.SingleVersion)]
    [TestCase(Storage.Mixed)]
    [VerifiesRule("CLUSTERWALK-04")]
    public void ClusterRefSet_OnAnOwnDestroyedSlot_IsRefused(Storage storage)
    {
        using var dbe = SetupEngine();
        var ids = SpawnCommitted(dbe, storage, Population);
        var victim = ids[Population / 2];
        var reference = WalkInFreshTransaction(dbe, storage);

        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(victim);
        var (single, batch) = storage == Storage.SingleVersion
            ? SetOnSlot<OwnWalkSvUnit>(tx, OwnWalkSvUnit.Data, reference.ChunkOf[victim], reference.SlotOf[victim])
            : SetOnSlot<OwnWalkMixUnit>(tx, OwnWalkMixUnit.Sv, reference.ChunkOf[victim], reference.SlotOf[victim]);

        Assert.That(single, Is.InstanceOf<ArgumentException>(), "Set(comp, slot, value) wrote into the slot of an own pending destroy");
        Assert.That(batch, Is.InstanceOf<ArgumentException>(), "Set(comp, slots, values) wrote into the slot of an own pending destroy");
        Assert.That(tx.Rollback(), Is.True);
    }

    /// <summary>
    /// The engine's bookkeeping keeps reading the raw occupancy: a span write over a cluster holding an own pending destroy, followed by
    /// <c>MarkDirty</c>, still marks the destroyed slot dirty — if the destroy is rolled back, the bytes the span wrote there must reach the fence (IX-07).
    /// </summary>
    [TestCase(Storage.SingleVersion)]
    [TestCase(Storage.Mixed)]
    [VerifiesRule("CLUSTERWALK-04")]
    public void MarkDirty_StillMarksTheSlotOfAnOwnDestroy(Storage storage)
    {
        using var dbe = SetupEngine();
        var ids = SpawnCommitted(dbe, storage, Population);
        var victim = ids[Population / 2];
        var reference = WalkInFreshTransaction(dbe, storage);
        var chunk = reference.ChunkOf[victim];
        var slot = reference.SlotOf[victim];
        var state = dbe._archetypeStates[MetaOf(storage).ArchetypeId].ClusterState;

        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(victim);
        state.ClusterDirtyBitmap.Snapshot();   // drop the spawn's marks
        var visible = storage == Storage.SingleVersion
            ? MarkDirtyOn<OwnWalkSvUnit>(tx, OwnWalkSvUnit.Data, chunk)
            : MarkDirtyOn<OwnWalkMixUnit>(tx, OwnWalkMixUnit.Sv, chunk);

        Assert.That(visible & (1UL << slot), Is.EqualTo(0UL), "precondition: the walk hides the own destroy");
        Assert.That(state.ClusterDirtyBitmap.ReadWord(chunk) & (1UL << slot), Is.Not.EqualTo(0UL),
            "MarkDirty skipped the slot of an own pending destroy: a rolled-back destroy would lose the span's write");
        Assert.That(tx.Rollback(), Is.True);
    }

    private static (Exception Single, Exception Batch) SetOnSlot<TArch>(Transaction tx, Comp<OwnWalkSv> comp, int chunk, int slot) where TArch : class
    {
        Exception single = null;
        Exception batch = null;
        var accessor = tx.For<TArch>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != chunk)
                {
                    continue;
                }

                try
                {
                    cluster.Set(comp, slot, new OwnWalkSv { Value = -1 });
                }
                catch (Exception ex)
                {
                    single = ex;
                }

                try
                {
                    var values = new OwnWalkSv[64];
                    cluster.Set(comp, 1UL << slot, values);
                }
                catch (Exception ex)
                {
                    batch = ex;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return (single, batch);
    }

    /// <summary>Span-writes every slot of cluster <paramref name="chunk"/>, marks it dirty, and returns the occupancy the walk showed.</summary>
    private static ulong MarkDirtyOn<TArch>(Transaction tx, Comp<OwnWalkSv> comp, int chunk) where TArch : class
    {
        var visible = 0UL;
        var accessor = tx.For<TArch>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != chunk)
                {
                    continue;
                }

                visible = cluster.OccupancyBits;
                var span = cluster.GetSpan(comp);
                for (var i = 0; i < span.Length; i++)
                {
                    span[i].Pad = 7;
                }
                cluster.MarkDirty(comp);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return visible;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Where an own destroy's slot can go stale before the walk
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Design guard, GREEN today: transaction A destroys <c>e</c>; B destroys it too and commits, releasing its slot; C spawns <c>f</c> into that slot. A's
    /// walk must yield <c>f</c> — a live entity A never destroyed — and not <c>e</c>. A fix that hides the (cluster, slot) A recorded at its destroy without
    /// checking who holds it now hides <c>f</c>.
    /// </summary>
    [TestCaseSource(nameof(ReleasingStorages))]
    public void ASlotReusedAfterAnotherTransactionsDestroy_IsYieldedToTheFirstDestroyer(Storage storage)
    {
        using var dbe = SetupEngine();
        var clusterSize = MetaOf(storage).ClusterLayout.ClusterSize;
        var ids = SpawnCommitted(dbe, storage, clusterSize);   // one full cluster: the only free slot after B's commit is e's
        var e = ids[clusterSize / 2];
        var before = WalkInFreshTransaction(dbe, storage);
        var eChunk = before.ChunkOf[e];
        var eSlot = before.SlotOf[e];

        using var a = dbe.CreateQuickTransaction();
        a.Destroy(e);

        using (var b = dbe.CreateQuickTransaction())
        {
            b.Destroy(e);
            Assert.That(b.Commit(), Is.True);
        }

        EntityId f;
        using (var c = dbe.CreateQuickTransaction())
        {
            f = SpawnOne(c, storage, 4242);
            Assert.That(c.Commit(), Is.True);
        }

        var after = WalkInFreshTransaction(dbe, storage);
        Assert.That(after.ChunkOf.TryGetValue(f, out var fChunk) && fChunk == eChunk && after.SlotOf[f] == eSlot, Is.True,
            "precondition: the spawn must reuse e's slot");

        var walk = Walk(a, storage, Entry.ArchetypeAccessorAll);

        Assert.That(walk.ByOccupancy, Does.Contain(f), "a live entity in a slot the transaction's destroy once named must be yielded");
        Assert.That(walk.ByOccupancy, Does.Not.Contain(e));
        AssertConsistent(walk);
    }

    /// <summary>
    /// Transaction A destroys <c>e</c>; B moves <c>e</c> to another cell and the fence migrates it to another cluster before A walks. A's walk must still not
    /// yield <c>e</c>, at its new slot. A fix that keeps the (cluster, slot) A saw at its destroy without noticing the move hides the old slot and yields
    /// <c>e</c> at the new one.
    /// </summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-04")]
    public void AnOwnDestroyMovedByTheFenceBeforeTheWalk_IsStillAbsent()
    {
        using var dbe = SetupSpatialEngine();
        EntityId e;
        var bystanders = new List<EntityId>();
        using (var t = dbe.CreateQuickTransaction())
        {
            e = t.Spawn<OwnWalkSpatialUnit>(OwnWalkSpatialUnit.Pos.Set(PointAt(50f, 50f)));
            for (var i = 0; i < 4; i++)
            {
                bystanders.Add(t.Spawn<OwnWalkSpatialUnit>(OwnWalkSpatialUnit.Pos.Set(PointAt(40f + i, 40f))));
            }

            t.Commit();
        }

        dbe.WriteTickFence(1);
        var before = Walk<OwnWalkSpatialUnit>(dbe.CreateQuickTransaction(), Entry.ArchetypeAccessorAll, disposeTx: true);
        var eChunkBefore = before.ChunkOf[e];

        using var a = dbe.CreateQuickTransaction();
        a.Destroy(e);

        using (var b = dbe.CreateQuickTransaction())
        {
            var m = b.OpenMut(e);
            m.Set(OwnWalkSpatialUnit.Pos, PointAt(450f, 650f));
            Assert.That(b.Commit(), Is.True);
        }

        dbe.WriteTickFence(2);
        var after = Walk<OwnWalkSpatialUnit>(dbe.CreateQuickTransaction(), Entry.ArchetypeAccessorAll, disposeTx: true);
        Assert.That(after.ChunkOf[e], Is.Not.EqualTo(eChunkBefore), "precondition: the fence moved e to another cluster");

        var walk = Walk<OwnWalkSpatialUnit>(a, Entry.ArchetypeAccessorAll);

        Assert.That(walk.ByOccupancy, Does.Not.Contain(e), "the walk yields an own pending destroy the fence moved after the destroy");
        foreach (var id in bystanders)
        {
            Assert.That(walk.ByOccupancy, Does.Contain(id));
        }
        AssertConsistent(walk);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spatial cluster queries: the transaction form and the engine form
    // ═══════════════════════════════════════════════════════════════════════

    private static readonly AABB2F WholeWorld = new() { MinX = 0f, MinY = 0f, MaxX = 999f, MaxY = 999f };
    private static readonly BSphere2F WholeWorldSphere = new() { CenterX = 500f, CenterY = 500f, Radius = 800f };

    public enum SpatialForm
    {
        Aabb,
        Radius,
        ForEachInRadius,
        CountRadius,
    }

    /// <summary>
    /// <c>tx.ClusterSpatialQuery</c> never returns an entity that transaction destroyed — every query shape, the radius batch included — while the engine
    /// form, which sees no transaction's pending work, still does (its documented semantics).
    /// </summary>
    [TestCase(SpatialForm.Aabb)]
    [TestCase(SpatialForm.Radius)]
    [TestCase(SpatialForm.ForEachInRadius)]
    [TestCase(SpatialForm.CountRadius)]
    [VerifiesRule("CLUSTERWALK-04")]
    public void TheTransactionsSpatialQuery_DropsItsOwnDestroy_TheEnginesDoesNot(SpatialForm form)
    {
        using var dbe = SetupSpatialEngine();
        var ids = SpawnSpatialGrid(dbe, 200);
        var victim = ids[77];

        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(victim);

        var throughTx = SpatialHits(tx.ClusterSpatialQuery<OwnWalkSpatialUnit>(), form, out var txCount);
        var throughEngine = SpatialHits(dbe.ClusterSpatialQuery<OwnWalkSpatialUnit>(), form, out var engineCount);

        if (form == SpatialForm.CountRadius)
        {
            Assert.That(txCount, Is.EqualTo(ids.Count - 1), "CountRadius through the transaction counts its own pending destroy");
            Assert.That(engineCount, Is.EqualTo(ids.Count), "the engine form sees no transaction's pending work");
            return;
        }

        Assert.That(throughTx, Does.Not.Contain(victim), $"{form} through the transaction returns an entity it destroyed");
        Assert.That(throughTx.Count, Is.EqualTo(ids.Count - 1), "every other entity, once");
        Assert.That(throughEngine, Does.Contain(victim), "the engine form sees no transaction's pending work");
        Assert.That(throughEngine.Count, Is.EqualTo(ids.Count));
    }

    /// <summary>
    /// A destroy made while a transaction's spatial query runs drops the entity from every cluster the query opens after it, as for the cluster walk.
    /// </summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-04")]
    public void ADestroyMadeDuringTheTransactionsSpatialQuery_DropsTheEntityFromTheClustersAfterIt()
    {
        using var dbe = SetupSpatialEngine();
        var ids = SpawnSpatialGrid(dbe, 200);

        using var tx = dbe.CreateQuickTransaction();
        var destroyed = new HashSet<EntityId>();
        var leaked = new List<EntityId>();
        var firstChunk = -1;
        var e = tx.ClusterSpatialQuery<OwnWalkSpatialUnit>().AABB(in WholeWorld);
        try
        {
            while (e.MoveNext())
            {
                var hit = e.Current;
                if (firstChunk < 0)
                {
                    // At the first hit, destroy every entity outside the current cluster: none of them may be returned from here on.
                    firstChunk = hit.ClusterChunkId;
                    var before = SpatialChunkOf(dbe);
                    foreach (var id in ids)
                    {
                        if (before[id] != firstChunk)
                        {
                            tx.Destroy(id);
                            destroyed.Add(id);
                        }
                    }
                }
                else if (destroyed.Contains(hit.Entity))
                {
                    leaked.Add(hit.Entity);
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        Assert.That(destroyed, Is.Not.Empty, "precondition: the entities span more than one cluster");
        Assert.That(leaked, Is.Empty, "the query returned entities its transaction destroyed while it ran");
    }

    private static List<EntityId> SpawnSpatialGrid(DatabaseEngine dbe, int count)
    {
        var ids = new List<EntityId>(count);
        using (var t = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                ids.Add(t.Spawn<OwnWalkSpatialUnit>(OwnWalkSpatialUnit.Pos.Set(PointAt(5f + i % 20 * 45f, 5f + i / 20 * 45f))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        return ids;
    }

    private static Dictionary<EntityId, int> SpatialChunkOf(DatabaseEngine dbe)
    {
        var map = new Dictionary<EntityId, int>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var e = dbe.ClusterSpatialQuery<OwnWalkSpatialUnit>().AABB(in WholeWorld);
        try
        {
            while (e.MoveNext())
            {
                map[e.Current.Entity] = e.Current.ClusterChunkId;
            }
        }
        finally
        {
            e.Dispose();
        }

        return map;
    }

    private static HashSet<EntityId> SpatialHits(TransactionClusterSpatialQuery<OwnWalkSpatialUnit> query, SpatialForm form, out int count)
    {
        count = 0;
        switch (form)
        {
            case SpatialForm.Aabb:
                return Drain(query.AABB(in WholeWorld));
            case SpatialForm.Radius:
                return Drain(query.Radius(in WholeWorldSphere));
            case SpatialForm.ForEachInRadius:
            {
                var sink = new CollectingSink { Hits = [] };
                query.ForEachInRadius([WholeWorldSphere], ref sink);
                return sink.Hits;
            }
            default:
            {
                Span<int> counts = stackalloc int[1];
                query.CountRadius([WholeWorldSphere], counts);
                count = counts[0];
                return [];
            }
        }
    }

    private static HashSet<EntityId> SpatialHits(ClusterSpatialQuery<OwnWalkSpatialUnit> query, SpatialForm form, out int count)
    {
        count = 0;
        switch (form)
        {
            case SpatialForm.Aabb:
                return Drain(query.AABB(in WholeWorld));
            case SpatialForm.Radius:
                return Drain(query.Radius(in WholeWorldSphere));
            case SpatialForm.ForEachInRadius:
            {
                var sink = new CollectingSink { Hits = [] };
                query.ForEachInRadius([WholeWorldSphere], ref sink);
                return sink.Hits;
            }
            default:
            {
                Span<int> counts = stackalloc int[1];
                query.CountRadius([WholeWorldSphere], counts);
                count = counts[0];
                return [];
            }
        }
    }

    private static HashSet<EntityId> Drain(AabbClusterEnumerator e)
    {
        var hits = new HashSet<EntityId>();
        try
        {
            while (e.MoveNext())
            {
                Assert.That(hits.Add(e.Current.Entity), Is.True, "an entity returned twice");
            }
        }
        finally
        {
            e.Dispose();
        }

        return hits;
    }

    private struct CollectingSink : IRadiusBatchSink
    {
        public HashSet<EntityId> Hits;

        public bool Hit(int member, in ClusterSpatialQueryResult hit)
        {
            Hits.Add(hit.Entity);
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class WalkResult
    {
        public readonly List<int> Visited = [];
        public readonly HashSet<EntityId> ByOccupancy = [];
        public readonly HashSet<EntityId> ByActive = [];
        public readonly Dictionary<int, int> LiveCount = [];
        public readonly Dictionary<int, ulong> Occupancy = [];
        public readonly Dictionary<EntityId, int> ChunkOf = [];
        public readonly Dictionary<EntityId, int> SlotOf = [];

        public List<EntityId> MembersOf(int chunk)
        {
            var list = new List<EntityId>();
            foreach (var (id, c) in ChunkOf)
            {
                if (c == chunk)
                {
                    list.Add(id);
                }
            }

            return list;
        }
    }

    /// <summary>
    /// The walk's own answers must agree: every visited cluster non-empty, <c>LiveCount</c> = popcount(<c>OccupancyBits</c>), no cluster twice.
    /// </summary>
    private static void AssertConsistent(WalkResult walk)
    {
        Assert.That(new HashSet<int>(walk.Visited).Count, Is.EqualTo(walk.Visited.Count), "a cluster visited twice");
        foreach (var chunk in walk.Visited)
        {
            Assert.That(walk.Occupancy[chunk], Is.Not.EqualTo(0UL), $"MoveNext yielded cluster {chunk} with OccupancyBits == 0 (non-empty guarantee)");
            Assert.That(walk.LiveCount[chunk], Is.EqualTo(BitOperations.PopCount(walk.Occupancy[chunk])),
                $"cluster {chunk}: LiveCount disagrees with OccupancyBits");
        }
    }

    private WalkResult WalkInFreshTransaction(DatabaseEngine dbe, Storage storage)
    {
        using var tx = dbe.CreateQuickTransaction();
        return Walk(tx, storage, Entry.ArchetypeAccessorAll);
    }

    private static WalkResult Walk(Transaction tx, Storage storage, Entry entry) => storage switch
    {
        Storage.SingleVersion => Walk<OwnWalkSvUnit>(tx, entry),
        Storage.Transient => Walk<OwnWalkTrUnit>(tx, entry),
        Storage.Versioned => Walk<OwnWalkVUnit>(tx, entry),
        Storage.Mixed => Walk<OwnWalkMixUnit>(tx, entry),
        _ => throw new ArgumentOutOfRangeException(nameof(storage)),
    };

    private static WalkResult Walk<TArch>(Transaction tx, Entry entry, bool disposeTx = false) where TArch : class
    {
        try
        {
            var result = new WalkResult();
            var state = tx.DBE._archetypeStates[ArchetypeRegistry.GetMetadata<TArch>().ArchetypeId].ClusterState;
            var activeIds = state.ReadActiveClusterList(out var activeCount);
            var accessor = tx.For<TArch>();
            try
            {
                var e = entry switch
                {
                    Entry.ArchetypeAccessorAll => accessor.GetClusterEnumerator(),
                    Entry.ArchetypeAccessorRange => accessor.GetClusterEnumerator(0, activeCount),
                    Entry.ArchetypeAccessorIds => accessor.GetClusterEnumerator(activeIds, 0, activeCount),
                    Entry.ArchetypeAccessorRealm => accessor.GetClusterEnumerator(RealmId.Default),
                    Entry.EntityAccessorAll => tx.GetClusterEnumerator<TArch>(),
                    Entry.EntityAccessorRange => tx.GetClusterEnumerator<TArch>(0, activeCount),
                    Entry.EntityAccessorIds => tx.GetClusterEnumerator<TArch>(activeIds, 0, activeCount),
                    Entry.EntityAccessorRealm => tx.GetClusterEnumerator<TArch>(RealmId.Default),
                    _ => throw new ArgumentOutOfRangeException(nameof(entry)),
                };
                foreach (var cluster in e)
                {
                    var chunk = cluster.ChunkId;
                    result.Visited.Add(chunk);
                    var occupancy = cluster.OccupancyBits;
                    result.Occupancy[chunk] = occupancy;
                    result.LiveCount[chunk] = cluster.LiveCount;
                    for (var bits = occupancy; bits != 0; bits &= bits - 1)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        var id = cluster.GetEntityId(slot);
                        result.ByOccupancy.Add(id);
                        result.ChunkOf[id] = chunk;
                        result.SlotOf[id] = slot;
                    }

                    for (var bits = cluster.ActiveBits(0); bits != 0; bits &= bits - 1)
                    {
                        result.ByActive.Add(cluster.GetEntityId(BitOperations.TrailingZeroCount(bits)));
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }

            return result;
        }
        finally
        {
            if (disposeTx)
            {
                tx.Dispose();
            }
        }
    }

    private static ArchetypeMetadata MetaOf(Storage storage) => storage switch
    {
        Storage.SingleVersion => Archetype<OwnWalkSvUnit>.Metadata,
        Storage.Transient => Archetype<OwnWalkTrUnit>.Metadata,
        Storage.Versioned => Archetype<OwnWalkVUnit>.Metadata,
        Storage.Mixed => Archetype<OwnWalkMixUnit>.Metadata,
        _ => throw new ArgumentOutOfRangeException(nameof(storage)),
    };

    private static EntityId SpawnOne(Transaction tx, Storage storage, int value) => storage switch
    {
        Storage.SingleVersion => tx.Spawn<OwnWalkSvUnit>(OwnWalkSvUnit.Data.Set(new OwnWalkSv { Value = value })),
        Storage.Transient => tx.Spawn<OwnWalkTrUnit>(OwnWalkTrUnit.Data.Set(new OwnWalkTr { Value = value })),
        Storage.Versioned => tx.Spawn<OwnWalkVUnit>(OwnWalkVUnit.Data.Set(new OwnWalkV { Value = value })),
        Storage.Mixed => tx.Spawn<OwnWalkMixUnit>(OwnWalkMixUnit.Sv.Set(new OwnWalkSv { Value = value }),
            OwnWalkMixUnit.Tr.Set(new OwnWalkTr { Value = value })),
        _ => throw new ArgumentOutOfRangeException(nameof(storage)),
    };

    private static List<EntityId> SpawnCommitted(DatabaseEngine dbe, Storage storage, int count)
    {
        var ids = new List<EntityId>(count);
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids.Add(SpawnOne(tx, storage, i));
        }

        Assert.That(tx.Commit(), Is.True);
        return ids;
    }

    private static OwnWalkPos PointAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<OwnWalkSv>();
        dbe.RegisterComponentFromAccessor<OwnWalkTr>();
        dbe.RegisterComponentFromAccessor<OwnWalkV>();
        dbe.RegisterComponentFromAccessor<OwnWalkParentData>();
        dbe.RegisterComponentFromAccessor<OwnWalkChildData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private DatabaseEngine SetupSpatialEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<OwnWalkPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000f, 1000f), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }
}
