using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema pair

[Component("Typhon.Schema.UnitTest.EvoDiskWal", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoDiskWalV1
{
    public int A;
    public float B;
    public EvoDiskWalV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoDiskWal", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EvoDiskWalV2
{
    public int A;
    public float B;
    public long C;
    public EvoDiskWalV2(int a, float b, long c) { A = a; B = b; C = c; }
}

[Archetype]
class EvoDiskWalArch : Archetype<EvoDiskWalArch>
{
    public static readonly Comp<EvoDiskWalV1> Comp = Register<EvoDiskWalV1>();
}

[Archetype]
class EvoDiskWalV2Arch : Archetype<EvoDiskWalV2Arch>
{
    public static readonly Comp<EvoDiskWalV2> Comp = Register<EvoDiskWalV2>();
}

#endregion

/// <summary>
/// Schema evolution against a REAL, disk-backed WAL.
/// </summary>
/// <remarks>
/// This fixture exists because the WAL backend is a coverage axis the rest of the schema-evolution suite does not vary, and a defect hid behind exactly that.
/// <c>TestBase</c> defaults to <see cref="InMemoryWalFileIO"/>, which leaves the WAL directory empty, so <c>WalFilesPresentAtOpen</c> is false and the entire
/// crash-rebuild branch of <c>RebuildEntityMapsFromPersistedData</c> is never entered. With a real WAL that flag is true on EVERY reopen — <c>*.wal</c> files
/// survive a clean shutdown — so a migrating reopen took the crash branch, re-derived the EntityMap from the freshly-allocated (empty) cluster, and
/// <c>continue</c>d past the only pass that re-places the entities. Every entity of the archetype was lost, and the whole storage-mode matrix passed anyway.
/// </remarks>
class SchemaEvolutionDiskWalTests : TestBase<SchemaEvolutionDiskWalTests>
{
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    [Test]
    public void MigratingReopen_OnDiskWal_PreservesEntities()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoDiskWalV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<EvoDiskWalArch>(EvoDiskWalArch.Comp.Set(new EvoDiskWalV1(1234, 5.5f)));
            t.Commit();
            // Clean shutdown on scope dispose — and the WAL files stay on disk regardless, which is the whole point.
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoDiskWalV2>();
            dbe.InitializeArchetypes();

            Assert.That(dbe.WalFilesPresentAtOpen, Is.True,
                "premise: with a disk-backed WAL the reopen must see WAL files, which is what routes it into the crash-rebuild branch");

            using var t = dbe.CreateQuickTransaction();
            var got = t.Open(id).Read(EvoDiskWalV2Arch.Comp);
            Assert.Multiple(() =>
            {
                Assert.That(got.A, Is.EqualTo(1234), "the entity must survive a migrating reopen on a real WAL");
                Assert.That(got.B, Is.EqualTo(5.5f).Within(0.0001f), "surviving field carries across the re-cluster");
                Assert.That(got.C, Is.EqualTo(0L), "field added by the migration zero-fills");
            });
        }
    }

    [Test]
    public void NonMigratingReopen_OnDiskWal_PreservesEntities()
    {
        // Matched control: same disk WAL, same clean close, NO schema change. Isolates the defect to the migration path rather than to the WAL backend.
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoDiskWalV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = t.Spawn<EvoDiskWalArch>(EvoDiskWalArch.Comp.Set(new EvoDiskWalV1(4321, 2.25f)));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoDiskWalV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var got = t.Open(id).Read(EvoDiskWalArch.Comp);
            Assert.That(got.A, Is.EqualTo(4321), "control: a non-migrating reopen on a disk WAL was never broken");
        }
    }
}
