using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>SQ-01</c> across a cell boundary: an entity filed in one cell whose box reaches into a query that covers only the next cell.
/// </summary>
/// <remarks>
/// <para>A cluster is filed by its entities' CENTRES, so an extended entity's box reaches past its home cell — by up to <c>ClusterReach</c>, or further for a
/// cluster named in <c>EscapedClusters</c>. Every
/// cell-walking query built its cell range from its own extent alone and never examined the home cell: the entity below lies 0.1 inside each query, and
/// AABB, radius, ray and frustum all missed it, while kNN — whose stopping rule already subtracted the overhang — found it. The frustum had a second copy of
/// the same mistake, rejecting a whole cell by classifying the cell's own box against the planes.</para>
/// <para>Found by <c>AabbClusterEnumeratorDrainTests</c>' oracle on a scattered population; this is its one-entity form.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]   // borrows ClCohUnit, and ArchetypeRegistry is process-global and unsynchronised across parallel fixtures (#720)
class ClusterOverhangTests : TestBase<ClusterOverhangTests>
{
    private const float CellSize = 100f;

    // Centre y = 798.1, so it is filed in cell row 7; the box reaches y = 802.296, into row 8.
    private static readonly AABB2F EntityBox = new() { MinX = 392.155f, MinY = 793.974f, MaxX = 400.477f, MaxY = 802.296f };

    // Every query starts here: 0.096 inside the entity's box, and entirely inside cell row 8.
    private const float QueryMinY = 802.2f;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;

    private DatabaseEngine SpawnStraddler(out long id)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1_000f, 1_000f), CellSize));
        dbe.InitializeArchetypes();

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClCohPos { Bounds = EntityBox, Mass = 1f };
            id = (long)tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(in pos)).RawValue;
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // The premise, so a passing test cannot mean the scenario quietly stopped straddling: the home cell is outside the query's own rows, and the
        // overhang the fence noted covers the reach.
        var grid = dbe.Realm0Grid;
        grid.WorldToCellCoords((EntityBox.MinX + EntityBox.MaxX) * 0.5, (EntityBox.MinY + EntityBox.MaxY) * 0.5, 0d, out _, out int homeRow, out _);
        grid.WorldToCellCoords(0d, QueryMinY, 0d, out _, out int queryRow, out _);
        Assert.That(homeRow, Is.LessThan(queryRow), "the entity must be filed in a row the queries do not cover");
        var spatial = ClusterStateOf(dbe).Realm0Spatial;
        Assert.That(Volatile.Read(ref spatial.ClusterReach) >= EntityBox.MaxY - ((homeRow + 1) * CellSize)
            || Volatile.Read(ref spatial.EscapedClusters).Count > 0,
            Is.True, "the reach covers the overhang, or the cluster is named");
        return dbe;
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void Aabb_FindsAnEntityOverhangingFromTheCellBelow_OnEveryDrain()
    {
        using var dbe = SpawnStraddler(out long id);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var box = new AABB2F { MinX = 300f, MinY = QueryMinY, MaxX = 500f, MaxY = 900f };

        var hits = new List<long>();
        var e = dbe.ClusterSpatialQuery<ClCohUnit>().AABB(in box);
        try
        {
            while (e.MoveNext())
            {
                hits.Add(unchecked((long)e.Current.Entity.RawValue));
            }
        }
        finally
        {
            e.Dispose();
        }

        var counted = dbe.ClusterSpatialQuery<ClCohUnit>().AABB(in box);
        int count;
        try
        {
            count = counted.Count();
        }
        finally
        {
            counted.Dispose();
        }

        var buffer = new ClusterSpatialQueryResult[4];
        var filled = dbe.ClusterSpatialQuery<ClCohUnit>().AABB(in box);
        int written;
        try
        {
            written = filled.Fill(buffer);
        }
        finally
        {
            filled.Dispose();
        }

        Assert.Multiple(() =>
        {
            Assert.That(hits, Is.EqualTo(new[] { id }), "MoveNext");
            Assert.That(count, Is.EqualTo(1), "Count");
            Assert.That(written, Is.EqualTo(1), "Fill");
            Assert.That(unchecked((long)buffer[0].Entity.RawValue), Is.EqualTo(id), "Fill");
        });
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void Radius_FindsAnEntityOverhangingFromTheCellBelow()
    {
        using var dbe = SpawnStraddler(out long id);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);

        // Nearest point of the box is (396, 802.296), 47.704 from the centre; the sphere's own extent starts at y = 802.2.
        var sphere = new BSphere2F { CenterX = 396f, CenterY = 850f, Radius = 47.8f };
        var hits = new List<long>();
        var e = dbe.ClusterSpatialQuery<ClCohUnit>().Radius(in sphere);
        try
        {
            while (e.MoveNext())
            {
                hits.Add(unchecked((long)e.Current.Entity.RawValue));
            }
        }
        finally
        {
            e.Dispose();
        }

        Assert.That(hits, Is.EqualTo(new[] { id }));
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void Ray_FindsAnEntityOverhangingFromTheCellBelow()
    {
        using var dbe = SpawnStraddler(out long id);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);

        // Horizontal, at y = 802.2: its bounding box is one row tall, and that row is not the entity's.
        var buffer = new (long entityId, double distance)[4];
        int n = ClusterStateOf(dbe).QueryRay(dbe.Realm0Grid, 300d, QueryMinY, 0d, 1d, 0d, 0d, 200d, buffer, categoryMask: 0);

        Assert.That(n, Is.EqualTo(1));
        Assert.That(buffer[0].entityId, Is.EqualTo(id));
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void Frustum_FindsAnEntityOverhangingFromTheCellBelow()
    {
        using var dbe = SpawnStraddler(out long id);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);

        // Inside is dot(n, p) + d >= 0. The home cell's own box, y 700..800, is entirely behind the y >= 802.2 plane — the cell-level rejection has to use
        // the box its clusters can reach, not the cell itself, or the widened cell range is walked and then thrown away.
        double[] planes =
        [
            1d, 0d, -300d,
            -1d, 0d, 500d,
            0d, 1d, -QueryMinY,
            0d, -1d, 900d,
        ];
        var buffer = new long[4];
        int n = ClusterStateOf(dbe).QueryFrustum(dbe.Realm0Grid, planes, 4, new Vector3Like(300d, QueryMinY, 0d), new Vector3Like(500d, 900d, 0d), buffer,
            categoryMask: 0);

        Assert.That(n, Is.EqualTo(1));
        Assert.That(buffer[0], Is.EqualTo(id));
    }
}
