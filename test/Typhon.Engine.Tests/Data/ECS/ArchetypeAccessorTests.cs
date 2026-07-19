using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── SV components (primary fast-path use case) ──────────────────────────
[Component("Typhon.Test.AA.SvPosition", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SvPosition
{
    public float X, Y;
    public SvPosition(float x, float y) { X = x; Y = y; }
}

[Component("Typhon.Test.AA.SvVelocity", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SvVelocity
{
    public float Dx, Dy;
    public SvVelocity(float dx, float dy) { Dx = dx; Dy = dy; }
}

[Archetype(150)]
partial class SvUnit : Archetype<SvUnit>
{
    public static readonly Comp<SvPosition> Position = Register<SvPosition>();
    public static readonly Comp<SvVelocity> Velocity = Register<SvVelocity>();
}

// ── Versioned components (MVCC path) ────────────────────────────────────
[Component("Typhon.Test.AA.VStats", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct VStats
{
    public int Health, MaxHealth;
    public VStats(int hp, int max) { Health = hp; MaxHealth = max; }
}

[Archetype(151)]
partial class VUnit : Archetype<VUnit>
{
    public static readonly Comp<VStats> Stats = Register<VStats>();
}

// ── Mixed archetype: SV + Versioned ─────────────────────────────────────
[Component("Typhon.Test.AA.MixedSvPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MixedSvPos
{
    public float X, Y;
    public MixedSvPos(float x, float y) { X = x; Y = y; }
}

[Component("Typhon.Test.AA.MixedVData", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct MixedVData
{
    public int Score;
    public int _pad;
    public MixedVData(int s) { Score = s; }
}

[Archetype(152)]
partial class MixedUnit : Archetype<MixedUnit>
{
    public static readonly Comp<MixedSvPos> Position = Register<MixedSvPos>();
    public static readonly Comp<MixedVData> Data = Register<MixedVData>();
}

[NonParallelizable]
class ArchetypeAccessorTests : TestBase<ArchetypeAccessorTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SvPosition>();
        dbe.RegisterComponentFromAccessor<SvVelocity>();
        dbe.RegisterComponentFromAccessor<VStats>();
        dbe.RegisterComponentFromAccessor<MixedSvPos>();
        dbe.RegisterComponentFromAccessor<MixedVData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SV Read Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SV_Open_ReadsCorrectValues()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new SvPosition(10, 20);
        var vel = new SvVelocity(1, 2);
        var id = tx.Spawn<SvUnit>(SvUnit.Position.Set(in pos), SvUnit.Velocity.Set(in vel));
        tx.Commit();

        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<SvUnit>();
        var entity = accessor.Open(id);

        Assert.That(entity.IsValid, Is.True);
        ref readonly var p = ref entity.Read(SvUnit.Position);
        Assert.That(p.X, Is.EqualTo(10f));
        Assert.That(p.Y, Is.EqualTo(20f));
        ref readonly var v = ref entity.Read(SvUnit.Velocity);
        Assert.That(v.Dx, Is.EqualTo(1f));
        Assert.That(v.Dy, Is.EqualTo(2f));
        accessor.Dispose();
    }

    [Test]
    public void SV_Open_MultipleEntities()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var ids = new EntityId[200];
        for (int i = 0; i < ids.Length; i++)
        {
            var pos = new SvPosition(i, i * 10);
            var vel = new SvVelocity(0, 0);
            ids[i] = tx.Spawn<SvUnit>(SvUnit.Position.Set(in pos), SvUnit.Velocity.Set(in vel));
        }
        tx.Commit();

        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<SvUnit>();
        for (int i = 0; i < ids.Length; i++)
        {
            var entity = accessor.Open(ids[i]);
            Assert.That(entity.IsValid, Is.True);
            ref readonly var p = ref entity.Read(SvUnit.Position);
            Assert.That(p.X, Is.EqualTo((float)i));
        }
        accessor.Dispose();
    }

    [Test]
    public void SV_Open_NonExistentEntity_ReturnsInvalid()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<SvUnit>();
        var entity = accessor.Open(new EntityId(99999, 150));
        Assert.That(entity.IsValid, Is.False);
        accessor.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SV Write Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SV_OpenMut_WritesPersist()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new SvPosition(0, 0);
        var vel = new SvVelocity(0, 0);
        var id = tx.Spawn<SvUnit>(SvUnit.Position.Set(in pos), SvUnit.Velocity.Set(in vel));
        tx.Commit();

        // Write via ArchetypeAccessor
        using var writeTx = dbe.CreateQuickTransaction();
        var accessor = writeTx.For<SvUnit>();
        var entity = accessor.OpenMut(id);
        ref var wp = ref entity.Write(SvUnit.Position);
        wp.X = 99f;
        wp.Y = 88f;
        accessor.Dispose();
        writeTx.Commit();

        // Verify
        using var verifyTx = dbe.CreateQuickTransaction();
        var va = verifyTx.For<SvUnit>();
        ref readonly var vp = ref va.Open(id).Read(SvUnit.Position);
        Assert.That(vp.X, Is.EqualTo(99f));
        Assert.That(vp.Y, Is.EqualTo(88f));
        va.Dispose();
    }

    [Test]
    public void SV_ReadWrite_MovementPattern()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new SvPosition(100, 200);
        var vel = new SvVelocity(5, -3);
        var id = tx.Spawn<SvUnit>(SvUnit.Position.Set(in pos), SvUnit.Velocity.Set(in vel));
        tx.Commit();

        // Simulate movement: pos += vel
        using var moveTx = dbe.CreateQuickTransaction();
        var accessor = moveTx.For<SvUnit>();
        var entity = accessor.OpenMut(id);
        ref var p = ref entity.Write(SvUnit.Position);
        ref readonly var v = ref entity.Read(SvUnit.Velocity);
        p.X += v.Dx;
        p.Y += v.Dy;
        accessor.Dispose();
        moveTx.Commit();

        // Verify: (100+5, 200-3) = (105, 197)
        using var verifyTx = dbe.CreateQuickTransaction();
        var va = verifyTx.For<SvUnit>();
        ref readonly var result = ref va.Open(id).Read(SvUnit.Position);
        Assert.That(result.X, Is.EqualTo(105f));
        Assert.That(result.Y, Is.EqualTo(197f));
        va.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Versioned Read/Write Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Versioned_Open_ReadsCorrectValues()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var stats = new VStats(100, 100);
        var id = tx.Spawn<VUnit>(VUnit.Stats.Set(in stats));
        tx.Commit();

        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<VUnit>();
        var entity = accessor.Open(id);
        Assert.That(entity.IsValid, Is.True);
        ref readonly var s = ref entity.Read(VUnit.Stats);
        Assert.That(s.Health, Is.EqualTo(100));
        Assert.That(s.MaxHealth, Is.EqualTo(100));
        accessor.Dispose();
    }

    [Test]
    public void Versioned_OpenMut_WritesPersist()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var stats = new VStats(100, 100);
        var id = tx.Spawn<VUnit>(VUnit.Stats.Set(in stats));
        tx.Commit();

        // Write via ArchetypeAccessor — Versioned copy-on-write
        using var writeTx = dbe.CreateQuickTransaction();
        var accessor = writeTx.For<VUnit>();
        var entity = accessor.OpenMut(id);
        ref var ws = ref entity.Write(VUnit.Stats);
        ws.Health = 42;
        accessor.Dispose();
        writeTx.Commit();

        // Verify the Versioned write persisted
        using var verifyTx = dbe.CreateQuickTransaction();
        var va = verifyTx.For<VUnit>();
        ref readonly var result = ref va.Open(id).Read(VUnit.Stats);
        Assert.That(result.Health, Is.EqualTo(42));
        Assert.That(result.MaxHealth, Is.EqualTo(100)); // untouched field
        va.Dispose();
    }

    [Test]
    public void Versioned_MultipleWrites_AllPersist()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var ids = new EntityId[50];
        for (int i = 0; i < ids.Length; i++)
        {
            var stats = new VStats(i, 1000);
            ids[i] = tx.Spawn<VUnit>(VUnit.Stats.Set(in stats));
        }
        tx.Commit();

        // Write all via ArchetypeAccessor in a single transaction
        using var writeTx = dbe.CreateQuickTransaction();
        var accessor = writeTx.For<VUnit>();
        for (int i = 0; i < ids.Length; i++)
        {
            var entity = accessor.OpenMut(ids[i]);
            ref var ws = ref entity.Write(VUnit.Stats);
            ws.Health = i * 10;
        }
        accessor.Dispose();
        writeTx.Commit();

        // Verify all writes
        using var verifyTx = dbe.CreateQuickTransaction();
        var va = verifyTx.For<VUnit>();
        for (int i = 0; i < ids.Length; i++)
        {
            ref readonly var result = ref va.Open(ids[i]).Read(VUnit.Stats);
            Assert.That(result.Health, Is.EqualTo(i * 10), $"Entity {i}");
            Assert.That(result.MaxHealth, Is.EqualTo(1000), $"Entity {i} MaxHealth");
        }
        va.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Mixed Archetype Tests (SV + Versioned in same archetype)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Mixed_ReadBothComponents()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new MixedSvPos(5, 10);
        var data = new MixedVData(42);
        var id = tx.Spawn<MixedUnit>(MixedUnit.Position.Set(in pos), MixedUnit.Data.Set(in data));
        tx.Commit();

        using var readTx = dbe.CreateQuickTransaction();
        var accessor = readTx.For<MixedUnit>();
        var entity = accessor.Open(id);
        Assert.That(entity.IsValid, Is.True);

        ref readonly var p = ref entity.Read(MixedUnit.Position);
        Assert.That(p.X, Is.EqualTo(5f));
        ref readonly var d = ref entity.Read(MixedUnit.Data);
        Assert.That(d.Score, Is.EqualTo(42));
        accessor.Dispose();
    }

    [Test]
    public void Mixed_WriteBothComponents()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new MixedSvPos(0, 0);
        var data = new MixedVData(0);
        var id = tx.Spawn<MixedUnit>(MixedUnit.Position.Set(in pos), MixedUnit.Data.Set(in data));
        tx.Commit();

        // Write both SV and Versioned components through ArchetypeAccessor
        using var writeTx = dbe.CreateQuickTransaction();
        var accessor = writeTx.For<MixedUnit>();
        var entity = accessor.OpenMut(id);
        ref var wp = ref entity.Write(MixedUnit.Position);
        wp.X = 77f;
        wp.Y = 33f;
        ref var wd = ref entity.Write(MixedUnit.Data);
        wd.Score = 999;
        accessor.Dispose();
        writeTx.Commit();

        // Verify both
        using var verifyTx = dbe.CreateQuickTransaction();
        var va = verifyTx.For<MixedUnit>();
        var ve = va.Open(id);
        ref readonly var vp = ref ve.Read(MixedUnit.Position);
        Assert.That(vp.X, Is.EqualTo(77f));
        Assert.That(vp.Y, Is.EqualTo(33f));
        ref readonly var vd = ref ve.Read(MixedUnit.Data);
        Assert.That(vd.Score, Is.EqualTo(999));
        va.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Equivalence: ArchetypeAccessor produces same results as standard path
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SV_EquivalentToStandardPath()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new SvPosition(42, 84);
        var vel = new SvVelocity(7, 3);
        var id = tx.Spawn<SvUnit>(SvUnit.Position.Set(in pos), SvUnit.Velocity.Set(in vel));
        tx.Commit();

        // Read via standard path
        using var stdTx = dbe.CreateQuickTransaction();
        var stdEntity = stdTx.Open(id);
        ref readonly var stdPos = ref stdEntity.Read(SvUnit.Position);
        ref readonly var stdVel = ref stdEntity.Read(SvUnit.Velocity);

        // Read via ArchetypeAccessor
        using var aaTx = dbe.CreateQuickTransaction();
        var aa = aaTx.For<SvUnit>();
        var aaEntity = aa.Open(id);
        ref readonly var aaPos = ref aaEntity.Read(SvUnit.Position);
        ref readonly var aaVel = ref aaEntity.Read(SvUnit.Velocity);

        // Must be identical
        Assert.That(aaPos.X, Is.EqualTo(stdPos.X));
        Assert.That(aaPos.Y, Is.EqualTo(stdPos.Y));
        Assert.That(aaVel.Dx, Is.EqualTo(stdVel.Dx));
        Assert.That(aaVel.Dy, Is.EqualTo(stdVel.Dy));
        aa.Dispose();
    }

    [Test]
    public void Versioned_EquivalentToStandardPath()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var stats = new VStats(55, 200);
        var id = tx.Spawn<VUnit>(VUnit.Stats.Set(in stats));
        tx.Commit();

        // Read via standard path
        using var stdTx = dbe.CreateQuickTransaction();
        ref readonly var stdStats = ref stdTx.Open(id).Read(VUnit.Stats);

        // Read via ArchetypeAccessor
        using var aaTx = dbe.CreateQuickTransaction();
        var aa = aaTx.For<VUnit>();
        ref readonly var aaStats = ref aa.Open(id).Read(VUnit.Stats);

        Assert.That(aaStats.Health, Is.EqualTo(stdStats.Health));
        Assert.That(aaStats.MaxHealth, Is.EqualTo(stdStats.MaxHealth));
        aa.Dispose();
    }
}
