using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>ComponentCollection</c> round-tripping across every storage shape that can carry one (#704 T2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this axis.</b> A <c>ComponentCollection</c> field is a descriptor into a shared variable-size buffer store, so "does the collection belong to the
/// entity that owns the descriptor" is a question the STORAGE SHAPE can change: a Versioned component re-mints its content on every revision, a SingleVersion
/// one writes in place, and a mixed archetype does both in one transaction. <c>ClusterComponentCollectionTests</c> covers exactly one composition
/// (Versioned collection + SingleVersion spatial); nothing crossed the collection with the shape.
/// </para>
/// <para>
/// <b>The element COUNT varies per entity, deliberately.</b> Entity <c>i</c> holds <c>(i % 4) + 1</c> elements. A constant length would let a defect that
/// hands every entity the same buffer pass unnoticed — with a varying length it surfaces as the wrong count, which is a stronger and earlier signal than a
/// wrong value.
/// </para>
/// <para>
/// <b>Reopen is not covered here, deliberately, and it is covered elsewhere.</b> These cases are in-session; the durable question lives in
/// <c>AxisArchetypesTests.EveryDurableCollectionCell_KeepsItsElementsAcrossAReopen</c>, which is where the kit's reopen harness (durable WAL,
/// seed-crash-reopen) actually exists. #389's plan asked for this filter to be LIFTED — it must not be. This fixture's bodies never reopen anything, so
/// removing the narrowing
/// would generate duplicate in-session cases whose NAMES advertise a reopen that never happens: #704's trap 2, a green case claiming coverage it does not have.
/// Stated rather than left to inference — a green matrix that silently skipped the durable question would be the illusion this epic removes.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class ComponentCollectionMatrixTests : TestBase<ComponentCollectionMatrixTests>
{
    private const int EntityCount = 40;

    /// <summary>The collection compositions the kit builds: every shape but pure-Transient, at <c>Index=None</c> and without spatial.</summary>
    public static IEnumerable<TestCaseData> Cells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsCollection(c) && c.Reopen == ReopenKind.None);

    private DatabaseEngine Open(Cell cell)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe, cell);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId[] Seed(DatabaseEngine dbe, Cell cell, int count)
    {
        var ids = new EntityId[count];
        using var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline);
        for (var i = 0; i < count; i++)
        {
            ids[i] = AxisArchetypes.Spawn(t, cell, i);
        }

        Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        return ids;
    }

    private sealed class CountVisitor : ICellVisitor<Transaction, int>
    {
        public int Visit<TArch>(Transaction t) where TArch : Archetype<TArch> => t.Query<TArch>().Count();
    }

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void Spawn_ThenRead_ReturnsEveryElement(Cell cell)
    {
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, EntityCount);

        using var read = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            AxisArchetypes.AssertCollectionRoundTrip(read, cell, ids[i], i);
        }
    }

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void EveryEntity_OwnsItsOwnBuffer(Cell cell)
    {
        // The failure this catches: two entities sharing one buffer. Reading each back against the per-entity model already does it, but only because the
        // model gives every entity a DIFFERENT length and different values — so this test states the property it depends on rather than assuming it.
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, EntityCount);

        using var read = dbe.CreateQuickTransaction();
        var seen = new HashSet<int>();
        for (var i = 0; i < EntityCount; i++)
        {
            AxisArchetypes.AssertCollectionRoundTrip(read, cell, ids[i], i);
            seen.Add(AxisArchetypes.ElementValue(i, 0));
        }

        Assert.That(seen, Has.Count.EqualTo(EntityCount),
            $"{cell}: the model must give every entity a distinct first element, or a shared buffer would read as correct");
    }

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void ManyEntities_AcrossClusters_KeepTheirCollections(Cell cell)
    {
        // 96 spans more than one cluster at any ClusterSize in [8,64], so the collection descriptor has to survive whatever placement the second cluster gets.
        using var dbe = Open(cell);
        const int many = 96;
        var ids = Seed(dbe, cell, many);

        using var read = dbe.CreateQuickTransaction();
        Assert.That(AxisArchetypes.Dispatch(cell, read, new CountVisitor()), Is.EqualTo(many), $"{cell}: every entity is live");

        for (var i = 0; i < many; i++)
        {
            AxisArchetypes.AssertCollectionRoundTrip(read, cell, ids[i], i);
        }
    }

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void Destroy_LeavesTheSurvivorsCollectionsIntact(Cell cell)
    {
        // Releasing a destroyed entity's buffer must not disturb anyone else's — the failure mode is a shared VSBS whose free-list reclaims a live extent.
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, EntityCount);

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < ids.Length; i += 2)
            {
                t.Destroy(ids[i]);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: destroy commit");
        }

        using var read = dbe.CreateQuickTransaction();
        Assert.That(AxisArchetypes.Dispatch(cell, read, new CountVisitor()), Is.EqualTo(EntityCount / 2), $"{cell}: half the entities were destroyed");

        for (var i = 1; i < EntityCount; i += 2)
        {
            AxisArchetypes.AssertCollectionRoundTrip(read, cell, ids[i], i);
        }
    }

    [Test]
    public void UpdateOnACollectionCell_RefusesRatherThanSilentlyWritingSomethingElse()
    {
        // The kit's Update() has no collection branch, and that is a deliberate refusal rather than a gap: rewriting a collection means allocating a new VSBS
        // buffer and releasing the old one, not overwriting a blittable payload. The spatial carrier taught why this must throw — an unhandled composition
        // that falls through to the (shape, index) switch does not fail, it silently writes a DIFFERENT archetype's component.
        var cell = new Cell(StorageShape.PureSv, DurabilityMode.Deferred, IndexShape.None, ReopenKind.None, Collection: CollectionShape.Present);
        Assert.That(AxisArchetypes.SupportsCollection(cell), Is.True, "precondition: the kit builds this cell");

        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, 4);

        using var t = dbe.CreateQuickTransaction();
        var ex = Assert.Throws<System.NotSupportedException>(() => AxisArchetypes.Update(t, cell, ids[0], 1));
        Assert.That(ex.Message, Does.Contain("does not rewrite collection buffers"));
    }
}
