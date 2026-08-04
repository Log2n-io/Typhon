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

        // The Versioned half of cluster-aware migration is implemented (#671): the cluster is discarded, entities are re-placed from their revision chains and
        // the HEADs refilled. SingleVersion has no chain, so its bytes exist only in the cluster slot the migration invalidates — and reconstructing them needs
        // the OLD cluster geometry, read at the OLD stride, copied through the field map. Until that lands the engine must REFUSE to open rather than present a
        // silently zeroed component, which is what it did before this assertion existed.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

            Assert.That(() => dbe.RegisterComponentFromAccessor<EvoSvProbeV2>(),
                Throws.InstanceOf<System.InvalidOperationException>().With.Message.Contains("671"),
                "a SingleVersion schema change must fail loudly with the reason, not lose the data or surface a raw storage error");
        }

        _ = entityId;
    }
}
