using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// A schema migration must not RESURRECT entities that were destroyed before it. Characterization test for review M10 — which does NOT reproduce.
//
// M10's reasoning is sound as far as it goes: the migration rebuild derives membership from the Versioned revision chains via
// EnumerateVersionedChainHeads, that scan asks only "is this chunk allocated and not an overflow chunk?", and it then writes BornTSN = 0 / DiedTSN = 0 over
// every entity it finds. Allocation is a STORAGE fact and liveness is an MVCC fact, so on its face a destroyed entity's chain would be read back as live.
//
// Measured, the precondition does not hold on this path: a destroy frees the entity's revision chain outright, so the enumerator never sees it. Probing the
// migrating open of exactly this scenario found the chain segment holding two allocated chunks — the reserved chunk-0 sentinel and the SURVIVOR — with no
// trace of the destroyed entity, and the rebuilt EntityMap holding one entry. The window M10 describes ("between destroy and deferred cleanup") is not
// reachable across a reopen, because the engine cannot be closed with the transaction that would hold cleanup back still open.
//
// So this fixture asserts the PROPERTY rather than guarding a live defect: it passes today, and it is here because the property is one a future change to
// the destroy path — deferring the chain free, say — would silently break, and nothing else covers it.
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region Versioned component that gains a field

[Component("Typhon.Schema.UnitTest.MtombVal", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MtombValV1
{
    public int A;
    public int Pad;
    public MtombValV1(int a) { A = a; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.MtombVal", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MtombValV2
{
    public int A;
    public int Pad;
    public long B;
    public MtombValV2(int a, long b) { A = a; Pad = 0; B = b; }
}

[Archetype]
class MtombArch : Archetype<MtombArch>
{
    public static readonly Comp<MtombValV1> Comp = Register<MtombValV1>();
}

[Archetype]
class MtombV2Arch : Archetype<MtombV2Arch>
{
    public static readonly Comp<MtombValV2> Comp = Register<MtombValV2>();
}

#endregion

/// <summary>
/// An entity destroyed before a schema migration must stay destroyed across it. Characterization test — see the banner: review M10 predicted this would
/// fail, and it does not, because the destroy frees the revision chain the rebuild would have read it from.
/// </summary>
[NonParallelizable]
class MigrationTombstoneTests : TestBase<MigrationTombstoneTests>
{
    [Test]
    public void MigratingReopen_DoesNotResurrectAnEntityDestroyedBeforeIt()
    {
        EntityId survivor;
        EntityId destroyed;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtombValV1>();
            dbe.InitializeArchetypes();

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                survivor = t.Spawn<MtombArch>(MtombArch.Comp.Set(new MtombValV1(111)));
                destroyed = t.Spawn<MtombArch>(MtombArch.Comp.Set(new MtombValV1(222)));
                t.Commit();
            }

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                t.Destroy(destroyed);
                t.Commit();
            }

            // Premise. If the destroy did not take effect in the seeding session, the reopen assertion below would prove nothing.
            using var check = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                Assert.That(check.IsAlive(survivor), Is.True, "premise: the survivor is alive before the migration");
                Assert.That(check.IsAlive(destroyed), Is.False, "premise: the destroyed entity is dead before the migration");
            });
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtombValV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                // The rebuild derives membership from the chains, so a freed or tombstoned chain must not contribute an entity.
                Assert.That(t.IsAlive(destroyed), Is.False, "an entity destroyed before the migration must not come back");

                // And the filter must reject ONLY the dead: a rebuild that dropped everything would satisfy the assertion above.
                Assert.That(t.IsAlive(survivor), Is.True, "the live entity must survive the migration");
                Assert.That(t.Open(survivor).Read(MtombV2Arch.Comp).A, Is.EqualTo(111), "and keep its value across the re-cluster");
            });
        }
    }
}
