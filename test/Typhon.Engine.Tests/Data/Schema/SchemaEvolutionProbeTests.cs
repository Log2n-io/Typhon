using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// SingleVersion, so this archetype was cluster-backed under the OLD eligibility rule (hasSvSlot) as well as the new one. That is the whole point of the
// probe: if evolution loses its data, the loss cannot be attributed to making pure-Versioned archetypes cluster-backed.
[Component("Typhon.Schema.UnitTest.EvoSvProbe", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSvProbeV1
{
    public int A;
    public float B;
    public EvoSvProbeV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoSvProbe", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSvProbeV2
{
    public int A;
    public float B;
    public int C;
    public EvoSvProbeV2(int a, float b, int c) { A = a; B = b; C = c; }
}

[Archetype]
class EvoSvProbeArch : Archetype<EvoSvProbeArch>
{
    public static readonly Comp<EvoSvProbeV1> Comp = Register<EvoSvProbeV1>();
}

[Archetype]
class EvoSvProbeV2Arch : Archetype<EvoSvProbeV2Arch>
{
    public static readonly Comp<EvoSvProbeV2> Comp = Register<EvoSvProbeV2>();
}

/// <summary>
/// Probe: does schema evolution migrate a CLUSTER-backed archetype's data? Every test in <c>SchemaEvolutionTests</c> uses a Versioned-by-default component on
/// a single-component archetype — flat storage — so none of them ever exercised the cluster SoA.
/// </summary>
class SchemaEvolutionProbeTests : TestBase<SchemaEvolutionProbeTests>
{
    [Test]
    public void AddField_OnSingleVersionClusterArchetype_PreservesData()
    {
        EntityId entityId;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSvProbeV1>();
            dbe.InitializeArchetypes();

            Assert.That(ArchetypeRegistry.GetMetadata<EvoSvProbeArch>().IsClusterEligible, Is.True,
                "premise: an SV slot makes this cluster-backed under BOTH the old and the new eligibility rule");

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<EvoSvProbeArch>(EvoSvProbeArch.Comp.Set(new EvoSvProbeV1(42, 3.14f)));
            t.Commit();
        }

        // Cluster-aware migration of SingleVersion data (#671): the migration invalidates the cluster geometry, so the old segment is loaded at its OWN stride
        // and each SV slot's bytes are copied across through the migration's field map. A is the surviving field, C is added and must land zeroed.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSvProbeV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var got = t.Open(entityId).Read(EvoSvProbeV2Arch.Comp);
            Assert.Multiple(() =>
            {
                Assert.That(got.A, Is.EqualTo(42), "surviving int field must carry over — SingleVersion has no chain, so the old cluster slot is its only copy");
                Assert.That(got.B, Is.EqualTo(3.14f).Within(0.0001f), "surviving float field must carry over");
                Assert.That(got.C, Is.EqualTo(0), "field added by the migration must be zero-filled");
            });
        }
    }
}
