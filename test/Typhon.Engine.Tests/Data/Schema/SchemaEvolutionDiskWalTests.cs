using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.IO;
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
/// <c>TestBase</c> defaults to <see cref="InMemoryWalFileIO"/>, which leaves the WAL directory empty, so the crash flag was false and the entire
/// crash-rebuild branch of <c>RebuildEntityMapsFromPersistedData</c> was never entered. With a real WAL the flag was true on EVERY reopen — <c>*.wal</c> files
/// survive a clean shutdown — so a migrating reopen took the crash branch, re-derived the EntityMap from the freshly-allocated (empty) cluster, and
/// <c>continue</c>d past the only pass that re-places the entities. Every entity of the archetype was lost, and the whole storage-mode matrix passed anyway.
/// <para>
/// Since #1143 the flag (<c>CrashRecoveryAtOpen</c>) is set only after an unclean close, so a clean migrating reopen no longer reaches that branch at all:
/// the crash-plus-migration shape the fix guards (<c>!HasMigratedSlot</c>) is exercised by the unclean-close variant.
/// </para>
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

            Assert.That(Directory.GetFiles(dbe.WalDirectory, "*.wal"), Is.Not.Empty,
                "premise: with a disk-backed WAL the WAL files survive the clean close — what used to route this reopen into the crash-rebuild branch");
            Assert.That(dbe.CrashRecoveryAtOpen, Is.False, "a clean close leaves no recovery window, WAL files or not (#1143)");

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

    /// <summary>
    /// The shape the original fix guards, reached the way it can still be reached: a migrating reopen after an UNCLEAN close takes the crash branch, and
    /// must re-place the entities rather than re-derive an empty EntityMap from the fresh cluster.
    /// </summary>
    [Test]
    public void MigratingReopen_AfterAnUncleanClose_OnDiskWal_PreservesEntities()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoDiskWalV1>();
            dbe.InitializeArchetypes();

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                id = t.Spawn<EvoDiskWalArch>(EvoDiskWalArch.Comp.Set(new EvoDiskWalV1(2468, 3.5f)));
                t.Commit();
            }

            dbe.SimulateUncleanShutdownForTest = true;
            dbe.Dispose();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoDiskWalV2>();
            dbe.InitializeArchetypes();

            Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: an unclean close routes the migrating reopen into the crash branch");

            using var t = dbe.CreateQuickTransaction();
            var got = t.Open(id).Read(EvoDiskWalV2Arch.Comp);
            Assert.That(got.A, Is.EqualTo(2468), "the entity must survive a migrating reopen on the crash path");
            Assert.That(got.B, Is.EqualTo(3.5f).Within(0.0001f));
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
