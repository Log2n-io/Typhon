using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Tests: 3D cluster spatial queries — Z-axis filtering in narrowphase and
// broadphase. Exercises the _is3D=true branch in AabbClusterEnumerator.MoveNext()
// and the Union3F path in ClusterSpatialAabb / CellSpatialIndex.
//
// Reuses ClSpatialPos (AABB3F) and ClSpatialMeta from ClusterSpatialTests.cs.
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
class ClusterSpatial3DTests : TestBase<ClusterSpatial3DTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClSpatialPos>();
        dbe.RegisterComponentFromAccessor<ClSpatialMeta>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(-10_000, -10_000),
            worldMax: new Vector2(10_000, 10_000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ClSpatialPos MakePos(float x, float y, float z, float size = 1.0f) =>
        new() { Bounds = new AABB3F { MinX = x - size, MinY = y - size, MinZ = z - size, MaxX = x + size, MaxY = y + size, MaxZ = z + size } };

    // ═══════════════════════════════════════════════════════════════════════
    // Z-axis filtering — AABB queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void QueryAabb_SameXY_DifferentZ_FiltersCorrectly()
    {
        using var dbe = SetupEngine();

        EntityId idLow, idMid, idHigh;
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 1 };

            // Three entities at the same XY (50,50) but different Z heights
            var posLow = MakePos(50, 50, 10);   // Z: [9, 11]
            var posMid = MakePos(50, 50, 100);  // Z: [99, 101]
            var posHigh = MakePos(50, 50, 500); // Z: [499, 501]

            idLow = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posLow), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            idMid = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posMid), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 3;
            idHigh = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posHigh), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Query a box that covers the XY range of all three but only Z=[0, 50]
        // Should find only idLow (Z: [9,11])
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 100, 100, 50).Execute();
            Assert.That(results, Does.Contain(idLow), "Entity at Z=10 should be within Z=[0,50]");
            Assert.That(results, Does.Not.Contain(idMid), "Entity at Z=100 should NOT be within Z=[0,50]");
            Assert.That(results, Does.Not.Contain(idHigh), "Entity at Z=500 should NOT be within Z=[0,50]");
        }
    }

    [Test]
    public void QueryAabb_ZRangeSelectsMiddleTier()
    {
        using var dbe = SetupEngine();

        EntityId idLow, idMid, idHigh;
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 1 };

            var posLow = MakePos(50, 50, 10);   // Z: [9, 11]
            var posMid = MakePos(50, 50, 100);  // Z: [99, 101]
            var posHigh = MakePos(50, 50, 500); // Z: [499, 501]

            idLow = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posLow), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            idMid = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posMid), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 3;
            idHigh = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posHigh), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Query Z=[80, 120] — should find only idMid
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 80, 100, 100, 120).Execute();
            Assert.That(results, Does.Not.Contain(idLow), "Entity at Z=10 should NOT be within Z=[80,120]");
            Assert.That(results, Does.Contain(idMid), "Entity at Z=100 should be within Z=[80,120]");
            Assert.That(results, Does.Not.Contain(idHigh), "Entity at Z=500 should NOT be within Z=[80,120]");
        }
    }

    [Test]
    public void QueryAabb_ZRangeExcludesAll()
    {
        using var dbe = SetupEngine();

        EntityId idA, idB;
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 1 };

            // Entities at Z=10 and Z=100
            var posA = MakePos(50, 50, 10);   // Z: [9, 11]
            var posB = MakePos(50, 50, 100);  // Z: [99, 101]

            idA = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posA), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            idB = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posB), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Query Z=[200, 300] — should find nothing despite matching XY
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 200, 100, 100, 300).Execute();
            Assert.That(results.Count, Is.EqualTo(0), "No entities should match when query Z range [200,300] misses all");
        }
    }

    [Test]
    public void QueryAabb_ZRangeIncludesAll()
    {
        using var dbe = SetupEngine();

        EntityId idLow, idMid, idHigh;
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 1 };

            var posLow = MakePos(50, 50, 10);
            var posMid = MakePos(50, 50, 100);
            var posHigh = MakePos(50, 50, 500);

            idLow = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posLow), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            idMid = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posMid), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 3;
            idHigh = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posHigh), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Query with Z covering all entities
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, -1000, 100, 100, 1000).Execute();
            Assert.That(results.Count, Is.EqualTo(3), "All three entities should be found when Z range covers them all");
            Assert.That(results, Does.Contain(idLow));
            Assert.That(results, Does.Contain(idMid));
            Assert.That(results, Does.Contain(idHigh));
        }
    }

    [Test]
    public void QueryAabb_NegativeZ_FiltersCorrectly()
    {
        using var dbe = SetupEngine();

        EntityId idNeg, idPos;
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 1 };

            var posNeg = MakePos(50, 50, -200); // Z: [-201, -199]
            var posPos = MakePos(50, 50, 200);  // Z: [199, 201]

            idNeg = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posNeg), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            idPos = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posPos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Query only negative Z range
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, -300, 100, 100, -100).Execute();
            Assert.That(results, Does.Contain(idNeg), "Entity at Z=-200 should be in Z=[-300,-100]");
            Assert.That(results, Does.Not.Contain(idPos), "Entity at Z=200 should NOT be in Z=[-300,-100]");
        }

        // Query only positive Z range
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 100, 100, 100, 300).Execute();
            Assert.That(results, Does.Not.Contain(idNeg), "Entity at Z=-200 should NOT be in Z=[100,300]");
            Assert.That(results, Does.Contain(idPos), "Entity at Z=200 should be in Z=[100,300]");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Z-axis filtering — Radius queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void QueryRadius_SameXY_DifferentZ_FiltersCorrectly()
    {
        using var dbe = SetupEngine();

        EntityId idClose, idFar;
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 1 };

            // Both at XY=(50,50), but at Z=10 and Z=500
            var posClose = MakePos(50, 50, 10);  // Z: [9, 11]
            var posFar = MakePos(50, 50, 500);   // Z: [499, 501]

            idClose = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posClose), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            idFar = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posFar), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Radius query centered at (50, 50, 10) with radius 20 — should find idClose but not idFar
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereNearby<ClSpatialPos>(50, 50, 10, 20).Execute();
            Assert.That(results, Does.Contain(idClose), "Entity at Z=10 should be within radius 20 of center Z=10");
            Assert.That(results, Does.Not.Contain(idFar), "Entity at Z=500 should NOT be within radius 20 of center Z=10");
        }
    }

    [Test]
    public void QueryRadius_ZDistanceDominates_ExcludesNearbyXY()
    {
        using var dbe = SetupEngine();

        EntityId idNearXYFarZ, idFarXYNearZ;
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 1 };

            // Entity A: very close in XY but far in Z
            var posA = MakePos(51, 51, 300); // XY distance ~1.4, Z distance ~290
            // Entity B: farther in XY but close in Z
            var posB = MakePos(70, 70, 12);  // XY distance ~28, Z distance ~2

            idNearXYFarZ = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posA), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            idFarXYNearZ = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posB), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Radius 50 centered at (50, 50, 10) — Entity A is ~290 away in Z, Entity B is ~30 away total
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereNearby<ClSpatialPos>(50, 50, 10, 50).Execute();
            Assert.That(results, Does.Not.Contain(idNearXYFarZ),
                "Entity close in XY but Z=300 should NOT be within radius 50 of center Z=10");
            Assert.That(results, Does.Contain(idFarXYNearZ),
                "Entity far in XY but Z=12 should be within radius 50 of center Z=10");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Z-axis precision — entity bounds overlap at Z boundary
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void QueryAabb_ZBoundaryOverlap_IncludesEntity()
    {
        using var dbe = SetupEngine();

        EntityId id;
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 1 };

            // Entity with tight AABB Z: [99, 101]
            var pos = MakePos(50, 50, 100);
            id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Query Z=[0, 100] — entity Z range [99,101] overlaps at Z=99..100
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 100, 100, 100).Execute();
            Assert.That(results, Does.Contain(id), "Entity at Z=100 (bounds [99,101]) should overlap with query MaxZ=100");
        }

        // Query Z=[0, 98] — entity Z range [99,101] does NOT overlap
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 100, 100, 98).Execute();
            Assert.That(results, Does.Not.Contain(id), "Entity at Z=100 (bounds [99,101]) should NOT overlap with query MaxZ=98");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tick fence with Z-axis movement
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TickFence_MoveAlongZAxis_SpatialIndexUpdated()
    {
        using var dbe = SetupEngine();

        EntityId id;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = MakePos(50, 50, 10);
            var met = new ClSpatialMeta { Tag = 1 };
            id = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Move the entity to Z=500 (same XY) — escapes fat AABB margin in Z
        {
            using var tx = dbe.CreateQuickTransaction();
            var eref = tx.OpenMut(id);
            ref var pos = ref eref.Write(ClSpatialUnit.Pos);
            pos = MakePos(50, 50, 500);
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Should be findable at new Z
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 450, 100, 100, 550).Execute();
            Assert.That(results, Does.Contain(id), "Entity should be queryable at Z=500 after tick fence");
        }

        // Should NOT be at old Z
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 0, 100, 100, 50).Execute();
            Assert.That(results, Does.Not.Contain(id), "Entity should NOT be at old Z=10 after tick fence");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Union3F path in CellSpatialIndex — Z bounds stored correctly
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void CellSpatialIndex_ZBoundsStoredViaUnion3F()
    {
        using var dbe = SetupEngine();

        // Spawn entities with well-separated Z values in the same cell
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 1 };

            // All at XY=(50,50) in the same cell — Z values: -100, 0, 100
            var posA = MakePos(50, 50, -100); // Z: [-101, -99]
            var posB = MakePos(50, 50, 0);    // Z: [-1, 1]
            var posC = MakePos(50, 50, 100);  // Z: [99, 101]

            tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posA), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posB), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 3;
            tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posC), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Verify the per-cell spatial index has the correct Z bounds by probing with narrow Z queries.
        // If Union3F did NOT store Z bounds, the broadphase would use sentinel values (+inf/-inf) which
        // would cause all queries to pass broadphase regardless of Z, but narrowphase would still filter.
        // We verify narrowphase Z filtering works by checking that a Z-miss query returns nothing.
        {
            using var tx = dbe.CreateQuickTransaction();

            // This Z range [200, 300] doesn't overlap any entity's Z
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 200, 100, 100, 300).Execute();
            Assert.That(results.Count, Is.EqualTo(0), "Z query [200,300] should return no entities");

            // This Z range [-150, -50] should find only entity A
            var resultsNeg = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, -150, 100, 100, -50).Execute();
            Assert.That(resultsNeg.Count, Is.EqualTo(1), "Z query [-150,-50] should find exactly 1 entity at Z=-100");

            // This Z range [-150, 150] should find all three
            var resultsAll = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, -150, 100, 100, 150).Execute();
            Assert.That(resultsAll.Count, Is.EqualTo(3), "Z query [-150,150] should find all 3 entities");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Bulk Z-separation — many entities at distinct Z layers
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void QueryAabb_ManyZLayers_SelectsCorrectLayer()
    {
        using var dbe = SetupEngine();
        var ids = new EntityId[10];

        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 0 };

            // 10 entities at same XY but Z = 0, 100, 200, ..., 900
            for (int i = 0; i < 10; i++)
            {
                var pos = MakePos(50, 50, i * 100);
                met.Tag = i;
                ids[i] = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
            }
            tx.Commit();
        }

        // Query Z=[350, 550] — should find entities at Z=400 and Z=500 (indices 4 and 5)
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 350, 100, 100, 550).Execute();
            Assert.That(results.Count, Is.EqualTo(2), "Should find exactly 2 entities in Z=[350,550]");
            Assert.That(results, Does.Contain(ids[4]), "Entity at Z=400 should be in range");
            Assert.That(results, Does.Contain(ids[5]), "Entity at Z=500 should be in range");
            Assert.That(results, Does.Not.Contain(ids[3]), "Entity at Z=300 should NOT be in range");
            Assert.That(results, Does.Not.Contain(ids[6]), "Entity at Z=600 should NOT be in range");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3D box query — combined XY + Z filtering
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void QueryAabb_3DGrid_SelectsByAllAxes()
    {
        using var dbe = SetupEngine();

        // Spawn a 3x3x3 grid of entities at positions (0,0,0), (0,0,100), ..., (200,200,200)
        var ids = new EntityId[27];
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 0 };
            int idx = 0;
            for (int x = 0; x < 3; x++)
            {
                for (int y = 0; y < 3; y++)
                {
                    for (int z = 0; z < 3; z++)
                    {
                        var pos = MakePos(x * 100, y * 100, z * 100, 2.0f);
                        met.Tag = idx;
                        ids[idx] = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in pos), ClSpatialUnit.Meta.Set(in met));
                        idx++;
                    }
                }
            }
            tx.Commit();
        }

        // Query a tight box around (100, 100, 100) — should find only 1 entity
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(90, 90, 90, 110, 110, 110).Execute();
            // Entity at (100,100,100) is index 1*9 + 1*3 + 1 = 13
            Assert.That(results.Count, Is.EqualTo(1), "Tight 3D box should find exactly 1 entity");
            Assert.That(results, Does.Contain(ids[13]), "Entity at (100,100,100) should be in tight box");
        }

        // Query bottom plane only: Z=[−10, 10] — should find all 9 entities at Z=0
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(-10, -10, -10, 210, 210, 10).Execute();
            Assert.That(results.Count, Is.EqualTo(9), "Bottom Z plane should contain 9 entities");
        }

        // Query top-right corner: X=[190,210], Y=[190,210], Z=[190,210] — should find 1 entity at (200,200,200)
        {
            using var tx = dbe.CreateQuickTransaction();
            var results = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(190, 190, 190, 210, 210, 210).Execute();
            Assert.That(results.Count, Is.EqualTo(1), "Top-right corner should find exactly 1 entity");
            Assert.That(results, Does.Contain(ids[26]), "Entity at (200,200,200) should be at index 26");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Destroy at specific Z — verifies removal doesn't break Z filtering
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_EntityAtSpecificZ_OtherZLayersUnaffected()
    {
        using var dbe = SetupEngine();

        EntityId idLow, idMid, idHigh;
        {
            using var tx = dbe.CreateQuickTransaction();
            var met = new ClSpatialMeta { Tag = 1 };

            var posLow = MakePos(50, 50, 0);    // Z: [-1, 1]
            var posMid = MakePos(50, 50, 100);  // Z: [99, 101]
            var posHigh = MakePos(50, 50, 200); // Z: [199, 201]

            idLow = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posLow), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 2;
            idMid = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posMid), ClSpatialUnit.Meta.Set(in met));
            met.Tag = 3;
            idHigh = tx.Spawn<ClSpatialUnit>(ClSpatialUnit.Pos.Set(in posHigh), ClSpatialUnit.Meta.Set(in met));
            tx.Commit();
        }

        // Destroy the middle entity
        {
            using var tx = dbe.CreateQuickTransaction();
            tx.Destroy(idMid);
            tx.Commit();
        }

        // Low and high should still be queryable at their respective Z ranges
        {
            using var tx = dbe.CreateQuickTransaction();
            var resultsLow = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, -10, 100, 100, 10).Execute();
            Assert.That(resultsLow, Does.Contain(idLow), "Low-Z entity should still be queryable after destroying mid-Z entity");
        }
        {
            using var tx = dbe.CreateQuickTransaction();
            var resultsHigh = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 190, 100, 100, 210).Execute();
            Assert.That(resultsHigh, Does.Contain(idHigh), "High-Z entity should still be queryable after destroying mid-Z entity");
        }
        {
            using var tx = dbe.CreateQuickTransaction();
            var resultsMid = tx.Query<ClSpatialUnit>().WhereInAABB<ClSpatialPos>(0, 0, 80, 100, 100, 120).Execute();
            Assert.That(resultsMid, Does.Not.Contain(idMid), "Destroyed mid-Z entity should not be queryable");
        }
    }
}
