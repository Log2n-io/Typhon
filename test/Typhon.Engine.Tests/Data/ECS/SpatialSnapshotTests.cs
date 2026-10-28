using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.SSN.VPos", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct SsnVPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

/// <summary>A spatial archetype whose spatial field is itself Versioned: a move is an update an older snapshot must not see.</summary>
[Archetype]
partial class SsnVUnit : Archetype<SsnVUnit>
{
    public static readonly Comp<SsnVPos> Pos = Register<SsnVPos>();
}

/// <summary>
/// #1247: a spatial query answers at its snapshot (VIS-13, SQ-07) on every shape. The spatial index walks current occupancy and bounds: an older reader
/// lost an entity a destroy newer than its snapshot hid, and found an entity a newer update moved at its new place.
/// </summary>
[TestFixture]
[NonParallelizable]
class SpatialSnapshotTests : TestBase<SpatialSnapshotTests>
{
    // The box [0, 99]² as four half-planes n·p + d ≥ 0.
    private static readonly double[] Box99 = [1, 0, 0, -1, 0, 99, 0, 1, 0, 0, -1, 99];

    private DatabaseEngine Open()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<Vis12Pos>();
        dbe.RegisterComponentFromAccessor<Vis12Rank>();
        dbe.RegisterComponentFromAccessor<SsnVPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000, 1000), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static Vis12Pos At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static SsnVPos VAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static HashSet<EntityId> Box<TA>(Transaction tx) where TA : Archetype<TA>, new() =>
        typeof(TA) == typeof(SsnVUnit)
            ? tx.Query<SsnVUnit>().WhereInAABB<SsnVPos>(0, 0, -1, 99, 99, 1).Execute()
            : tx.Query<Vis12Unit>().WhereInAABB<Vis12Pos>(0, 0, -1, 99, 99, 1).Execute();

    [Test]
    [VerifiesRule("VIS-13")]
    public void EveryShape_FindsADestroyNewerThanTheSnapshot_AndNotALaterSpawn()
    {
        using var dbe = Open();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                ids.Add(tx.Spawn<Vis12Unit>(Vis12Unit.Pos.Set(At(20f + i * 5, 50f)), Vis12Unit.Rank.Set(new Vis12Rank { Tier = 5, Score = i })));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(0);
        using var reader = dbe.CreateQuickTransaction();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[9]);   // the extreme X: the fence's shrink moves the cluster box off it
            Assert.That(tx.Commit(), Is.True);
        }

        EntityId late;
        using (var tx = dbe.CreateQuickTransaction())
        {
            late = tx.Spawn<Vis12Unit>(Vis12Unit.Pos.Set(At(30f, 30f)), Vis12Unit.Rank.Set(new Vis12Rank { Tier = 5, Score = 99 }));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        Assert.That(Box<Vis12Unit>(reader), Is.EquivalentTo(ids), "AABB");
        Assert.That(reader.Query<Vis12Unit>().WhereNearby<Vis12Pos>(50, 50, 0, 49).Execute(), Is.EquivalentTo(ids), "radius");
        Assert.That(reader.Query<Vis12Unit>().WhereRay<Vis12Pos>(0, 50, 0, 1, 0, 0, 200).Execute(), Is.EquivalentTo(ids), "ray");
        Assert.That(reader.Query<Vis12Unit>().WhereFrustum<Vis12Pos>(Box99, 4, 0, 0, 0, 99, 99, 0).Execute(), Is.EquivalentTo(ids), "frustum");
        Assert.That(Box<Vis12Unit>(reader), Does.Not.Contain(late));
    }

    [Test]
    [VerifiesRule("VIS-13")]
    public void EveryShape_FindsAVersionedEntityWhereItsSnapshotHasIt_NotWhereANewerUpdateMovedIt()
    {
        using var dbe = Open();
        EntityId a, b;
        using (var tx = dbe.CreateQuickTransaction())
        {
            a = tx.Spawn<SsnVUnit>(SsnVUnit.Pos.Set(VAt(50, 50)));
            b = tx.Spawn<SsnVUnit>(SsnVUnit.Pos.Set(VAt(60, 50)));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(0);
        using var reader = dbe.CreateQuickTransaction();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(a).Set(SsnVUnit.Pos, VAt(550, 750));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        Assert.That(Box<SsnVUnit>(reader), Is.EquivalentTo(new[] { a, b }), "AABB: where the snapshot has it");
        Assert.That(reader.Query<SsnVUnit>().WhereInAABB<SsnVPos>(500, 700, -1, 599, 799, 1).Execute(), Is.Empty, "AABB: not where it moved");
        Assert.That(reader.Query<SsnVUnit>().WhereNearby<SsnVPos>(50, 50, 0, 5).Execute(), Is.EquivalentTo(new[] { a }), "radius");
        Assert.That(reader.Query<SsnVUnit>().WhereRay<SsnVPos>(0, 50, 0, 1, 0, 0, 200).Execute(), Is.EquivalentTo(new[] { a, b }), "ray");
        Assert.That(reader.Query<SsnVUnit>().WhereFrustum<SsnVPos>(Box99, 4, 0, 0, 0, 99, 99, 0).Execute(), Is.EquivalentTo(new[] { a, b }), "frustum");

        using var fresh = dbe.CreateQuickTransaction();
        Assert.That(Box<SsnVUnit>(fresh), Is.EquivalentTo(new[] { b }), "control: a reader after the move");
    }
}
