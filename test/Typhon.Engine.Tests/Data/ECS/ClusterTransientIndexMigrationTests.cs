using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.TrMig.Runtime", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct TrMigRuntime
{
    [Index] public int Code;
    [Index(AllowMultiple = true)] public int Group;
}

[Component("Typhon.Test.TrMig.BarePos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TrMigBarePos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

/// <summary>No persistent index: its Transient fields are the first of the staging's field ids.</summary>
[Archetype]
class TrMigBareUnit : Archetype<TrMigBareUnit>
{
    public static readonly Comp<TrMigBarePos> Pos = Register<TrMigBarePos>();
    public static readonly Comp<TrMigRuntime> Runtime = Register<TrMigRuntime>();
}

[Archetype]
class TrMigUnit : Archetype<TrMigUnit>
{
    public static readonly Comp<ClMigPos> Pos = Register<ClMigPos>();
    public static readonly Comp<TrMigRuntime> Runtime = Register<TrMigRuntime>();
}

/// <summary>
/// #1162 (IX-02): migration re-valued the persistent index home and nothing else. On a spatial archetype with an indexed Transient field, a migrant's entries kept
/// its source location, a released slot; its multi-value element id stayed behind in the source's tail; and its destination cluster's zone map never saw it.
/// </summary>
[TestFixture]
[NonParallelizable]
unsafe class ClusterTransientIndexMigrationTests : TestBase<ClusterTransientIndexMigrationTests>
{
    private const float CellSize = 100f;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<TrMigRuntime>();
        dbe.RegisterComponentFromAccessor<TrMigBarePos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000f, 1000f), cellSize: CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ClMigPos At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static EntityId Spawn(DatabaseEngine dbe, float x, int code, int group)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<TrMigUnit>(TrMigUnit.Pos.Set(At(x, 50f)), TrMigUnit.Runtime.Set(new TrMigRuntime { Code = code, Group = group }));
        Assert.That(tx.Commit(), Is.True);
        return id;
    }

    private static int ClusterOf(DatabaseEngine dbe, EntityId id)
    {
        var state = dbe._archetypeStates[Archetype<TrMigUnit>.Metadata.ArchetypeId];
        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = state.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            Assert.That(state.EntityMap.TryGet(id.EntityKey, record, ref accessor), Is.True, "premise: the entity is mapped");
            return ClusterEntityRecordAccessor.GetClusterChunkId(record);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private static void Cross(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.OpenMut(id).Set(TrMigUnit.Pos, At(550f, 750f));
        Assert.That(tx.Commit(), Is.True);
    }

    /// <summary>The fence that applies the staged moves: serial, or the parallel phases, whose apply sites are others.</summary>
    private static void Fence(DatabaseEngine dbe, bool parallel, long tick)
    {
        if (!parallel)
        {
            dbe.WriteTickFence(tick);
            return;
        }

        using var runtime = TyphonRuntime.Create(dbe, schedule => schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => { }),
            new RuntimeOptions { WorkerCount = 4, BaseTickRate = 1000, EnableParallelFence = true });
        Exception unhandled = null;
        runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);
        runtime.Start();
        Assert.That(SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5)), Is.True, "the runtime stopped ticking");
        runtime.Shutdown();
        Assert.That(unhandled, Is.Null);
    }

    private static EntityId[] ByCode(DatabaseEngine dbe, int code)
    {
        using var tx = dbe.CreateQuickTransaction();
        return [.. tx.Query<TrMigUnit>().WhereField<TrMigRuntime>(r => r.Code == code).Execute()];
    }

    private static EntityId[] ByGroup(DatabaseEngine dbe, int group)
    {
        using var tx = dbe.CreateQuickTransaction();
        return [.. tx.Query<TrMigUnit>().WhereField<TrMigRuntime>(r => r.Group == group).Execute()];
    }

    /// <summary>A migrant is found through both Transient trees: their entries follow it to its new slot.</summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("IX-02")]
    public void Migration_RepointsTransientIndexEntries([Values] bool parallel)
    {
        using var dbe = SetupEngine();
        var migrant = Spawn(dbe, 50f, 1, 7);
        var stays = Spawn(dbe, 51f, 2, 7);
        dbe.WriteTickFence(1);
        var source = ClusterOf(dbe, migrant);

        Cross(dbe, migrant);
        Fence(dbe, parallel, 2);
        Assert.That(ClusterOf(dbe, migrant), Is.Not.EqualTo(source), "premise: it migrated");

        QueryPathProbe.Forced = ClusterScanPath.Selective;
        try
        {
            Assert.That(ByCode(dbe, 1), Is.EqualTo(new[] { migrant }), "the unique tree");
            Assert.That(ByGroup(dbe, 7), Is.EquivalentTo(new[] { migrant, stays }), "the multi-value tree");
        }
        finally
        {
            QueryPathProbe.Reset();
        }

        IndexDataOracle.AssertIndexAgreesWithData<TrMigUnit>(dbe, "after the migration");
    }

    /// <summary>The element id travels with the migrant: its next key change finds the entry to move, and its old key lets it go.</summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("IX-02")]
    public void Migration_CarriesATransientElementId([Values] bool parallel)
    {
        using var dbe = SetupEngine();
        var migrant = Spawn(dbe, 50f, 1, 7);
        var stays = Spawn(dbe, 51f, 2, 7);
        dbe.WriteTickFence(1);

        Cross(dbe, migrant);
        Fence(dbe, parallel, 2);

        var missesBefore = Volatile.Read(ref BTreeMoveValueMisses.Count);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(migrant).Set(TrMigUnit.Runtime, new TrMigRuntime { Code = 1, Group = 8 });
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(10);

        Assert.That(Volatile.Read(ref BTreeMoveValueMisses.Count), Is.EqualTo(missesBefore), "the move found its entry");
        Assert.That(ByGroup(dbe, 8), Is.EqualTo(new[] { migrant }));
        Assert.That(ByGroup(dbe, 7), Is.EqualTo(new[] { stays }));
    }

    /// <summary>A destroy finds a migrant's entries by its new location, and the entity that takes its old slot is not touched.</summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("IX-02")]
    public void DestroyAfterMigration_RemovesTransientEntries()
    {
        using var dbe = SetupEngine();
        var migrant = Spawn(dbe, 50f, 1, 7);
        var stays = Spawn(dbe, 51f, 2, 7);
        dbe.WriteTickFence(1);
        Cross(dbe, migrant);
        dbe.WriteTickFence(2);
        var newcomer = Spawn(dbe, 52f, 3, 7);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(migrant);
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(3);

        Assert.That(ByCode(dbe, 1), Is.Empty);
        Assert.That(ByGroup(dbe, 7), Is.EquivalentTo(new[] { stays, newcomer }));
    }

    /// <summary>A key changed in the tick of the crossing: the fence's move runs first, on the old slot, and the migration re-points what it left (IX-08).</summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("IX-02")]
    public void AKeyChangeInTheTickOfTheCrossing_LandsOnTheDestination([Values] bool parallel)
    {
        using var dbe = SetupEngine();
        var migrant = Spawn(dbe, 50f, 1, 7);
        var stays = Spawn(dbe, 51f, 2, 7);
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var e = tx.OpenMut(migrant);
            e.Set(TrMigUnit.Pos, At(550f, 750f));
            e.Set(TrMigUnit.Runtime, new TrMigRuntime { Code = 9, Group = 8 });
            Assert.That(tx.Commit(), Is.True);
        }

        Fence(dbe, parallel, 2);

        Assert.That(ByCode(dbe, 9), Is.EqualTo(new[] { migrant }));
        Assert.That(ByCode(dbe, 1), Is.Empty);
        Assert.That(ByGroup(dbe, 8), Is.EqualTo(new[] { migrant }));
        Assert.That(ByGroup(dbe, 7), Is.EqualTo(new[] { stays }));
    }

    /// <summary>An archetype whose Transient fields are the first of the staging's ids: no persistent field shifts them.</summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("IX-02")]
    public void ATransientOnlyIndexedArchetype_FollowsItsMigrants([Values] bool parallel)
    {
        using var dbe = SetupEngine();
        EntityId migrant;
        using (var tx = dbe.CreateQuickTransaction())
        {
            migrant = tx.Spawn<TrMigBareUnit>(TrMigBareUnit.Pos.Set(new TrMigBarePos { Bounds = At(50f, 50f).Bounds }),
                TrMigBareUnit.Runtime.Set(new TrMigRuntime { Code = 1, Group = 7 }));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(migrant).Set(TrMigBareUnit.Pos, new TrMigBarePos { Bounds = At(550f, 750f).Bounds });
            Assert.That(tx.Commit(), Is.True);
        }

        Fence(dbe, parallel, 2);

        QueryPathProbe.Forced = ClusterScanPath.Selective;
        try
        {
            using var read = dbe.CreateQuickTransaction();
            Assert.That(read.Query<TrMigBareUnit>().WhereField<TrMigRuntime>(r => r.Group == 7).Execute(), Is.EqualTo(new[] { migrant }));
            Assert.That(read.Query<TrMigBareUnit>().WhereField<TrMigRuntime>(r => r.Code == 1).Execute(), Is.EqualTo(new[] { migrant }));
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    /// <summary>The destination cluster's zone map sees the migrant's Transient value, or a zone-map scan prunes the cluster that holds it. The parallel fence writes
    /// through the slice's zone-map batch, by field id.</summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("IX-02")]
    public void Migration_WidensTheDestinationsTransientZoneMap([Values] bool parallel)
    {
        using var dbe = SetupEngine();
        var migrant = Spawn(dbe, 50f, 500, 7);
        dbe.WriteTickFence(1);
        Cross(dbe, migrant);
        Fence(dbe, parallel, 2);

        var cs = dbe._archetypeStates[Archetype<TrMigUnit>.Metadata.ArchetypeId].ClusterState;
        var chunk = cs.ActiveClusterIds[0];
        var zoneMap = cs.TransientIndexSlots.SelectMany(s => s.Fields).First(f => !f.AllowMultiple).ZoneMap;
        Assert.That(zoneMap.TryGetBounds(chunk, out var min, out var max), Is.True, "the map never saw the migrant");
        Assert.That(min <= 500 && max >= 500, Is.True, $"bounds [{min}, {max}] exclude the migrant's value");

        QueryPathProbe.Forced = ClusterScanPath.FullScan;
        try
        {
            Assert.That(ByCode(dbe, 500), Is.EqualTo(new[] { migrant }));
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }
}
