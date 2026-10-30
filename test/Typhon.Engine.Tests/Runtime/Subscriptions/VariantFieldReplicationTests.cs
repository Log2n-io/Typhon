using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

[Component("Typhon.Test.Proj.Variant", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjVariantValue
{
    public int Before;
    public Variant Value;
    public int After;
}

[Archetype]
class ProjVariantEntity : Archetype<ProjVariantEntity>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjVariantValue> Data = Register<ProjVariantValue>();
}

[TestFixture]
[NonParallelizable]
sealed class VariantFieldReplicationTests : TestBase<VariantFieldReplicationTests>
{
    private const string Profile = "world";

    [Test]
    public void AVariantReplicatesOnEnterAfterAnUpdateAndToALateJoiner()
    {
        var engine = ServiceProvider.GetRequiredService<DatabaseEngine>();
        engine.RegisterComponentFromAccessor<ProjBounds>();
        engine.RegisterComponentFromAccessor<ProjVariantValue>();
        engine.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(-ProjectionTestSchema.WorldExtentM, -ProjectionTestSchema.WorldExtentM),
            worldMax: new Vector2(ProjectionTestSchema.WorldExtentM, ProjectionTestSchema.WorldExtentM), cellSize: 256f));
        engine.InitializeArchetypes();

        EntityId id;
        using (var tx = engine.CreateQuickTransaction())
        {
            var bounds = new ProjBounds { Bounds = new AABB2F { MinX = 9.5f, MinY = -0.5f, MaxX = 10.5f, MaxY = 0.5f }, Speed = 1f };
            var data = new ProjVariantValue { Before = 101, Value = new Variant(42), After = 202 };
            id = tx.Spawn<ProjVariantEntity>(ProjVariantEntity.Bounds.Set(in bounds), ProjVariantEntity.Data.Set(in data));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(engine, subscriptions =>
        {
            subscriptions.Archetype<ProjVariantEntity>(a => a
                .Motion(ProjVariantEntity.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
                .Field(ProjVariantEntity.Data, x => x.Value, Codec.Str(63), name: "value", group: "value"));
            subscriptions.Profile(Profile, p => p.World().Of<ProjVariantEntity>());
        }, nameof(VariantFieldReplicationTests), replicationCellM: ProjectionTestSchema.ReplicationCellFor(0));
        harness.RunFence = true;
        var session = harness.OpenSessions(1, Profile)[0];
        harness.RunTick(1);
        Assert.That(harness.Deliver(session), Is.EqualTo(1));
        var archetype = harness.CatalogPlan.ArchetypeByName(nameof(ProjVariantEntity)).Idx;
        var netId = harness.NetIdOf(id);
        Assert.That(harness.Replica(session).Text(archetype, netId, "value"), Is.EqualTo("si:42"));

        using (var tx = engine.CreateQuickTransaction())
        {
            var accessor = tx.For<ProjVariantEntity>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009
                var values = cluster.GetSpan(ProjVariantEntity.Data);
#pragma warning restore TYPHON009
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (cluster.GetEntityId(slot) == id)
                    {
                        values[slot].Value = new Variant("ñandú", false);
                        harness.Subscriptions.Commands.Replicate(in cluster, slot);
                    }
                }
            }

            tx.Commit();
        }

        harness.RunTick(2);
        Assert.That(harness.Deliver(session), Is.EqualTo(1));
        Assert.That(harness.Replica(session).Text(archetype, netId, "value"), Is.EqualTo("st:ñandú"));

        using (var tx = engine.CreateQuickTransaction())
        {
            var entity = tx.OpenMut(id);
            var stored = entity.Read(ProjVariantEntity.Data);
            Assert.Multiple(() =>
            {
                Assert.That(stored.Value.AsString(), Is.EqualTo("ñandú"));
                Assert.That(stored.Before, Is.EqualTo(101));
                Assert.That(stored.After, Is.EqualTo(202));
            });
        }

        var lateJoiner = harness.OpenSessions(1, Profile)[0];
        harness.RunTick(3);
        harness.Deliver(session);
        Assert.That(harness.Deliver(lateJoiner), Is.EqualTo(1));
        Assert.That(harness.Replica(lateJoiner).Text(archetype, netId, "value"), Is.EqualTo("st:ñandú"));
    }
}
