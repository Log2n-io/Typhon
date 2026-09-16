using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Ecs;

// Own archetypes: ArchetypeRegistry is process-global and unsynchronised across parallel fixtures (#720). Two of them, because an id that is merely "some
// ushort" would pass with one — the test has to show the ids are distinct and each matches its own archetype.
[Component("Typhon.Test.PubArchId.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PubArchIdPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Component("Typhon.Test.PubArchId.Tag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PubArchIdTag
{
    [Field]
    public int Value;
}

[Archetype]
partial class PubArchIdUnit : Archetype<PubArchIdUnit>
{
    public static readonly Comp<PubArchIdPos> Pos = Register<PubArchIdPos>();
}

[Archetype]
partial class PubArchIdOther : Archetype<PubArchIdOther>
{
    public static readonly Comp<PubArchIdTag> Tag = Register<PubArchIdTag>();
}

/// <summary>
/// #958 — the public route from an archetype type to its engine-assigned id, and the telemetry overload that removes the need for the id at the common call.
/// </summary>
/// <remarks>
/// <para>
/// Before this, <c>GetSpatialTelemetry(int archetypeId)</c> was public and <c>[PublicAPI]</c> while being the only public API that takes an archetype id — and
/// the id was reachable only through <c>internal static ArchetypeMetadata Metadata</c>. A public method whose argument cannot be obtained publicly.
/// </para>
/// <para>
/// The check that matters is <b>against the engine's own use of the id</b>, not against another accessor that could be wrong in the same way. The engine's use
/// is <c>_archetypeStates[catalogId]</c>, which <see cref="DatabaseEngine.GetSpatialTelemetry(int)"/> reaches — so the witness is that telemetry answers for
/// the right archetype, and that needs no friend access.
/// </para>
/// <para>
/// <b>The trap this fixture exists to pin down.</b> Typhon has two archetype id spaces: the process-global <i>catalog</i> id, which is what
/// <c>Archetype&lt;T&gt;.CatalogId</c> and every per-archetype engine table use, and the per-database <i>routing</i> id, which is what an
/// <see cref="EntityId"/>'s low
/// 16 bits hold. Both are <see cref="ushort"/>, so confusing them compiles and silently never matches. The first draft of this fixture asserted the two were
/// equal and failed with 98 against 2 — that assertion is kept below, inverted, so the distinction cannot be lost again.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class PublicArchetypeIdTests : TestBase<PublicArchetypeIdTests>
{
    private const float CellSize = 100f;
    private const float World = 1_000f;

    private DatabaseEngine CreateEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<PubArchIdPos>();
        dbe.RegisterComponentFromAccessor<PubArchIdTag>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void ArchetypeIdIsTheCatalogIdTheEngineIndexesItsPerArchetypeTablesBy()
    {
        using var dbe = CreateEngine();

        // Wildly different populations, so the two ids cannot be told apart by luck: whatever the cluster size, 200 entities occupy more clusters than 1.
        // ActiveClusterCount counts STORAGE clusters, which every archetype has — not spatial ones — so the discriminator has to be relative, not "the
        // non-spatial one reports zero". It does not: the first draft asserted exactly that, and found PubArchIdOther holding the one cluster its single
        // entity sits in.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 200; i++)
            {
                var pos = new PubArchIdPos { Bounds = new AABB2F { MinX = i * 5f, MinY = i * 5f, MaxX = (i * 5f) + 1f, MaxY = (i * 5f) + 1f } };
                tx.Spawn<PubArchIdUnit>(PubArchIdUnit.Pos.Set(in pos));
            }

            var tag = new PubArchIdTag { Value = 7 };
            tx.Spawn<PubArchIdOther>(PubArchIdOther.Tag.Set(in tag));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var unit = dbe.GetSpatialTelemetry(Archetype<PubArchIdUnit>.CatalogId);
        var other = dbe.GetSpatialTelemetry(Archetype<PubArchIdOther>.CatalogId);

        Assert.Multiple(() =>
        {
            Assert.That(unit.ActiveClusterCount, Is.GreaterThan(1), "Archetype<T>.CatalogId must be the id the engine's per-archetype tables are indexed by");
            Assert.That(other.ActiveClusterCount, Is.EqualTo(1), "the second id must answer with ITS archetype's one cluster, not the first's 200 entities");
            Assert.That(unit.ActiveClusterCount, Is.GreaterThan(other.ActiveClusterCount), "each id must resolve to its own row, not to a shared one");
            Assert.That(Archetype<PubArchIdUnit>.CatalogId, Is.Not.EqualTo(Archetype<PubArchIdOther>.CatalogId), "two archetypes must not share a catalog id");
        });
    }

    /// <summary>
    /// The catalog id and the routing id are different numbers, and nothing in the type system says so. Asserting the distinction keeps a future change from
    /// quietly making <c>Archetype&lt;T&gt;.CatalogId</c> mean the other one — which would compile everywhere and break only where the two are compared.
    /// </summary>
    [Test]
    public void ArchetypeIdIsNotTheRoutingIdCarriedByAnEntityId()
    {
        using var dbe = CreateEngine();

        EntityId spawned;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new PubArchIdPos { Bounds = new AABB2F { MinX = 10f, MinY = 10f, MaxX = 11f, MaxY = 11f } };
            spawned = tx.Spawn<PubArchIdUnit>(PubArchIdUnit.Pos.Set(in pos));
            tx.Commit();
        }

        // The routing id is per database and numbered from 1 in registration order; the catalog id is process-global. They are both ushort, so the compiler
        // will never object to confusing them.
        Assert.That(spawned.ArchetypeId, Is.GreaterThan((ushort)0), "a spawned entity must carry a real routing id");
        Assert.That(dbe.GetMetaByRouting(spawned.ArchetypeId).ArchetypeId, Is.EqualTo(Archetype<PubArchIdUnit>.CatalogId),
            "routing id -> metadata -> catalog id must land back on Archetype<T>.CatalogId: that hop IS the relationship between the two id spaces");
    }

    /// <summary>
    /// Reading the id must not depend on anything having been spawned, or on the engine having been built at all — it finalizes the archetype itself.
    /// </summary>
    [Test]
    public void ArchetypeIdIsStableAcrossReads()
    {
        // Deliberately NO engine: the id comes from the archetype registry, which finalizes on first touch. Building one here — as the first draft did —
        // would have tested the opposite of what the summary claims.
        var first = Archetype<PubArchIdUnit>.CatalogId;
        var second = Archetype<PubArchIdUnit>.CatalogId;

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.GreaterThan((ushort)0), "a registered archetype must have a real catalog id without an engine");
            Assert.That(second, Is.EqualTo(first));
        });
    }

    [Test]
    public void TheGenericTelemetryOverloadReadsTheSameArchetypeAsTheIdOverload()
    {
        using var dbe = CreateEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 64; i++)
            {
                var pos = new PubArchIdPos { Bounds = new AABB2F { MinX = i * 5f, MinY = i * 5f, MaxX = (i * 5f) + 1f, MaxY = (i * 5f) + 1f } };
                tx.Spawn<PubArchIdUnit>(PubArchIdUnit.Pos.Set(in pos));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var byId = dbe.GetSpatialTelemetry(Archetype<PubArchIdUnit>.CatalogId);
        var byType = dbe.GetSpatialTelemetry<PubArchIdUnit>();

        Assert.Multiple(() =>
        {
            Assert.That(byType.ActiveClusterCount, Is.EqualTo(byId.ActiveClusterCount));
            Assert.That(byType.ActiveClusterCount, Is.GreaterThan(0), "precondition: the archetype has no clusters, so the two could agree on nothing");
            Assert.That(byType.FenceBranchPath, Is.EqualTo(byId.FenceBranchPath));
        });
    }

    /// <summary>
    /// The fence branch is the one number that says what the fence actually did for an archetype, and it had no public carrier — the demo reached into
    /// <c>_archetypeStates[...].ClusterState</c> for it.
    /// </summary>
    [Test]
    public void SpatialTelemetryCarriesTheFenceBranchPath()
    {
        using var dbe = CreateEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 64; i++)
            {
                var pos = new PubArchIdPos { Bounds = new AABB2F { MinX = i * 5f, MinY = i * 5f, MaxX = (i * 5f) + 1f, MaxY = (i * 5f) + 1f } };
                tx.Spawn<PubArchIdUnit>(PubArchIdUnit.Pos.Set(in pos));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var telemetry = dbe.GetSpatialTelemetry<PubArchIdUnit>();
        var total = dbe.GetSpatialTelemetryTotal();

        Assert.Multiple(() =>
        {
            // NOT.ZERO is the assertion with teeth here: 0 means "Prep found no work", and this archetype demonstrably had some, so a member hard-wired to
            // zero — the failure the first draft's InRange(0, 2) would have admitted — fails this. The exact branch is deliberately not pinned: this fence
            // reports 1 (clean-bitmap refresh) rather than the 2 a full snapshot would give, and which one Prep selects is its business, not this test's.
            Assert.That(telemetry.FenceBranchPath, Is.Not.Zero, "the fence did work for this archetype, so the branch cannot be the no-work one");
            Assert.That(telemetry.FenceBranchPath, Is.LessThanOrEqualTo((byte)2), "the branch is one of the three Prep selects: 0 none, 1 refresh, 2 full");
            Assert.That(total.FenceBranchPath, Is.GreaterThanOrEqualTo(telemetry.FenceBranchPath),
                "the engine-wide figure is the heaviest branch any archetype ran, so it cannot be below this one");
        });
    }
}
