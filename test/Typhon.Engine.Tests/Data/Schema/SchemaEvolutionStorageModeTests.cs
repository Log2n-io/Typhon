using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The storage-mode axis of schema evolution (#671). SchemaEvolutionTests has ZERO occurrences of StorageMode — every one of its tests runs a Versioned-by-
// default component, which is why a subsystem that could not migrate a SingleVersion cluster looked healthy for months. Versioned itself is covered there
// (and is cluster-backed now that every archetype is), so what these add is the mode that has NO second copy of its data: SingleVersion lives only in the
// cluster slot, and a migration changes component sizes, which changes ClusterSize, which moves every offset in the cluster.
//
// Field sizes here are deliberately NON-UNIFORM and the payloads deliberately distinctive. Uniform sizes let a mis-addressed read land inside the right
// slot anyway and report success; a mix of int/float/long/short with recognisable values makes an off-by-one field or slot visible in the assertion.
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region Add a field

[Component("Typhon.Schema.UnitTest.EvoSmAdd", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSmAddV1
{
    public int A;
    public float B;
    public EvoSmAddV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoSmAdd", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSmAddV2
{
    public int A;
    public float B;
    public long C;
    public EvoSmAddV2(int a, float b, long c) { A = a; B = b; C = c; }
}

[Archetype]
class EvoSmAddArch : Archetype<EvoSmAddArch>
{
    public static readonly Comp<EvoSmAddV1> Comp = Register<EvoSmAddV1>();
}

[Archetype]
class EvoSmAddV2Arch : Archetype<EvoSmAddV2Arch>
{
    public static readonly Comp<EvoSmAddV2> Comp = Register<EvoSmAddV2>();
}

#endregion

#region Widen a field

[Component("Typhon.Schema.UnitTest.EvoSmWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSmWidenV1
{
    public short A;
    public int B;
    public EvoSmWidenV1(short a, int b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoSmWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct EvoSmWidenV2
{
    public long A;
    public int B;
    public EvoSmWidenV2(long a, int b) { A = a; B = b; }
}

[Archetype]
class EvoSmWidenArch : Archetype<EvoSmWidenArch>
{
    public static readonly Comp<EvoSmWidenV1> Comp = Register<EvoSmWidenV1>();
}

[Archetype]
class EvoSmWidenV2Arch : Archetype<EvoSmWidenV2Arch>
{
    public static readonly Comp<EvoSmWidenV2> Comp = Register<EvoSmWidenV2>();
}

#endregion

#region Remove a field

[Component("Typhon.Schema.UnitTest.EvoSmDrop", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct EvoSmDropV1
{
    public int A;
    public long Doomed;
    public float C;
    public EvoSmDropV1(int a, long doomed, float c) { A = a; Doomed = doomed; C = c; }
}

[Component("Typhon.Schema.UnitTest.EvoSmDrop", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSmDropV2
{
    public int A;
    public float C;
    public EvoSmDropV2(int a, float c) { A = a; C = c; }
}

[Archetype]
class EvoSmDropArch : Archetype<EvoSmDropArch>
{
    public static readonly Comp<EvoSmDropV1> Comp = Register<EvoSmDropV1>();
}

[Archetype]
class EvoSmDropV2Arch : Archetype<EvoSmDropV2Arch>
{
    public static readonly Comp<EvoSmDropV2> Comp = Register<EvoSmDropV2>();
}

#endregion

#region Mixed archetype: SingleVersion alongside Versioned

[Component("Typhon.Schema.UnitTest.EvoMixSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoMixSvV1
{
    public int A;
    public float B;
    public EvoMixSvV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoMixSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoMixSvV2
{
    public int A;
    public float B;
    public long C;
    public EvoMixSvV2(int a, float b, long c) { A = a; B = b; C = c; }
}

// Versioned companion, unchanged across the migration. Its cluster HEAD is rebuilt from its revision chain, so it must survive a migration driven entirely
// by the SV component next to it — a different recovery route in the same rebuild pass.
[Component("Typhon.Schema.UnitTest.EvoMixVer", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoMixVer
{
    public long V;
    public EvoMixVer(long v) { V = v; }
}

[Archetype]
class EvoMixArch : Archetype<EvoMixArch>
{
    public static readonly Comp<EvoMixSvV1> Sv = Register<EvoMixSvV1>();
    public static readonly Comp<EvoMixVer> Ver = Register<EvoMixVer>();
}

[Archetype]
class EvoMixV2Arch : Archetype<EvoMixV2Arch>
{
    public static readonly Comp<EvoMixSvV2> Sv = Register<EvoMixSvV2>();
    public static readonly Comp<EvoMixVer> Ver = Register<EvoMixVer>();
}

#endregion

/// <summary>
/// Schema evolution across the storage-mode axis (#671). Each test seeds under the V1 schema, closes cleanly, then reopens declaring V2 and asserts the data
/// survived the re-cluster — the migration changes component sizes, so every entity lands at a different <c>(clusterChunkId, slotIndex)</c>.
/// </summary>
class SchemaEvolutionStorageModeTests : TestBase<SchemaEvolutionStorageModeTests>
{
    [Test]
    public void SingleVersion_AddField_PreservesDataAndZeroFillsNewField()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmAddV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<EvoSmAddArch>(EvoSmAddArch.Comp.Set(new EvoSmAddV1(0x5EED, 2.5f)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmAddV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var got = t.Open(id).Read(EvoSmAddV2Arch.Comp);
            Assert.Multiple(() =>
            {
                Assert.That(got.A, Is.EqualTo(0x5EED), "surviving int must carry across the re-cluster");
                Assert.That(got.B, Is.EqualTo(2.5f).Within(0.0001f), "surviving float must carry across the re-cluster");
                Assert.That(got.C, Is.EqualTo(0L), "field added by the migration must be zero-filled, not garbage from the old layout");
            });
        }
    }

    [Test]
    public void SingleVersion_WidenField_PreservesValue()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmWidenV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<EvoSmWidenArch>(EvoSmWidenArch.Comp.Set(new EvoSmWidenV1(-1234, 77)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmWidenV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var got = t.Open(id).Read(EvoSmWidenV2Arch.Comp);
            Assert.Multiple(() =>
            {
                // Negative on purpose: a widening that zero-extends instead of sign-extending turns -1234 into 64302, which a positive probe value would hide.
                Assert.That(got.A, Is.EqualTo(-1234L), "short -> long must SIGN-extend through the field map");
                Assert.That(got.B, Is.EqualTo(77), "the field after the widened one must not be dragged out of position");
            });
        }
    }

    [Test]
    public void SingleVersion_RemoveField_KeepsSurvivorsAtTheirNewOffsets()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmDropV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<EvoSmDropArch>(EvoSmDropArch.Comp.Set(new EvoSmDropV1(11, long.MaxValue, 6.25f)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSmDropV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var got = t.Open(id).Read(EvoSmDropV2Arch.Comp);
            Assert.Multiple(() =>
            {
                Assert.That(got.A, Is.EqualTo(11), "field before the removed one keeps its offset");
                // C moves from offset 12 to offset 4. The 12 comes from V1's Pack = 4, which places Doomed at 4 rather than 8 (#816, TYPHON010) — under the
                // natural layout C sat at 16. Reading C at its OLD offset would pick up bytes 12-15 of a V1 record, which is where Doomed's tail sits, so
                // this is the assertion that actually proves the field map drove the copy rather than a blind memcpy.
                Assert.That(got.C, Is.EqualTo(6.25f).Within(0.0001f), "field AFTER the removed one must be re-addressed to its new offset");
            });
        }
    }

    [Test]
    public void MixedArchetype_SvMigrates_AndVersionedNeighbourSurvives()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoMixSvV1>();
            dbe.RegisterComponentFromAccessor<EvoMixVer>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<EvoMixArch>(EvoMixArch.Sv.Set(new EvoMixSvV1(7, 1.5f)), EvoMixArch.Ver.Set(new EvoMixVer(0xABCDEF)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoMixSvV2>();
            dbe.RegisterComponentFromAccessor<EvoMixVer>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var e = t.Open(id);
            var sv = e.Read(EvoMixV2Arch.Sv);
            var ver = e.Read(EvoMixV2Arch.Ver);
            Assert.Multiple(() =>
            {
                Assert.That(sv.A, Is.EqualTo(7), "SV survives via the cluster-to-cluster copy");
                Assert.That(sv.B, Is.EqualTo(1.5f).Within(0.0001f), "SV survives via the cluster-to-cluster copy");
                Assert.That(sv.C, Is.EqualTo(0L), "added SV field zero-fills");
                // The two modes recover by DIFFERENT routes in the same pass — SV is copied from the old cluster, Versioned is refilled from its chain. This
                // asserts the SV copy did not overwrite the neighbour's slot, and that re-placing entities kept both slots pointing at the same entity.
                Assert.That(ver.V, Is.EqualTo(0xABCDEF), "the untouched Versioned neighbour must be refilled from its chain at the entity's NEW cluster slot");

                // Review §5 C.4: the canonical migration fixture also has to say the migration left the FILE consistent, not just the data readable. A
                // migration replaces the cluster, the EntityMap and (when an index is added) the index segment; each replacement used to abandon the old
                // segment's pages (M9). MigrationSegmentReclaimTests covers the shapes; this is the guard on the fixture everyone edits.
                Assert.That(dbe.RunStorageIntegrityCheck().IsHealthy, Is.True, "the migrating open must not leave pages allocated to no segment");
            });
        }
    }
}
