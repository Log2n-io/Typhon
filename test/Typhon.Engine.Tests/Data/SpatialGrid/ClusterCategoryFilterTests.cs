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

// Own archetypes: ArchetypeRegistry is process-global and unsynchronised across parallel fixtures (#720).

/// <summary>Category <c>0b011</c> — TWO bits, which is what lets a mask distinguish any-bit from all-bits.</summary>
[Component("Typhon.Test.Cat.Alpha", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CatAlphaPos
{
    [Field]
    [SpatialIndex(Category = ClusterCategoryFilterTests.AlphaCategory)]
    public AABB2F Bounds;
}

[Archetype]
partial class CatAlphaUnit : Archetype<CatAlphaUnit>
{
    public static readonly Comp<CatAlphaPos> Pos = Register<CatAlphaPos>();
}

/// <summary>Category <c>0b100</c> — disjoint from <see cref="CatAlphaPos"/>'s, so a one-bit mask separates the two populations.</summary>
[Component("Typhon.Test.Cat.Beta", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CatBetaPos
{
    [Field]
    [SpatialIndex(Category = ClusterCategoryFilterTests.BetaCategory)]
    public AABB2F Bounds;
}

[Archetype]
partial class CatBetaUnit : Archetype<CatBetaUnit>
{
    public static readonly Comp<CatBetaPos> Pos = Register<CatBetaPos>();
}

/// <summary>
/// SQ-02: category filtering is decided at the CLUSTER by any-bit overlap, and a promoted cell answers exactly as an unpromoted one (#900).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the two populations share their geometry.</b> Every Alpha entity is spawned at the same box as a Beta entity, so no query can separate them by
/// shape. Whatever separation a mask produces is therefore attributable to the category test and to nothing else — which is the only way an assertion about
/// filtering can be distinguished from an assertion about aim.
/// </para>
/// <para>
/// <b>Why Alpha carries TWO bits.</b> Any-bit and all-bits agree whenever the archetype's mask is a single bit, so a fixture built on one-bit categories
/// cannot see the difference between the two semantics and would pass against either. With <c>Alpha = 0b011</c> the mask <c>0b110</c> is the discriminator:
/// any-bit admits it (<c>0b011 &amp; 0b110 = 0b010 ≠ 0</c>) and all-bits rejects it (<c>0b010 ≠ 0b110</c>). That mask is what the mutant drives.
/// </para>
/// <para>
/// <b>What this fixture exists to protect.</b> Every cluster query hands a promoted cell's tree a mask of <c>0</c> and applies the any-bit test to what
/// comes back, because <c>SpatialRTree</c>'s own leaf test is all-bits. Pushing the filter into the tree looks like an optimisation and produces a false
/// negative only above <c>CellTreePromoteThreshold</c> — invisible on a default configuration, where no cell ever promotes.
/// </para>
/// </remarks>
[TestFixture]
class ClusterCategoryFilterTests : TestBase<ClusterCategoryFilterTests>
{
    internal const uint AlphaCategory = 0b011u;
    internal const uint BetaCategory = 0b100u;

    /// <summary>Shares a bit with Alpha and a bit with nothing else: any-bit admits Alpha, all-bits rejects it.</summary>
    private const uint SplitMask = 0b110u;

    private const string Sq02Marker = "SQ-02";

    private const int EntityCount = 3_000;
    private const float WorldMax = 1_000f;

    /// <summary>One cell for the whole world, so the promoted arm has a cell dense enough to promote.</summary>
    private const float CellSize = 1_000f;

    private const int PromoteAt = 24;

    /// <summary>Every mask worth asking, including the two that admit nothing and everything.</summary>
    private static IEnumerable<uint> Masks() => [0u, 1u, 2u, 4u, SplitMask, 8u, uint.MaxValue];

    private DatabaseEngine OpenEngine(int promoteThreshold) => OpenEngine(ServiceProvider, promoteThreshold);

    private static DatabaseEngine OpenEngine(IServiceProvider services, int promoteThreshold)
    {
        var dbe = services.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CatAlphaPos>();
        dbe.RegisterComponentFromAccessor<CatBetaPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize));
        if (promoteThreshold > 0)
        {
            dbe.ClusterCellTreePromoteThreshold = promoteThreshold;
            // Count-only promotion: these clusters are scattered over the cell on purpose, which is the shape the tightness gate refuses.
            dbe.ClusterCellTreePromoteTightness = 1f;
        }
        else
        {
            dbe.ClusterCellTreePromoteThreshold = int.MaxValue;
        }

        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState StateOf<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch>, new() =>
        dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState;

    /// <summary>The same boxes in both archetypes, so geometry can never be what separates them.</summary>
    private static void Spawn(DatabaseEngine dbe)
    {
        var rng = new Random(7);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < EntityCount; i++)
            {
                float x = 5f + ((float)rng.NextDouble() * (WorldMax - 10f));
                float y = 5f + ((float)rng.NextDouble() * (WorldMax - 10f));
                float half = i % 3 == 0 ? 0f : (float)rng.NextDouble() * 4f;
                var b = new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half };
                tx.Spawn<CatAlphaUnit>(CatAlphaUnit.Pos.Set(new CatAlphaPos { Bounds = b }));
                tx.Spawn<CatBetaUnit>(CatBetaUnit.Pos.Set(new CatBetaPos { Bounds = b }));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    private static AABB2F WholeWorld => new() { MinX = -1f, MinY = -1f, MaxX = WorldMax + 1f, MaxY = WorldMax + 1f };

    private static HashSet<long> Aabb<TArch>(DatabaseEngine dbe, uint mask) where TArch : Archetype<TArch>, new()
    {
        var box = WholeWorld;
        var set = new HashSet<long>();
        // The enumerator creates a ChunkAccessor over the cluster segment for its narrowphase, which is only legal inside an epoch scope (#909).
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var e = dbe.ClusterSpatialQuery<TArch>().AABB(in box, mask);
        try
        {
            while (e.MoveNext())
            {
                set.Add(unchecked((long)e.Current.Entity.RawValue));
            }
        }
        finally
        {
            e.Dispose();
        }

        return set;
    }

    private static HashSet<long> RadiusHits<TArch>(DatabaseEngine dbe, uint mask) where TArch : Archetype<TArch>, new()
    {
        var sphere = new BSphere2F { CenterX = WorldMax / 2f, CenterY = WorldMax / 2f, Radius = WorldMax * 2f };
        var set = new HashSet<long>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var e = dbe.ClusterSpatialQuery<TArch>().Radius(in sphere, mask);
        try
        {
            while (e.MoveNext())
            {
                set.Add(unchecked((long)e.Current.Entity.RawValue));
            }
        }
        finally
        {
            e.Dispose();
        }

        return set;
    }

    /// <summary>Any-bit, stated here independently of the engine's helper so the test is an oracle rather than a restatement.</summary>
    private static bool AnyBitAdmits(uint clusterMask, uint queryMask) => queryMask == 0 || (clusterMask & queryMask) != 0;

    /// <summary>All-bits — the tree's own semantics, which no cluster query may ever apply. Used only by the mutant.</summary>
    private static bool AllBitsAdmits(uint clusterMask, uint queryMask) => queryMask == 0 || (clusterMask & queryMask) == queryMask;

    private static void AssertSelects(int actual, int expectedWhenAdmitted, bool admitted, uint mask, string what)
    {
        Assert.That(actual, Is.EqualTo(admitted ? expectedWhenAdmitted : 0),
            $"{Sq02Marker}: {what} under mask 0x{mask:X} — a cluster is admitted on ANY-BIT overlap with the archetype's constant category, "
            + "so the whole population is returned or none of it is");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The mask selects an archetype, in both arms
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two archetypes on identical geometry are separated by the query mask alone, and the answer is all-or-nothing because the category is an archetype
    /// constant.
    /// </summary>
    [TestCase(0, TestName = "AQueryMaskSelectsOneArchetypeAndNotTheOther(linear)")]
    [TestCase(PromoteAt, TestName = "AQueryMaskSelectsOneArchetypeAndNotTheOther(promoted)")]
    [VerifiesRule("SQ-02")]
    public void AQueryMaskSelectsOneArchetypeAndNotTheOther(int promoteThreshold)
    {
        using var dbe = OpenEngine(promoteThreshold);
        Spawn(dbe);

        if (promoteThreshold > 0)
        {
            Assert.Multiple(() =>
            {
                Assert.That(StateOf<CatAlphaUnit>(dbe).Realm0Spatial.PromotedCellCount, Is.GreaterThan(0),
                    "precondition: the Alpha cell must promote, or this is the linear arm again");
                Assert.That(StateOf<CatBetaUnit>(dbe).Realm0Spatial.PromotedCellCount, Is.GreaterThan(0),
                    "precondition: the Beta cell must promote, or this is the linear arm again");
            });
        }

        // Precondition: unfiltered, both archetypes return their whole population. Without this, "a mask returns nothing" would be satisfied just as well by
        // a query that reaches nothing at all, and every assertion below would hold for the wrong reason.
        Assert.Multiple(() =>
        {
            Assert.That(Aabb<CatAlphaUnit>(dbe, 0u), Has.Count.EqualTo(EntityCount), "precondition: an unfiltered query must see every Alpha entity");
            Assert.That(Aabb<CatBetaUnit>(dbe, 0u), Has.Count.EqualTo(EntityCount), "precondition: an unfiltered query must see every Beta entity");
        });

        Assert.Multiple(() =>
        {
            foreach (var mask in Masks())
            {
                AssertSelects(Aabb<CatAlphaUnit>(dbe, mask).Count, EntityCount, AnyBitAdmits(AlphaCategory, mask), mask, "AABB over Alpha");
                AssertSelects(Aabb<CatBetaUnit>(dbe, mask).Count, EntityCount, AnyBitAdmits(BetaCategory, mask), mask, "AABB over Beta");
                AssertSelects(RadiusHits<CatAlphaUnit>(dbe, mask).Count, EntityCount, AnyBitAdmits(AlphaCategory, mask), mask, "Radius over Alpha");
                AssertSelects(RadiusHits<CatBetaUnit>(dbe, mask).Count, EntityCount, AnyBitAdmits(BetaCategory, mask), mask, "Radius over Beta");
            }
        });
    }

    /// <summary>
    /// The mutant: all-bits semantics, which is what handing the query mask down to a promoted cell's tree would produce.
    /// </summary>
    /// <remarks>
    /// <see cref="SplitMask"/> is where the two semantics disagree, so this is not a test that any wrong answer would fail — it is the specific wrong answer
    /// the rule forbids. It drives the fixture's own assertion, which must reject it.
    /// </remarks>
    [Test]
    [RuleMutant("SQ-02")]
    public void AnAllBitsClusterTest_IsRejected() =>
        RuleMutants.AssertDetects("SQ-02", Sq02Marker, () =>
            AssertSelects(EntityCount, EntityCount, AllBitsAdmits(AlphaCategory, SplitMask), SplitMask, "AABB over Alpha"));

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // A promoted cell answers exactly as an unpromoted one
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The same population, the same masks, either side of the promotion threshold, must produce the same entity sets — not the same counts, the same ids.
    /// </summary>
    /// <remarks>
    /// This is the assertion the "never hand the mask to the tree" clause exists to protect: pushing the filter down changes the answer only here, only for
    /// a mask that shares some but not all of the archetype's bits, and only once a cell has crossed <c>CellTreePromoteThreshold</c>.
    /// </remarks>
    [Test]
    [VerifiesRule("SQ-02")]
    public void ThePromotedAndUnpromotedArmsAnswerIdentically_UnderEveryMask()
    {
        // Each arm needs its OWN engine: ConfigureSpatialGrid refuses to run twice, and a reopened file would carry the first arm's population into the
        // second. Separate scopes plus a deleted file is how the other two-engine fixtures in this suite do it.
        var linear = new Dictionary<uint, HashSet<long>>();
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = OpenEngine(scope.ServiceProvider, 0))
        {
            Spawn(dbe);
            Assert.That(StateOf<CatAlphaUnit>(dbe).Realm0Spatial.PromotedCellCount, Is.Zero,
                "precondition: this arm must NOT promote, or both arms are the same arm");
            foreach (var mask in Masks())
            {
                linear[mask] = Aabb<CatAlphaUnit>(dbe, mask);
            }
        }

        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var promotedScope = ServiceProvider.CreateScope();
        using var promoted = OpenEngine(promotedScope.ServiceProvider, PromoteAt);
        Spawn(promoted);
        Assert.That(StateOf<CatAlphaUnit>(promoted).Realm0Spatial.PromotedCellCount, Is.GreaterThan(0),
            "precondition: this arm must promote, or it proves nothing about trees");

        Assert.Multiple(() =>
        {
            foreach (var mask in Masks())
            {
                Assert.That(Aabb<CatAlphaUnit>(promoted, mask), Is.EquivalentTo(linear[mask]),
                    $"{Sq02Marker}: a promoted cell answered mask 0x{mask:X} differently from an unpromoted one — the tree's leaf test is all-bits, so a "
                    + "cluster query that hands its mask down stops agreeing with its unpromoted neighbour");
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Every shape applies the same test
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ray, frustum and kNN gate on the same any-bit cluster test as AABB and radius. Until #900 each spelled it out for itself.
    /// </summary>
    [TestCase(0, TestName = "EveryShapeAppliesTheSameAnyBitTest(linear)")]
    [TestCase(PromoteAt, TestName = "EveryShapeAppliesTheSameAnyBitTest(promoted)")]
    [VerifiesRule("SQ-02")]
    public void EveryShapeAppliesTheSameAnyBitTest(int promoteThreshold)
    {
        using var dbe = OpenEngine(promoteThreshold);
        Spawn(dbe);

        // Without this the promoted case can silently be a second linear arm, and this is the ONLY promoted arm that drives the ray, frustum and kNN tree
        // paths — the shapes' own preconditions below prove only that they reach entities, which the linear arm satisfies just as well.
        if (promoteThreshold > 0)
        {
            Assert.That(StateOf<CatAlphaUnit>(dbe).Realm0Spatial.PromotedCellCount, Is.GreaterThan(0),
                "precondition: the cell must promote, or this case proves nothing about the shapes' tree paths");
        }

        var cs = StateOf<CatAlphaUnit>(dbe);
        var grid = dbe.Realm0Grid;
        // Each shape opens a ChunkAccessor on the cluster segment for its narrowphase (#909).
        using var epoch = EpochGuard.Enter(dbe.EpochManager);

        var rayHits = new (long entityId, double distance)[EntityCount];
        var frustumHits = new long[EntityCount];
        var knnHits = new (long entityId, double distSq)[16];

        // The whole world as four inward half-spaces, and a ray along the diagonal: both reach a large share of the population, which is all the shapes need
        // to do here — what is under test is the category gate, not the geometry.
        // Packed (normalX, normalY, distance): dim + 1 doubles per plane, so THREE for a 2D archetype. Inside is dot(n, p) + d >= 0. Passing four per plane
        // (the shape EcsQuery's frustum predicate takes) silently misreads every plane and the query finds nothing — which the precondition below caught.
        double[] planes =
        [
            1, 0, 1,
            -1, 0, WorldMax + 1,
            0, 1, 1,
            0, -1, WorldMax + 1,
        ];
        var boundsMin = new Vector3Like(-1, -1, 0);
        var boundsMax = new Vector3Like(WorldMax + 1, WorldMax + 1, 0);

        int RayCount(uint mask) => cs.QueryRay(grid, -1, -1, 0, 0.7071067811865476, 0.7071067811865476, 0, WorldMax * 4, rayHits, mask);
        int FrustumCount(uint mask) => cs.QueryFrustum(grid, planes, 4, boundsMin, boundsMax, frustumHits, mask);
        int KnnCount(uint mask) => cs.QueryNearest(grid, WorldMax / 2, WorldMax / 2, 0, 16, knnHits, mask);

        // Preconditions: unfiltered, each shape must actually reach entities. A shape that reaches none would satisfy every rejection assertion below.
        Assert.Multiple(() =>
        {
            Assert.That(RayCount(0u), Is.GreaterThan(0), "precondition: the ray must reach entities, or its rejection case is vacuous");
            Assert.That(FrustumCount(0u), Is.GreaterThan(0), "precondition: the frustum must reach entities, or its rejection case is vacuous");
            Assert.That(KnnCount(0u), Is.GreaterThan(0), "precondition: kNN must reach entities, or its rejection case is vacuous");
        });

        Assert.Multiple(() =>
        {
            foreach (var mask in Masks())
            {
                bool admitted = AnyBitAdmits(AlphaCategory, mask);
                Assert.That(RayCount(mask) > 0, Is.EqualTo(admitted), $"{Sq02Marker}: ray under mask 0x{mask:X}");
                Assert.That(FrustumCount(mask) > 0, Is.EqualTo(admitted), $"{Sq02Marker}: frustum under mask 0x{mask:X}");
                Assert.That(KnnCount(mask) > 0, Is.EqualTo(admitted), $"{Sq02Marker}: kNN under mask 0x{mask:X}");
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The structural half
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// No query a cell tree offers takes a category mask, so a cluster query cannot hand one down through it.
    /// </summary>
    /// <remarks>
    /// <para>Asserted by REFLECTION rather than by reading the source: a grep for the parameter stops matching on a rename, where enumerating the type
    /// through <c>typeof</c> fails the test instead.</para>
    /// <para><b>What this does and does not guard.</b> It sees SIGNATURES, not bodies — a wrapper that kept its mask-less signature but passed
    /// <c>uint.MaxValue</c> to the tree would still pass here. That case is covered behaviourally instead, by
    /// <see cref="ThePromotedAndUnpromotedArmsAnswerIdentically_UnderEveryMask"/>, which was confirmed to fail when the mask is routed into the tree. The
    /// two together cover the invariant; neither does alone.</para>
    /// <para>Every method whose name begins with <c>Query</c> is enumerated rather than a fixed list, so a wrapper added later is guarded by default —
    /// and the parameter test is by SHAPE rather than by <c>typeof(uint)</c>, because an <c>int</c>, a uint-backed enum or a <c>CategoryMask</c> struct
    /// would all slip past an exact-type check in a codebase that likes wrapping primitives.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("SQ-02")]
    public void NoCellClusterTreeQueryTakesACategoryMask()
    {
        // Every read path onto the tree, not a name prefix. kNN reaches a promoted cell through EnumerateClusterIds rather than a Query* wrapper, so a
        // filter on "Query" would have left exactly one of the five shapes unguarded — which is what it did until this was widened.
        var expected = new[] { "Query", "QueryWith", "QueryF32", "QueryF32With", "QueryRay", "QueryFrustum", "EnumerateClusterIds" };
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var methods = typeof(CellClusterTree).GetMethods(All).Where(m => m.DeclaringType == typeof(CellClusterTree)).ToArray();

        Assert.That(methods.Select(m => m.Name).Distinct(), Is.SupersetOf(expected),
            "precondition: every known read path must be found, or this test is asserting over an empty or truncated set");

        // Constructors too: GetMethods excludes them, so `new CellClusterTree(segment, backPointers, defaultMask)` would route a mask in without touching
        // any method signature. Widening to every method already covers the property case for free — a `set_DefaultMask(uint)` is enumerated here.
        var members = methods.Cast<MethodBase>().Concat(typeof(CellClusterTree).GetConstructors(All)).ToArray();

        // `int` is excluded so plain counts such as planeCount need no carve-out — an exemption matched on a formatted string was the earlier version, and
        // a parameter renamed to end in "planeCount" walked straight through it. Excluding `int` costs one thing, so it is bought back explicitly: EVERY
        // enum counts whatever its backing, because a plain C# enum backs to `int` and `CategoryBits categories` would otherwise slip — the likeliest shape
        // a future refactor takes. No legitimate enum parameter exists on this surface, so the blanket rule costs nothing today.
        static bool IsMaskShaped(ParameterInfo p)
        {
            var t = p.ParameterType;
            return t.IsEnum
                || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(byte)
                || p.Name.Contains("mask", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("categor", StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains("Mask", StringComparison.Ordinal)
                || t.Name.Contains("Categor", StringComparison.Ordinal);
        }

        Assert.Multiple(() =>
        {
            foreach (var m in members)
            {
                var offenders = m.GetParameters().Where(IsMaskShaped).Select(p => $"{p.ParameterType.Name} {p.Name}").ToArray();
                Assert.That(offenders, Is.Empty,
                    $"{Sq02Marker}: CellClusterTree.{m.Name} takes {string.Join(", ", offenders)} — the cell tree's leaf test is all-bits while every "
                    + "cluster query filters any-bit, so a mask reaching the tree makes a promoted cell answer a different question from an unpromoted one");
            }
        });
    }
}
