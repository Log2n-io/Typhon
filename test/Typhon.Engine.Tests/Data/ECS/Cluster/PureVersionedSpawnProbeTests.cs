using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Probe: does a pure-Versioned archetype's CLUSTER slot hold the component after a plain spawn (no subsequent update)?
/// </summary>
/// <remarks>
/// The cluster HEAD copy in the commit path is gated on "a committed value exists and this is not a spawn", so the spawn path must populate the slot by some
/// other route. Everything that scans the SoA — field queries via the zone-map path, and the statistics rebuilder — depends on it having done so.
/// </remarks>
class PureVersionedSpawnProbeTests : TestBase<PureVersionedSpawnProbeTests>
{
    [Test]
    public void SpawnOnly_PureVersionedArchetype_IsQueryableByField()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        Assert.That(ArchetypeRegistry.GetMetadata<CompDArch>().IsClusterEligible, Is.True, "premise: pure-Versioned is cluster-backed now");

        using (var t = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                t.Spawn<CompDArch>(CompDArch.D.Set(new CompD(i, i, i)));
            }

            t.Commit();
        }

        dbe.WriteTickFence(1);

        using var tx = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B == 5).Count(), Is.EqualTo(1), "spawned entity must be findable by its indexed field");
            Assert.That(tx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).Count(), Is.EqualTo(10), "all ten must be findable");
        });
    }
}
