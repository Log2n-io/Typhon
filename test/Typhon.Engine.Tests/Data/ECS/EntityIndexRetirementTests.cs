using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// #872 step 13 — retiring the entity-level spatial index.
//
// Two 3D archetypes, because the shapes this step wires up (ray, frustum) are the ones a 2D fixture cannot distinguish
// from a slab query. The 2D half rides on ClCohUnit, which already exists.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Retire.Pos3", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct RetirePos3
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;
}

[Archetype]
partial class RetireUnit3 : Archetype<RetireUnit3>
{
    public static readonly Comp<RetirePos3> Pos = Register<RetirePos3>();
}

/// <summary>
/// <c>AC-13.1</c> / <c>AC-13.3</c> / <c>AC-13.4</c> / <c>AC-13.6</c> — the query surface after the entity-level R-Tree's removal.
/// </summary>
/// <remarks>
/// <para><b>What this step actually risks.</b> Removing an index home is only safe if nothing depended on it, and the only proof of that is that every query
/// shape still answers identically. Two of them — ray and frustum — did not previously reach the cluster tier at all: <c>EcsQuery</c> threw
/// <see cref="NotSupportedException"/> for anything past AABB and Radius, so the entity tree was the sole implementation of <c>WhereRay</c> and there was no
/// frustum entry point. Wiring those in is what makes deleting the tree a removal rather than a regression, and it is what these tests check first.</para>
/// <para><b>Every oracle here is a brute-force scan of the entities' own component data</b>, read back through the public query API, with the predicate
/// written independently of the production one. Comparing against the production predicate would only prove the two agree, which is exactly what a shared bug
/// guarantees.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class EntityIndexRetirementTests : TestBase<EntityIndexRetirementTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float WorldExtent = 1_000f;
    private const float CellSize = 100f;

    // ── 3D harness ──────────────────────────────────────────────────────

    private DatabaseEngine Setup3D(IServiceScope scope, int promoteThreshold = int.MaxValue)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RetirePos3>();

        // The three-argument constructor, NOT SpatialGridConfig.Flat. Flat() sets worldMin.Z = 0 and worldMax.Z = cellSize — a grid ONE cell layer thick
        // — which is right for a 2D archetype and silently wrong for a 3D one: entities above z = cellSize land outside the grid, and the resulting misses
        // read as query bugs. That mistake cost this fixture its first frustum run.
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            new Vector3(0, 0, 0), new Vector3(WorldExtent, WorldExtent, WorldExtent), CellSize));
        dbe.ClusterCellTreePromoteThreshold = promoteThreshold;
        // Step 16: these fixtures exercise the TREE, not the gate that decides when a cell gets one — their clusters are scattered over the cell
        // on purpose, which is exactly the shape the tightness gate refuses. Count-only promotion keeps them testing what they were written for.
        dbe.ClusterCellTreePromoteTightness = 1f;
        dbe.InitializeArchetypes();
        return dbe;
    }

    private DatabaseEngine Setup2D(IServiceScope scope, int promoteThreshold = int.MaxValue)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0), new Vector2(WorldExtent, WorldExtent), CellSize));
        dbe.ClusterCellTreePromoteThreshold = promoteThreshold;
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>One box per entity, keyed by entity id — the oracle's whole world model.</summary>
    private static Dictionary<long, (float minX, float minY, float minZ, float maxX, float maxY, float maxZ)> Populate3D(
        DatabaseEngine dbe, int count, int seed)
    {
        var rng = new Random(seed);
        var boxes = new Dictionary<long, (float, float, float, float, float, float)>(count);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                var cx = (float)(rng.NextDouble() * WorldExtent);
                var cy = (float)(rng.NextDouble() * WorldExtent);
                var cz = (float)(rng.NextDouble() * WorldExtent);
                var half = 1f + ((float)rng.NextDouble() * 5f);
                var pos = new RetirePos3
                {
                    Bounds = new AABB3F
                    {
                        MinX = cx - half, MinY = cy - half, MinZ = cz - half,
                        MaxX = cx + half, MaxY = cy + half, MaxZ = cz + half,
                    },
                };
                var id = tx.Spawn<RetireUnit3>(RetireUnit3.Pos.Set(in pos));
                boxes[(long)id.RawValue] = (cx - half, cy - half, cz - half, cx + half, cy + half, cz + half);
            }
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return boxes;
    }

    private static HashSet<long> RawIds(IEnumerable<EntityId> ids)
    {
        var set = new HashSet<long>();
        foreach (var id in ids)
        {
            set.Add((long)id.RawValue);
        }
        return set;
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC-13.1 — ray
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-13.1</c> — <c>WhereRay</c> over a cluster-backed archetype returns exactly the entities an independent slab test says the segment enters.
    /// </summary>
    /// <remarks>
    /// <b>This threw before the step.</b> <c>EcsQuery.ExecuteSpatial</c> raised <see cref="NotSupportedException"/> for every shape past AABB and Radius on
    /// the cluster tier, and since #666 made every archetype cluster-backed there was no other tier — so <c>WhereRay</c> was unreachable from user code
    /// against any real archetype. A test asserting "no false negatives" would have passed vacuously on an exception, hence the explicit non-empty guard.
    /// </remarks>
    [Test]
    [VerifiesRule("SH-01")]
    [VerifiesRule("SQ-01")]
    [TestCase(int.MaxValue, TestName = "Ray_MatchesBruteForce(unpromoted)")]
    [TestCase(1, TestName = "Ray_MatchesBruteForce(promoted)")]
    public void Ray_MatchesBruteForce(int promoteThreshold)
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup3D(scope, promoteThreshold);
        var boxes = Populate3D(dbe, 400, seed: 9001);

        // Rays are AIMED, not random. A line has zero volume: 400 boxes of ~7 units across in a 1 000³ world give an expected hit count near 0.01 for a
        // uniformly random ray, so a sweep of a dozen of them hits nothing and the oracle comparison passes on two empty sets. Each ray here is fired at a
        // randomly chosen entity's centre from a random origin, which guarantees at least one true hit per query while leaving everything else it crosses
        // for the oracle to find.
        var targets = boxes.Values.ToList();
        var rng = new Random(4242);
        var totalHits = 0;

        for (var q = 0; q < 12; q++)
        {
            var target = targets[rng.Next(targets.Count)];
            double tx0 = (target.minX + target.maxX) / 2d;
            double ty0 = (target.minY + target.maxY) / 2d;
            double tz0 = (target.minZ + target.maxZ) / 2d;

            double ox = rng.NextDouble() * WorldExtent;
            double oy = rng.NextDouble() * WorldExtent;
            double oz = rng.NextDouble() * WorldExtent;
            double dx = tx0 - ox;
            double dy = ty0 - oy;
            double dz = tz0 - oz;
            var len = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (len < 1e-6)
            {
                continue;
            }
            dx /= len; dy /= len; dz /= len;
            double maxDist = len + 50;

            HashSet<long> actual;
            using (var tx = dbe.CreateQuickTransaction())
            {
                actual = RawIds(tx.Query<RetireUnit3>().WhereRay<RetirePos3>(ox, oy, oz, dx, dy, dz, maxDist).Execute());
            }

            var expected = new HashSet<long>();
            foreach (var (id, b) in boxes)
            {
                if (OracleRayHitsBox(ox, oy, oz, dx, dy, dz, maxDist, b))
                {
                    expected.Add(id);
                }
            }

            totalHits += expected.Count;
            AssertSetsMatch(expected, actual, $"ray {q} from ({ox:F1},{oy:F1},{oz:F1}) dir ({dx:F3},{dy:F3},{dz:F3}) len {maxDist:F1}");
        }

        Assert.That(totalHits, Is.GreaterThan(0),
            "no ray in the sweep hit anything, so agreeing with the oracle proves only that both returned nothing");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC-13.1 — frustum
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-13.1</c> — <c>WhereFrustum</c> over a cluster-backed archetype returns exactly the entities an independent half-space test does not reject.
    /// </summary>
    /// <remarks>
    /// <para>The regions are AXIS-ALIGNED boxes expressed as six planes, which makes the oracle an overlap test rather than a second plane classifier — a
    /// genuinely independent predicate rather than a transcription of the production one. A box is the one convex region whose plane form and whose extent
    /// form can be compared without either being derived from the other.</para>
    /// <para><b>Frustum semantics are "not fully outside", not "fully inside".</b> A broadphase that returned only entities strictly inside every plane would
    /// silently drop everything straddling an edge — which for a camera is most of what is on screen. The oracle is written the same way, as an overlap, so
    /// a production change to containment reddens this rather than passing.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("SH-01")]
    [VerifiesRule("SQ-01")]
    [TestCase(int.MaxValue, TestName = "Frustum_MatchesBruteForce(unpromoted)")]
    [TestCase(1, TestName = "Frustum_MatchesBruteForce(promoted)")]
    public void Frustum_MatchesBruteForce(int promoteThreshold)
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup3D(scope, promoteThreshold);
        var boxes = Populate3D(dbe, 400, seed: 9002);

        var rng = new Random(777);
        var totalHits = 0;

        for (var q = 0; q < 12; q++)
        {
            double minX = rng.NextDouble() * (WorldExtent - 200);
            double minY = rng.NextDouble() * (WorldExtent - 200);
            double minZ = rng.NextDouble() * (WorldExtent - 200);
            double sizeX = 50 + (rng.NextDouble() * 250);
            double sizeY = 50 + (rng.NextDouble() * 250);
            double sizeZ = 50 + (rng.NextDouble() * 250);
            double maxX = minX + sizeX, maxY = minY + sizeY, maxZ = minZ + sizeZ;

            // Six inward half-spaces: dot(n, p) + d >= 0 inside.
            var planes = new double[]
            {
                +1, 0, 0, -minX,
                -1, 0, 0, +maxX,
                0, +1, 0, -minY,
                0, -1, 0, +maxY,
                0, 0, +1, -minZ,
                0, 0, -1, +maxZ,
            };

            HashSet<long> actual;
            using (var tx = dbe.CreateQuickTransaction())
            {
                actual = RawIds(tx.Query<RetireUnit3>()
                    .WhereFrustum<RetirePos3>(planes, 6, minX, minY, minZ, maxX, maxY, maxZ)
                    .Execute());
            }

            var expected = new HashSet<long>();
            foreach (var (id, b) in boxes)
            {
                // Independent predicate: box-box overlap, never a plane classification.
                if (b.maxX >= minX && b.minX <= maxX && b.maxY >= minY && b.minY <= maxY && b.maxZ >= minZ && b.minZ <= maxZ)
                {
                    expected.Add(id);
                }
            }

            totalHits += expected.Count;
            AssertSetsMatch(expected, actual, $"frustum {q} box ({minX:F1},{minY:F1},{minZ:F1})-({maxX:F1},{maxY:F1},{maxZ:F1})");
        }

        Assert.That(totalHits, Is.GreaterThan(0), "no frustum in the sweep contained anything");
    }

    /// <summary>
    /// A 2D archetype takes THREE doubles per plane, and passing four is rejected rather than silently misread.
    /// </summary>
    /// <remarks>
    /// The packing is dimension-dependent, so the same <c>double[]</c> means different planes to a 2D and a 3D component. Reading a 3D-packed array as 2D
    /// finds each plane's <c>normalZ</c> where its distance belongs, which classifies against a plane nobody asked for and quietly loses rows — the exact
    /// <c>SQ-01</c> false negative this step exists to avoid creating.
    /// </remarks>
    [Test]
    public void Frustum_WithWrongPlaneStrideForTheDimension_IsRejected()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup2D(scope);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new ClCohPos { Bounds = new AABB2F { MinX = 10, MinY = 10, MaxX = 20, MaxY = 20 }, Mass = 1f };
            tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(in pos));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Two planes' worth of 2D data (3 doubles each = 6) offered as four planes, which needs 12.
        var tooShort = new double[] { 1, 0, 0, -1, 0, 100 };

        using var queryTx = dbe.CreateQuickTransaction();
        Assert.Throws<ArgumentException>(
            () => queryTx.Query<ClCohUnit>().WhereFrustum<ClCohPos>(tooShort, 4, 0, 0, 0, 100, 100, 0).Execute(),
            "a plane array too short for the component's dimension must be refused, not read past or truncated");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC-13.6 — the whole query surface, before-and-after equivalent
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-13.6</c> — AABB, radius, ray and frustum all answer identically to a brute-force oracle over the same population and seeds.
    /// </summary>
    /// <remarks>
    /// <para>The literal "before and after" the criterion asks for cannot be run in one process — the removed path no longer exists to compare against — so
    /// the equivalence is established against an oracle instead, which is strictly stronger: it would also catch a shape where BOTH implementations were
    /// wrong the same way.</para>
    /// <para><b>Each shape is asserted non-empty in its own right.</b> The failure mode this guards is a shape silently returning nothing after losing its
    /// only implementation, and a set comparison agrees enthusiastically when both sides are empty.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("SH-01")]
    [VerifiesRule("SQ-01")]
    public void EveryQueryShape_AgreesWithBruteForce()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup3D(scope);
        var boxes = Populate3D(dbe, 500, seed: 31337);

        // ── AABB ──
        const double aMinX = 200, aMinY = 200, aMinZ = 200, aMaxX = 600, aMaxY = 600, aMaxZ = 600;
        HashSet<long> aabbActual;
        using (var tx = dbe.CreateQuickTransaction())
        {
            aabbActual = RawIds(tx.Query<RetireUnit3>().WhereInAABB<RetirePos3>(aMinX, aMinY, aMinZ, aMaxX, aMaxY, aMaxZ).Execute());
        }
        var aabbExpected = new HashSet<long>();
        foreach (var (id, b) in boxes)
        {
            if (b.maxX >= aMinX && b.minX <= aMaxX && b.maxY >= aMinY && b.minY <= aMaxY && b.maxZ >= aMinZ && b.minZ <= aMaxZ)
            {
                aabbExpected.Add(id);
            }
        }

        // ── Radius ──
        const double cX = 500, cY = 500, cZ = 500, radius = 220;
        HashSet<long> radiusActual;
        using (var tx = dbe.CreateQuickTransaction())
        {
            radiusActual = RawIds(tx.Query<RetireUnit3>().WhereNearby<RetirePos3>(cX, cY, cZ, radius).Execute());
        }
        var radiusExpected = new HashSet<long>();
        foreach (var (id, b) in boxes)
        {
            // Closest point on the box to the centre, squared distance against r² — the standard sphere-AABB overlap test.
            var qx = Math.Clamp(cX, b.minX, b.maxX);
            var qy = Math.Clamp(cY, b.minY, b.maxY);
            var qz = Math.Clamp(cZ, b.minZ, b.maxZ);
            var dsq = ((cX - qx) * (cX - qx)) + ((cY - qy) * (cY - qy)) + ((cZ - qz) * (cZ - qz));
            if (dsq <= radius * radius)
            {
                radiusExpected.Add(id);
            }
        }

        // ── Ray ──
        const double ox = 0, oy = 0, oz = 0, dx = 0.577350269, dy = 0.577350269, dz = 0.577350269, maxDist = 1800;
        HashSet<long> rayActual;
        using (var tx = dbe.CreateQuickTransaction())
        {
            rayActual = RawIds(tx.Query<RetireUnit3>().WhereRay<RetirePos3>(ox, oy, oz, dx, dy, dz, maxDist).Execute());
        }
        var rayExpected = new HashSet<long>();
        foreach (var (id, b) in boxes)
        {
            if (OracleRayHitsBox(ox, oy, oz, dx, dy, dz, maxDist, b))
            {
                rayExpected.Add(id);
            }
        }

        // ── Frustum (an axis-aligned box as six planes) ──
        const double fMinX = 100, fMinY = 100, fMinZ = 100, fMaxX = 450, fMaxY = 450, fMaxZ = 450;
        var planes = new double[]
        {
            +1, 0, 0, -fMinX,
            -1, 0, 0, +fMaxX,
            0, +1, 0, -fMinY,
            0, -1, 0, +fMaxY,
            0, 0, +1, -fMinZ,
            0, 0, -1, +fMaxZ,
        };
        HashSet<long> frustumActual;
        using (var tx = dbe.CreateQuickTransaction())
        {
            frustumActual = RawIds(tx.Query<RetireUnit3>().WhereFrustum<RetirePos3>(planes, 6, fMinX, fMinY, fMinZ, fMaxX, fMaxY, fMaxZ).Execute());
        }
        var frustumExpected = new HashSet<long>();
        foreach (var (id, b) in boxes)
        {
            if (b.maxX >= fMinX && b.minX <= fMaxX && b.maxY >= fMinY && b.minY <= fMaxY && b.maxZ >= fMinZ && b.minZ <= fMaxZ)
            {
                frustumExpected.Add(id);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(aabbExpected, Is.Not.Empty, "the AABB query region is empty, so its comparison asserts nothing");
            Assert.That(radiusExpected, Is.Not.Empty, "the radius query region is empty, so its comparison asserts nothing");
            Assert.That(rayExpected, Is.Not.Empty, "the ray hits nothing, so its comparison asserts nothing");
            Assert.That(frustumExpected, Is.Not.Empty, "the frustum region is empty, so its comparison asserts nothing");
        });

        AssertSetsMatch(aabbExpected, aabbActual, "AABB");
        AssertSetsMatch(radiusExpected, radiusActual, "radius");
        AssertSetsMatch(rayExpected, rayActual, "ray");
        AssertSetsMatch(frustumExpected, frustumActual, "frustum");
    }

    /// <summary>
    /// A result set larger than the first buffer the cluster collectors try still comes back whole.
    /// </summary>
    /// <remarks>
    /// <b>The cluster ray and frustum APIs truncate silently</b> — they fill a caller-supplied <c>Span</c> and stop — which is right for a picking ray and
    /// wrong for a query whose contract is every match. <c>EcsQuery</c> grows and retries; without that, a large frustum would quietly answer with the first
    /// 1 024 entities it happened to find, and every set comparison above would still pass because none of them is that big. The population here is chosen to
    /// exceed the initial capacity so the growth path is the one under test.
    /// </remarks>
    [Test]
    public void AResultSetLargerThanTheInitialBuffer_IsNotTruncated()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup3D(scope);

        // Above EcsQuery's 1 024-entry starting buffer, so at least one doubling is forced.
        const int population = 1_500;
        var boxes = Populate3D(dbe, population, seed: 5150);

        // A frustum covering the whole world: every entity must come back.
        var planes = new double[]
        {
            +1, 0, 0, +WorldExtent,
            -1, 0, 0, +(2 * WorldExtent),
            0, +1, 0, +WorldExtent,
            0, -1, 0, +(2 * WorldExtent),
            0, 0, +1, +WorldExtent,
            0, 0, -1, +(2 * WorldExtent),
        };

        HashSet<long> frustumActual;
        using (var tx = dbe.CreateQuickTransaction())
        {
            frustumActual = RawIds(tx.Query<RetireUnit3>()
                .WhereFrustum<RetirePos3>(planes, 6, -WorldExtent, -WorldExtent, -WorldExtent, 2 * WorldExtent, 2 * WorldExtent, 2 * WorldExtent)
                .Execute());
        }

        Assert.That(frustumActual, Has.Count.EqualTo(boxes.Count),
            $"a world-covering frustum over {population} entities returned {frustumActual.Count} — the result buffer was not grown past its first size");
    }

    /// <summary>
    /// A RAY result larger than the initial buffer comes back whole, and every entity it crosses is reported once.
    /// </summary>
    /// <remarks>
    /// <b>The frustum arm above did not cover this.</b> Ray is the shape that carries the extra work — it is the one with a final sort and, until review, the
    /// one with no full-buffer early exit, so every growth attempt but the last walked the whole grid after filling. It is also the shape whose cluster API
    /// truncates most quietly, because its documented contract is "the nearest few". The duplicate check is the second half: the growth loop re-runs the whole
    /// walk, so a visit set that leaked state across attempts would report entities twice and the count alone would not say so.
    /// </remarks>
    [Test]
    [VerifiesRule("SQ-01")]
    public void ARayResultLargerThanTheInitialBuffer_IsNotTruncatedOrDuplicated()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup3D(scope);

        // Entities strung along the world diagonal with generous extents, so one ray crosses more than the 1 024-entry
        // starting buffer and the loop is forced to grow.
        const int population = 1_500;
        var expected = new HashSet<long>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < population; i++)
            {
                var t = (i + 0.5f) / population * WorldExtent;
                var pos = new RetirePos3
                {
                    Bounds = new AABB3F { MinX = t - 8f, MinY = t - 8f, MinZ = t - 8f, MaxX = t + 8f, MaxY = t + 8f, MaxZ = t + 8f },
                };
                expected.Add((long)tx.Spawn<RetireUnit3>(RetireUnit3.Pos.Set(in pos)).RawValue);
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        List<EntityId> hits;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var inv = 1d / Math.Sqrt(3d);
            hits = [.. tx.Query<RetireUnit3>().WhereRay<RetirePos3>(0, 0, 0, inv, inv, inv, WorldExtent * 2).Execute()];
        }

        var distinct = new HashSet<long>();
        foreach (var id in hits)
        {
            distinct.Add((long)id.RawValue);
        }

        Assert.Multiple(() =>
        {
            Assert.That(expected, Has.Count.EqualTo(population), "the fixture did not spawn what it meant to");
            Assert.That(distinct, Has.Count.GreaterThan(InitialClusterResultCapacityForTest),
                $"the ray crossed only {distinct.Count} entities, which is inside the initial buffer — the growth path is not exercised");
            Assert.That(hits, Has.Count.EqualTo(distinct.Count),
                "an entity was reported more than once, so the visit set is not being reset between growth attempts");
        });
    }

    /// <summary>The initial cluster-result buffer size, mirrored here because <c>EcsQuery</c>'s copy is private.</summary>
    /// <remarks>
    /// A literal rather than a reflection read on purpose: if the production constant changes, the test above stops proving that growth happened, and a
    /// mismatch that has to be noticed is better than one that silently makes an assertion vacuous.
    /// </remarks>
    private const int InitialClusterResultCapacityForTest = 1_024;

    /// <summary>
    /// A result that is EXACTLY the buffer size still comes back whole.
    /// </summary>
    /// <remarks>
    /// <b>This is the case the growth loop's design note calls out as the deliberate cost.</b> <c>hits == capacity</c> cannot distinguish "exactly full" from
    /// "truncated", so the loop re-runs on an exact multiple — one wasted attempt, chosen over the alternative of losing rows. Without a test at exactly the
    /// boundary, an ablation that only grows on <c>hits &gt; capacity</c> stays green, and every result of precisely 1 024 entities is silently truncated.
    /// </remarks>
    [Test]
    [VerifiesRule("SQ-01")]
    public void AResultOfExactlyTheBufferSize_IsNotTruncated()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup3D(scope);

        Populate3D(dbe, InitialClusterResultCapacityForTest, seed: 4711);

        // A frustum covering the whole world: the answer is the entire population, which is exactly the buffer size.
        var planes = new double[]
        {
            +1, 0, 0, +WorldExtent,
            -1, 0, 0, +(2 * WorldExtent),
            0, +1, 0, +WorldExtent,
            0, -1, 0, +(2 * WorldExtent),
            0, 0, +1, +WorldExtent,
            0, 0, -1, +(2 * WorldExtent),
        };

        int count;
        using (var tx = dbe.CreateQuickTransaction())
        {
            count = tx.Query<RetireUnit3>()
                .WhereFrustum<RetirePos3>(planes, 6, -WorldExtent, -WorldExtent, -WorldExtent, 2 * WorldExtent, 2 * WorldExtent, 2 * WorldExtent)
                .Execute()
                .Count;
        }

        Assert.That(count, Is.EqualTo(InitialClusterResultCapacityForTest),
            $"a result of exactly the buffer size came back as {count} — the loop treats a full buffer as complete instead of re-running");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC-13.3 / AC-13.4 — the index home itself
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-13.3</c> — no type in the shipped engine outside the cell layer HOLDS or HANDS OUT a <c>SpatialRTree</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Reflection over real members, not a text search.</b> The criterion asks for a test rather than a grep so it cannot rot, and a grep for
    /// <c>new SpatialRTree</c> rots three ways: it misses a factory, it misses a differently-formatted call, and it silently stops matching when the type is
    /// renamed. This walks every field, property, method return and parameter in the engine assembly and fails on a <c>SpatialRTree&lt;&gt;</c> anywhere
    /// outside <c>CellClusterTree</c> and the tree's own file — and because it names the type through <c>typeof</c>, a rename breaks the BUILD instead of
    /// quietly passing.</para>
    /// <para><b>What it does and does not catch.</b> A construction has to be stored in a field or handed back through a signature to be reachable by
    /// anything else, so a second index home is caught. A tree constructed, used and discarded entirely within one method body is not — that is what an IL
    /// scan would add, and it is a shape with no way to serve queries, which is what a second home would have to do to matter.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("SH-01")]
    public void NoTypeOutsideTheCellLayerHoldsASpatialRTree()
    {
        var treeDefinition = typeof(SpatialRTree<>);
        var engineAssembly = typeof(DatabaseEngine).Assembly;
        var offenders = new List<string>();

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in engineAssembly.GetTypes())
        {
            if (IsCellLayer(type))
            {
                continue;
            }

            foreach (var f in type.GetFields(all))
            {
                if (IsSpatialRTree(f.FieldType, treeDefinition))
                {
                    offenders.Add($"field {type.FullName}.{f.Name}");
                }
            }

            foreach (var p in type.GetProperties(all))
            {
                if (IsSpatialRTree(p.PropertyType, treeDefinition))
                {
                    offenders.Add($"property {type.FullName}.{p.Name}");
                }
            }

            foreach (var m in type.GetMethods(all))
            {
                if (IsSpatialRTree(m.ReturnType, treeDefinition))
                {
                    offenders.Add($"return of {type.FullName}.{m.Name}");
                }

                foreach (var parameter in m.GetParameters())
                {
                    if (IsSpatialRTree(parameter.ParameterType, treeDefinition))
                    {
                        offenders.Add($"parameter {parameter.Name} of {type.FullName}.{m.Name}");
                    }
                }
            }
        }

        Assert.That(offenders, Is.Empty,
            "a second entity-level spatial index home has reappeared — SpatialRTree must be reachable only through the per-cell cluster layer: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The tree itself, the per-cell wrapper around it, their nested/compiler-generated types, and the structural validator.
    /// </summary>
    /// <remarks>
    /// <b><c>TreeValidator</c> is exempt because it CONSUMES a tree it is handed, never obtains one.</b> Its whole surface is
    /// <c>Validate(SpatialRTree&lt;T&gt;)</c> — a debug walk asserting R1-R7 on whatever the caller already has — so it cannot be the second index home this
    /// test is looking for; something else would have to hold the tree before it could be validated, and that holder is what would be caught.
    /// </remarks>
    private static bool IsCellLayer(Type type)
    {
        for (var t = type; t != null; t = t.DeclaringType)
        {
            var name = t.Name;
            if (name.StartsWith("SpatialRTree", StringComparison.Ordinal)
                || name.StartsWith("CellClusterTree", StringComparison.Ordinal)
                || name.StartsWith("TreeValidator", StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Compiler-generated closures and iterator state machines carry their owner's name in angle brackets: <MethodName>d__3.
        return type.Name.Contains("CellClusterTree", StringComparison.Ordinal) || type.Name.Contains("SpatialRTree", StringComparison.Ordinal);
    }

    private static bool IsSpatialRTree(Type type, Type treeDefinition)
    {
        var t = type;
        if (t.IsByRef || t.IsPointer || t.IsArray)
        {
            t = t.GetElementType();
        }

        return t != null && t.IsGenericType && t.GetGenericTypeDefinition() == treeDefinition;
    }

    /// <summary>
    /// <c>AC-13.4</c> — a database with a spatial component allocates no <c>StorageSegmentKind.Spatial</c> segment.
    /// </summary>
    /// <remarks>
    /// Three such segments used to be allocated per spatial component — the R-Tree, its back-pointer segment, and a Layer-1 occupancy hashmap — written into
    /// the file and reloaded on open, and empty throughout since #666. The count, not merely the query behaviour, is what makes the removal observable: a
    /// build that kept allocating them while never reading them would satisfy every other test in this fixture.
    /// </remarks>
    [Test]
    [VerifiesRule("SH-01")]
    public void ASpatialComponentAllocatesNoSpatialSegment()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup3D(scope);
        Populate3D(dbe, 200, seed: 8080);

        var segs = dbe.EnumerateStorageSegments();
        var spatial = segs.Where(s => s.Kind == StorageSegmentKind.Spatial).ToList();
        var cluster = segs.Count(s => s.Kind == StorageSegmentKind.Cluster);

        Assert.Multiple(() =>
        {
            Assert.That(cluster, Is.GreaterThan(0), "no cluster segment exists, so the absence of a Spatial one proves nothing about where entities live");
            Assert.That(spatial, Is.Empty,
                $"{spatial.Count} Spatial-kind segment(s) were allocated for a component whose entities are indexed by the per-cell cluster trees");
        });
    }

    /// <summary>
    /// A 2D component's AABB query takes its max corner from the same arguments a 3D one does.
    /// </summary>
    /// <remarks>
    /// <para><b>It did not, and nothing caught it.</b> <c>ExecuteSpatial</c> read <c>_spatialParams[2]</c> as
    /// <c>maxX</c> and <c>[3]</c> as <c>maxY</c> for a component with <c>CoordCount == 4</c> — the caller's <c>minZ</c>
    /// and <c>maxX</c>. <see cref="EcsQuery{TArchetype}.WhereInAABB{T}"/> documents and packs six doubles as
    /// <c>(minX, minY, minZ, maxX, maxY, maxZ)</c> whatever the dimension, so a 2D query got a degenerate box and a
    /// silently empty answer: an <c>SQ-01</c> false negative with no exception.</para>
    /// <para>It survived because <b>every other <c>EcsQuery</c> spatial test uses a 3D component</b>, and because the
    /// Workbench's <c>QuerySpecCompiler</c> re-packed its arguments to compensate — a workaround at a call site three
    /// projects away, which is how a defect gets mistaken for a convention. Found by the #872 measurement harness,
    /// whose 2D rows all reported zero hits.</para>
    /// <para>The assertion is deliberately a COUNT against a hand-checked expectation rather than "not empty": the
    /// broken form returned zero for a box covering everything, so any non-degenerate result would have passed a
    /// weaker test while the box was still the wrong one.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("SQ-01")]
    public void A2DComponentsAabbQuery_ReadsItsMaxCornerFromTheSameArgumentsAs3D()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup2D(scope);

        // Five boxes on a line at x = 100, 300, 500, 700, 900, all at y = 500, half-extent 10.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 5; i++)
            {
                var cx = 100f + (i * 200f);
                var pos = new ClCohPos { Bounds = new AABB2F { MinX = cx - 10f, MinY = 490f, MaxX = cx + 10f, MaxY = 510f }, Mass = 1f };
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(in pos));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        int all, leftHalf, missAbove;
        using (var tx = dbe.CreateQuickTransaction())
        {
            // Whole world. The Z arguments are real and are ignored for a 2D component — passing them must not narrow
            // anything, which is exactly what the defect did.
            all = tx.Query<ClCohUnit>().WhereInAABB<ClCohPos>(0, 0, 0, 1000, 1000, 0).Execute().Count;

            // x <= 400 covers the boxes centred at 100 and 300 only.
            leftHalf = tx.Query<ClCohUnit>().WhereInAABB<ClCohPos>(0, 0, 0, 400, 1000, 0).Execute().Count;

            // A band well above every entity — the negative control, so "returns everything" cannot pass either.
            missAbove = tx.Query<ClCohUnit>().WhereInAABB<ClCohPos>(0, 800, 0, 1000, 900, 0).Execute().Count;
        }

        Assert.Multiple(() =>
        {
            Assert.That(all, Is.EqualTo(5),
                $"a world-covering 2D AABB returned {all} of 5 — the max corner is being read from the wrong arguments");
            Assert.That(leftHalf, Is.EqualTo(2), "x <= 400 covers exactly the boxes centred at 100 and 300");
            Assert.That(missAbove, Is.EqualTo(0), "a band at y in [800, 900] contains no entity, so a non-zero count means the box is being ignored");
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC-13.2 — interest management and trigger volumes
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-13.2</c> — trigger volumes report enter, stay and leave for cluster-backed entities, driven through the public entry point.
    /// </summary>
    /// <remarks>
    /// <para><b>The criterion allows exactly two outcomes and forbids a third.</b> Either these systems are covered by a test that reaches them the way
    /// production would, or they are deleted together with their <c>rules/spatial.md</c> modules. Before this step neither was true: the only callers were
    /// tests and benchmarks reaching an <c>internal</c> <c>GetOrCreateTriggerSystem</c>, and the single production reference was a null-conditional read of a
    /// field production never assigned. That is the "unfinished" state the design warns would silently become "gone" if the tree were deleted underneath it.
    /// </para>
    /// <para>So the entry point is real API — <see cref="SpatialObserverExtensions.SpatialTriggers{T}"/> — and this drives the whole lifecycle over it: a
    /// region that starts empty, an entity spawned into it, the same entity reported as staying, and then destroyed and reported as leaving.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("IM-04")]
    [VerifiesRule("TV-01")]
    public void TriggerVolumes_ReportEnterStayAndLeave_ThroughThePublicEntryPoint()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup3D(scope);

        var triggers = dbe.SpatialTriggers<RetirePos3>();
        var handle = triggers.CreateRegion(new double[] { 100, 100, 100, 300, 300, 300 });

        Assert.That(triggers.ActiveRegionCount, Is.EqualTo(1), "the region was not registered, so nothing below is about its occupancy");

        // Nothing spawned yet: the first evaluation must be empty rather than reporting phantom occupants.
        var r0 = triggers.EvaluateRegion(handle, 1);
        var enteredOnEmpty = r0.Entered.Length;

        EntityId inside;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new RetirePos3 { Bounds = new AABB3F { MinX = 190, MinY = 190, MinZ = 190, MaxX = 210, MaxY = 210, MaxZ = 210 } };
            inside = tx.Spawn<RetireUnit3>(RetireUnit3.Pos.Set(in pos));

            // A second entity well outside the region, so "everything is reported" would fail rather than pass.
            var outsidePos = new RetirePos3 { Bounds = new AABB3F { MinX = 800, MinY = 800, MinZ = 800, MaxX = 820, MaxY = 820, MaxZ = 820 } };
            tx.Spawn<RetireUnit3>(RetireUnit3.Pos.Set(in outsidePos));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var r1 = triggers.EvaluateRegion(handle, 2);
        var entered = r1.Entered.ToArray();
        var leftOnEnter = r1.Left.Length;

        var r2 = triggers.EvaluateRegion(handle, 3);
        var enteredAgain = r2.Entered.Length;
        var stayed = r2.StayCount;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(inside);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        var r3 = triggers.EvaluateRegion(handle, 4);
        var left = r3.Left.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(enteredOnEmpty, Is.EqualTo(0), "an empty region reported an occupant");
            Assert.That(entered, Has.Length.EqualTo(1), $"expected exactly the one entity inside the region, got {entered.Length}");
            Assert.That(entered[0], Is.EqualTo((long)inside.RawValue), "the entity reported as entering is not the one inside the region");
            Assert.That(leftOnEnter, Is.EqualTo(0), "an entity left a region it had never been in");
            Assert.That(enteredAgain, Is.EqualTo(0), "a standing occupant was reported as entering a second time");
            Assert.That(stayed, Is.EqualTo(1), "the standing occupant was not reported as staying");
            Assert.That(left, Has.Length.EqualTo(1), $"expected the destroyed entity to leave, got {left.Length} departures");
            Assert.That(left[0], Is.EqualTo((long)inside.RawValue), "the entity reported as leaving is not the one destroyed");
        });

        triggers.DestroyRegion(handle);
        Assert.That(triggers.ActiveRegionCount, Is.EqualTo(0));
    }

    /// <summary>
    /// <c>AC-13.2</c> — an interest observer's delta reports the entities that moved inside its region, driven through the public entry point.
    /// </summary>
    /// <remarks>
    /// <b>The delta path is the half that lost the most code.</b> It used to accumulate the per-TABLE dirty ring and resolve each dirty chunk id through the
    /// entity tree's back-pointer segment into a leaf entry — three structures, all removed. What remains reads bounds straight out of cluster storage using
    /// the per-ARCHETYPE dirty ring, which is where entities have actually lived since #666. Asserting the observer still sees a move is what distinguishes
    /// "ported" from "the walk was deleted and the result is now always empty" — which is why the negative half (an entity moving OUTSIDE the region must not
    /// be reported) is asserted alongside it.
    /// </remarks>
    [Test]
    [VerifiesRule("IM-04")]
    [VerifiesRule("IM-01")]
    public void InterestObserver_SeesMovementInsideItsRegion_ThroughThePublicEntryPoint()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup3D(scope);

        EntityId inside, outside;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var insidePos = new RetirePos3 { Bounds = new AABB3F { MinX = 190, MinY = 190, MinZ = 190, MaxX = 210, MaxY = 210, MaxZ = 210 } };
            inside = tx.Spawn<RetireUnit3>(RetireUnit3.Pos.Set(in insidePos));
            var outsidePos = new RetirePos3 { Bounds = new AABB3F { MinX = 800, MinY = 800, MinZ = 800, MaxX = 820, MaxY = 820, MaxZ = 820 } };
            outside = tx.Spawn<RetireUnit3>(RetireUnit3.Pos.Set(in outsidePos));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var observers = dbe.SpatialObservers<RetirePos3>();
        var handle = observers.RegisterObserver(new double[] { 100, 100, 100, 300, 300, 300 }, initialTick: 1);
        Assert.That(observers.ActiveObserverCount, Is.EqualTo(1), "the observer was not registered");

        // Move BOTH entities within their own neighbourhoods, so the observer has one change to report and one to reject.
        using (var tx = dbe.CreateQuickTransaction())
        {
            ref var a = ref tx.OpenMut(inside).Write(RetireUnit3.Pos);
            a.Bounds = new AABB3F { MinX = 195, MinY = 195, MinZ = 195, MaxX = 215, MaxY = 215, MaxZ = 215 };
            ref var b = ref tx.OpenMut(outside).Write(RetireUnit3.Pos);
            b.Bounds = new AABB3F { MinX = 805, MinY = 805, MinZ = 805, MaxX = 825, MaxY = 825, MaxZ = 825 };
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        var changes = observers.GetSpatialChanges(handle, 2);
        var changed = changes.ChangedEntities.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(changed, Does.Contain((long)inside.RawValue),
                "the observer reported no change for an entity that moved inside its region — the cluster delta walk is not running");
            Assert.That(changed, Does.Not.Contain((long)outside.RawValue),
                "the observer reported an entity that moved far outside its region, so the region test is not being applied");
        });

        observers.UnregisterObserver(handle);
        Assert.That(observers.ActiveObserverCount, Is.EqualTo(0));
    }

    /// <summary>
    /// A handle to a destroyed region never validates against the slot's next tenant.
    /// </summary>
    /// <remarks>
    /// <para><b>It did, on a reachable three-region sequence.</b> The free list was threaded through
    /// <c>SpatialRegionConfig.Generation</c>: destroy wrote the next-free index over the generation, and create did
    /// <c>Generation++</c> on that link — so the counter walked backwards instead of monotonically. Create 0/1/2 (all
    /// generation 1), destroy 0 then 1, create once more: the reused slot lands on generation <c>0 + 1 = 1</c>, which is
    /// exactly the handle the caller was told was dead. <c>ValidateHandle</c> accepts it, and the caller then evaluates,
    /// moves or destroys somebody else's region.</para>
    /// <para>Pre-existing, and it would have stayed a curiosity — but #872 step 13 made these systems public API, so a
    /// handle is now something a user holds. The free list has its own <c>NextFree</c> field and <c>Generation</c> is
    /// monotonic per slot.</para>
    /// <para>The assertion is on the GENERATION rather than on a thrown exception, because the defect's signature is a
    /// handle that compares equal — asserting the throw would pass on any implementation that happened to reorder the
    /// free list, while the generations still collided.</para>
    /// </remarks>
    [Test]
    public void ADestroyedRegionHandle_NeverValidatesAgainstTheSlotsNextTenant()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Setup3D(scope);

        var triggers = dbe.SpatialTriggers<RetirePos3>();
        double[] box = [0, 0, 0, 100, 100, 100];

        var a = triggers.CreateRegion(box);
        var b = triggers.CreateRegion(box);
        var c = triggers.CreateRegion(box);

        // Destroy in the order that makes the free list hand slot b's index back first.
        triggers.DestroyRegion(a);
        triggers.DestroyRegion(b);

        var reused = triggers.CreateRegion(box);

        Assert.Multiple(() =>
        {
            Assert.That(reused, Is.Not.EqualTo(b),
                "the new region was handed the exact handle of a destroyed one — the free list is riding on the generation counter");
            Assert.That(reused, Is.Not.EqualTo(a), "same, for the first destroyed region");
            Assert.That(() => triggers.EvaluateRegion(b, 1), Throws.ArgumentException,
                "a destroyed handle still validates, so the caller can drive a region it does not own");
        });

        triggers.DestroyRegion(c);
        triggers.DestroyRegion(reused);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Independent slab test, written the long way so it shares no code with the implementation it checks.</summary>
    private static bool OracleRayHitsBox(double ox, double oy, double oz, double dx, double dy, double dz, double maxDist,
        (float minX, float minY, float minZ, float maxX, float maxY, float maxZ) b)
    {
        double tMin = 0d;
        double tMax = maxDist;

        for (var axis = 0; axis < 3; axis++)
        {
            var o = axis == 0 ? ox : axis == 1 ? oy : oz;
            var d = axis == 0 ? dx : axis == 1 ? dy : dz;
            var lo = axis == 0 ? b.minX : axis == 1 ? b.minY : b.minZ;
            var hi = axis == 0 ? b.maxX : axis == 1 ? b.maxY : b.maxZ;

            if (Math.Abs(d) < 1e-12)
            {
                if (o < lo || o > hi)
                {
                    return false;
                }
                continue;
            }

            var t1 = (lo - o) / d;
            var t2 = (hi - o) / d;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }
            tMin = Math.Max(tMin, t1);
            tMax = Math.Min(tMax, t2);
            if (tMin > tMax)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reports missing and extra ids separately — a false NEGATIVE is an SQ-01 violation, a false positive merely costs the caller a filter.</summary>
    private static void AssertSetsMatch(HashSet<long> expected, HashSet<long> actual, string what)
    {
        var missing = expected.Where(id => !actual.Contains(id)).Take(5).ToList();
        var extra = actual.Where(id => !expected.Contains(id)).Take(5).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(missing, Is.Empty,
                $"{what}: SQ-01 false negative — {expected.Count - actual.Count(expected.Contains)} entity(ies) the oracle found were not returned, "
                + $"e.g. {string.Join(", ", missing)}");
            Assert.That(extra, Is.Empty, $"{what}: {extra.Count}+ entity(ies) returned that the oracle rejects, e.g. {string.Join(", ", extra)}");
        });
    }
}
