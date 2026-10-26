using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Schema.UnitTest.EvoArchKeep", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EvoArchKeep
{
    public int A;
    public float B;

    public EvoArchKeep(int a, float b)
    {
        A = a;
        B = b;
    }
}

[Component("Typhon.Schema.UnitTest.EvoArchAdded", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EvoArchAdded
{
    public long C;

    public EvoArchAdded(long c) => C = c;
}

/// <summary>Revision 1 of the archetype: one component. Its durable identity is the shared <c>Name</c>, not the CLR type.</summary>
[Archetype(1, Name = "EvoArchGrow")]
class EvoArchGrowV1Arch : Archetype<EvoArchGrowV1Arch>
{
    public static readonly Comp<EvoArchKeep> Keep = Register<EvoArchKeep>();
}

/// <summary>Revision 2 of the SAME archetype, with a second component. This is the change EN-8 is about.</summary>
[Archetype(2, Name = "EvoArchGrow")]
class EvoArchGrowV2Arch : Archetype<EvoArchGrowV2Arch>
{
    public static readonly Comp<EvoArchKeep> Keep = Register<EvoArchKeep>();
    public static readonly Comp<EvoArchAdded> Added = Register<EvoArchAdded>();
}

/// <summary>
/// Adding a COMPONENT to an archetype that already has persisted entities, which is the one schema change every long-lived world eventually makes and the
/// one the evolution suite does not cover.
/// </summary>
/// <remarks>
/// <para>
/// <b>A different axis from the rest of <c>SchemaEvolutionTests</c>, and the distinction is the point.</b> Those fourteen cases all change a component's own
/// FIELDS — add one, widen one, reorder them — and the migration remaps bytes inside one component's layout. This changes the archetype's COMPONENT SET,
/// which moves the cluster's geometry: a new slot, a new column, a new per-entity stride, and existing entities that have no value for any of it.
/// </para>
/// <para>
/// It is written as a question rather than as a regression guard. <c>14-game-programme.md</c> carries it as <b>EN-8</b>, "the known landmine", with the
/// instruction not to create prod's world until the deploy gate exists because "the first time we add a field" to an existing archetype would destroy the
/// only thing prod is for. That note cites a line range in <c>DatabaseEngine</c> from 2026-10-04; a good deal of archetype-level migration has been built
/// since (<c>CapturePreMigrationCluster</c>, <c>RebuildClusterFromChains</c> and the migration's field map). Nothing asserted either way, so the cost of
/// EN-8 was unknown — and an unknown that gates prod is worth one test.
/// </para>
/// <para>
/// The two archetype classes share <c>Name = "EvoArchGrow"</c> deliberately: an archetype's durable identity is that name and not its CLR type, so this is
/// the same archetype at two revisions rather than two archetypes. The second scope reopens the same data file, which is how the whole evolution suite
/// stages a reopen.
/// </para>
/// <para>
/// <b>Quarantined against #1394, which is the answer these cases produced.</b> The engine REFUSES the open rather than corrupting anything --
/// <c>ValidateArchetypeSchema</c> compares the persisted component count with the runtime's and throws before a byte is touched -- so EN-8 is a failed
/// open and not the lost world the programme doc sequences prod around. What is missing is the migration itself, which is a project and not a fix: a new
/// cluster layout, the existing entities carried across it, and the added slot left absent-but-enableable per entity. These stay red and in the nightly
/// until it exists, because a reproduction is what makes the next attempt cheap.
/// </para>
/// </remarks>
[Category("Quarantine")]
class ArchetypeComponentAddedTests : TestBase<ArchetypeComponentAddedTests>
{
    /// <summary>
    /// An entity written under revision 1 is readable under revision 2, its own component intact and the added one absent rather than garbage.
    /// </summary>
    [Test]
    public void AComponentAddedToAPopulatedArchetype_LeavesTheOldDataReadable()
    {
        EntityId entity;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoArchKeep>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entity = tx.Spawn<EvoArchGrowV1Arch>(EvoArchGrowV1Arch.Keep.Set(new EvoArchKeep(42, 3.5f)));
            tx.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoArchKeep>();
            dbe.RegisterComponentFromAccessor<EvoArchAdded>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            var kept = tx.Open(entity).Read(EvoArchGrowV2Arch.Keep);

            Assert.Multiple(() =>
            {
                Assert.That(kept.A, Is.EqualTo(42), "the component the archetype already had must survive the geometry change");
                Assert.That(kept.B, Is.EqualTo(3.5f).Within(0.0001f));
            });
        }
    }

    /// <summary>
    /// And the archetype still works after the change: a new entity spawned under revision 2 carries both components.
    /// </summary>
    /// <remarks>
    /// Separate because it projects a different claim. The case above says the old data survived; this one says the migrated archetype is usable, which a
    /// migration that preserved the bytes and left the new slot unwritable would fail.
    /// </remarks>
    [Test]
    public void AfterTheComponentIsAdded_NewEntitiesCarryBoth()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoArchKeep>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            tx.Spawn<EvoArchGrowV1Arch>(EvoArchGrowV1Arch.Keep.Set(new EvoArchKeep(7, 1.5f)));
            tx.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoArchKeep>();
            dbe.RegisterComponentFromAccessor<EvoArchAdded>();
            dbe.InitializeArchetypes();

            EntityId fresh;
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                fresh = tx.Spawn<EvoArchGrowV2Arch>(
                    EvoArchGrowV2Arch.Keep.Set(new EvoArchKeep(9, 2.5f)),
                    EvoArchGrowV2Arch.Added.Set(new EvoArchAdded(1234)));
                tx.Commit();
            }

            using var read = dbe.CreateQuickTransaction();
            var open = read.Open(fresh);
            var kept = open.Read(EvoArchGrowV2Arch.Keep);
            var added = open.Read(EvoArchGrowV2Arch.Added);

            Assert.Multiple(() =>
            {
                Assert.That(kept.A, Is.EqualTo(9));
                Assert.That(added.C, Is.EqualTo(1234), "the added component must be writable and readable on the migrated archetype");
            });
        }
    }
}
