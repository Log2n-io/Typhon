using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Schema.UnitTest.WalMigration", 1)]
[StructLayout(LayoutKind.Sequential)]
struct WalMigrationV1
{
    public int A;
    public int B;
}

[Component("Typhon.Schema.UnitTest.WalMigration", 2)]
[StructLayout(LayoutKind.Sequential)]
struct WalMigrationV2
{
    public long C;
    public int A;
    public int B;
}

[Component("Typhon.Schema.UnitTest.WalMigration", 3)]
[StructLayout(LayoutKind.Sequential)]
struct WalMigrationShrunkV3
{
    public int A;
    public int B;
}

[Component("Typhon.Schema.UnitTest.WalMigration", 4)]
[StructLayout(LayoutKind.Sequential)]
struct WalMigrationWidenedV4
{
    public long A;
    public long B;
}

[Component("Typhon.Schema.UnitTest.WalMigration", 5)]
[StructLayout(LayoutKind.Sequential)]
struct WalMigrationConvertedV5
{
    public float A;
    public int B;
}

[Component("Typhon.Schema.UnitTest.WalSvMigration", 1, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = CommitDiscipline.Commit)]
[StructLayout(LayoutKind.Sequential)]
struct WalSvMigrationV1
{
    public int A;
    public int B;
}

[Component("Typhon.Schema.UnitTest.WalSvMigration", 2, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = CommitDiscipline.Commit)]
[StructLayout(LayoutKind.Sequential)]
struct WalSvMigrationV2
{
    public long C;
    public int A;
    public int B;
}

[Archetype]
class WalMigrationArch : Archetype<WalMigrationArch>
{
    public static readonly Comp<WalMigrationV1> Value = Register<WalMigrationV1>();
}

[Archetype]
class WalMigrationV2Arch : Archetype<WalMigrationV2Arch>
{
    public static readonly Comp<WalMigrationV2> Value = Register<WalMigrationV2>();
}

[Archetype]
class WalMigrationShrunkArch : Archetype<WalMigrationShrunkArch>
{
    public static readonly Comp<WalMigrationShrunkV3> Value = Register<WalMigrationShrunkV3>();
}

[Archetype]
class WalMigrationWidenedArch : Archetype<WalMigrationWidenedArch>
{
    public static readonly Comp<WalMigrationWidenedV4> Value = Register<WalMigrationWidenedV4>();
}

[Archetype]
class WalMigrationConvertedArch : Archetype<WalMigrationConvertedArch>
{
    public static readonly Comp<WalMigrationConvertedV5> Value = Register<WalMigrationConvertedV5>();
}

[Archetype]
class WalSvMigrationArch : Archetype<WalSvMigrationArch>
{
    public static readonly Comp<WalSvMigrationV1> Value = Register<WalSvMigrationV1>();
}

[Archetype]
class WalSvMigrationV2Arch : Archetype<WalSvMigrationV2Arch>
{
    public static readonly Comp<WalSvMigrationV2> Value = Register<WalSvMigrationV2>();
}

[TestFixture]
[NonParallelizable]
class SchemaMigrationRecoveryTests : TestBase<SchemaMigrationRecoveryTests>
{
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
        o.Wal.WalDirectory = null;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CrashThenMigratingOpen_ReplaysOldLayoutRecords(bool checkpointEntity)
    {
        var id = WriteV1AndClose(crash: true, checkpointEntity);
        AssertV2OnTwoOpens(id, crashAfterRecovery: true);
    }

    [Test]
    public void CleanThenMigratingOpen_Control()
    {
        var id = WriteV1AndClose(crash: false, checkpointEntity: false);
        AssertV2OnTwoOpens(id, crashAfterRecovery: false);
    }

    [Test]
    public void CrashThenShrinkingOpen_ReplaysOnlySurvivingFields()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                id = tx.Spawn<WalMigrationV2Arch>(WalMigrationV2Arch.Value.Set(new WalMigrationV2 { C = 9876543210, A = 1111, B = 2222 }));
                tx.Commit();
            }
            dbe.SimulateHardCrash();
        }

        for (var open = 0; open < 2; open++)
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationShrunkV3>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction();
            var value = tx.Open(id).Read(WalMigrationShrunkArch.Value);
            Assert.That((value.A, value.B), Is.EqualTo((1111, 2222)), $"open {open}");
        }
    }

    [Test]
    public void CrashThenWideningOpen_ConvertsFieldTypes()
    {
        var id = WriteV1AndClose(crash: true, checkpointEntity: true);
        for (var open = 0; open < 2; open++)
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationWidenedV4>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction();
            var value = tx.Open(id).Read(WalMigrationWidenedArch.Value);
            Assert.That((value.A, value.B), Is.EqualTo((1111L, 2222L)), $"open {open}");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CrashThenMigratingOpen_UsesRegisteredMigrationChain(bool checkpointEntity)
    {
        var id = WriteV1AndClose(crash: true, checkpointEntity);
        for (var open = 0; open < 2; open++)
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterMigration<WalMigrationV1, WalMigrationV2>((ref WalMigrationV1 old, out WalMigrationV2 value) =>
            {
                value = new WalMigrationV2 { C = old.A + old.B, A = old.A + 1, B = old.B };
            });
            dbe.RegisterMigration<WalMigrationV2, WalMigrationConvertedV5>((ref WalMigrationV2 old, out WalMigrationConvertedV5 value) =>
            {
                value = new WalMigrationConvertedV5 { A = old.A / 8f, B = old.B + (int)old.C };
            });
            dbe.RegisterComponentFromAccessor<WalMigrationConvertedV5>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction();
            var value = tx.Open(id).Read(WalMigrationConvertedArch.Value);
            Assert.That((value.A, value.B), Is.EqualTo((139f, 5555)), $"open {open}");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CrashThenMigratingOpen_ReplaysSingleVersionValues(bool checkpointEntity)
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalSvMigrationV1>();
            dbe.InitializeArchetypes();
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                id = tx.Spawn<WalSvMigrationArch>(WalSvMigrationArch.Value.Set(new WalSvMigrationV1 { A = 10, B = 20 }));
                tx.Commit();
            }
            if (checkpointEntity)
            {
                Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);
            }
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.OpenMut(id).Set(WalSvMigrationArch.Value, new WalSvMigrationV1 { A = 1111, B = 2222 });
                tx.Commit();
            }
            dbe.SimulateHardCrash();
        }

        for (var open = 0; open < 2; open++)
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalSvMigrationV2>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction();
            var value = tx.Open(id).Read(WalSvMigrationV2Arch.Value);
            Assert.That((value.A, value.B, value.C), Is.EqualTo((1111, 2222, 0L)), $"open {open}");
        }
    }

    private EntityId WriteV1AndClose(bool crash, bool checkpointEntity)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WalMigrationV1>();
        dbe.InitializeArchetypes();

        EntityId id = default;
        if (checkpointEntity)
        {
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            id = tx.Spawn<WalMigrationArch>(WalMigrationArch.Value.Set(new WalMigrationV1 { A = 10, B = 20 }));
            tx.Commit();
        }

        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True);

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            var value = new WalMigrationV1 { A = 1111, B = 2222 };
            if (checkpointEntity)
            {
                tx.OpenMut(id).Set(WalMigrationArch.Value, value);
            }
            else
            {
                id = tx.Spawn<WalMigrationArch>(WalMigrationArch.Value.Set(value));
            }
            tx.Commit();
        }

        if (crash)
        {
            dbe.SimulateHardCrash();
        }
        return id;
    }

    private void AssertV2OnTwoOpens(EntityId id, bool crashAfterRecovery)
    {
        for (var open = 0; open < 2; open++)
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction())
            {
                var value = tx.Open(id).Read(WalMigrationV2Arch.Value);
                Assert.That((value.A, value.B, value.C), Is.EqualTo((1111, 2222, 0L)), $"open {open}");
            }
            if (open == 0 && crashAfterRecovery)
            {
                // 再次崩溃会从 revision chain 重建 HEAD，验证转换后的值也写进了权威副本。
                dbe.SimulateHardCrash();
            }
        }
    }
}
