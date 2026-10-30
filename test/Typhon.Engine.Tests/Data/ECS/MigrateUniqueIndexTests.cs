using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.MigUq.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MigUqPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [Index]
    public int Key;
}

[Archetype]
partial class MigUqUnit : Archetype<MigUqUnit>
{
    public static readonly Comp<MigUqPos> Pos = Register<MigUqPos>();
}

/// <summary>
/// #1348: Migrate repoints a unique index entry at the migrant's new location under the key its data holds. When the tree holds that key for another
/// entity — the winner of a unique collision, the migrant being the loser that is not indexed under it — the repoint must leave the holder's entry alone.
/// </summary>
[TestFixture]
class MigrateUniqueIndexTests : TestBase<MigrateUniqueIndexTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<MigUqPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000, 1000), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static MigUqPos At(float x, float y, int key) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Key = key };

    private static unsafe int? Indexed(DatabaseEngine dbe, int key)
    {
        var cs = dbe._archetypeStates[Archetype<MigUqUnit>.Metadata.ArchetypeId].ClusterState;
        ref var field = ref cs.IndexSlots[0].Fields[0];
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = field.Index.Segment.CreateChunkAccessor();
        try
        {
            var result = field.Index.TryGet(&key, ref accessor);
            return result.IsSuccess ? result.Value : null;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private static int ClusterOf(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<MigUqUnit>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var ids = cluster.EntityIds;
            for (var slot = 0; slot < cluster.ClusterSize; slot++)
            {
                if ((cluster.OccupancyBits & (1UL << slot)) != 0 && ids[slot] == (long)id.RawValue)
                {
                    return cluster.ChunkId;
                }
            }
        }

        Assert.Fail($"{id} is in no cluster");
        return -1;
    }

    [Test]
    [VerifiesRule("IX-08")]
    public void TheLoserOfACollisionMigrates_TheHolderKeepsItsEntry()
    {
        using var dbe = SetupEngine();
        EntityId holder, loser;
        using (var tx = dbe.CreateQuickTransaction())
        {
            holder = tx.Spawn<MigUqUnit>(MigUqUnit.Pos.Set(At(50f, 50f, 100)));
            loser = tx.Spawn<MigUqUnit>(MigUqUnit.Pos.Set(At(60f, 50f, 200)));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(0);
        var holderLocation = Indexed(dbe, 100);
        Assert.That(holderLocation, Is.Not.Null, "premise: the holder is indexed under 100");

        // The loser's data takes the holder's key: the fence refuses the move and raises the collision once its drain is done.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(loser).Set(MigUqUnit.Pos, At(60f, 50f, 100));
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.Catch<Exception>(() => dbe.WriteTickFence(1), "premise: the collision is raised");
        Assert.That(Indexed(dbe, 100), Is.EqualTo(holderLocation), "premise: the tree still names the holder under 100");

        // The loser moves to another cell: Migrate relocates it and stages its index update under the key its data holds, 100.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(loser).Set(MigUqUnit.Pos, At(550f, 750f, 100));
            Assert.That(tx.Commit(), Is.True);
        }

        var loserCluster = ClusterOf(dbe, loser);
        dbe.WriteTickFence(2);   // no key changed this tick, so no collision to raise
        Assert.That(ClusterOf(dbe, loser), Is.Not.EqualTo(loserCluster), "premise: the loser migrated");
        Assert.That(Indexed(dbe, 100), Is.EqualTo(holderLocation), "the holder's entry, after the loser migrated");
        using var check = dbe.CreateQuickTransaction();
        Assert.That(check.Query<MigUqUnit>().WhereField<MigUqPos>(p => p.Key == 100).Execute(), Does.Contain(holder));
    }
}
