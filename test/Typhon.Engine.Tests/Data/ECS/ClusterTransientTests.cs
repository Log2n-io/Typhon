using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test components for Cluster + Transient (Phase 6)
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClT6.SvPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClT6SvPos
{
    public float X, Y;
    public ClT6SvPos(float x, float y) { X = x; Y = y; }
}

[Component("Typhon.Test.ClT6.TransVel", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct ClT6TransVel
{
    public float VX, VY;
    public ClT6TransVel(float vx, float vy) { VX = vx; VY = vy; }
}

[Component("Typhon.Test.ClT6.TransTag", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct ClT6TransTag
{
    public int Value;
    public int _pad;
    public ClT6TransTag(int v) { Value = v; }
}

[Component("Typhon.Test.ClT6.Health", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct ClT6VHealth
{
    public int Current;
    public int _pad;
    public ClT6VHealth(int v) { Current = v; }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Test archetypes
// ═══════════════════════════════════════════════════════════════════════════════

[Archetype(620)]
partial class ClT6MixedSvT : Archetype<ClT6MixedSvT>
{
    public static readonly Comp<ClT6SvPos> Pos = Register<ClT6SvPos>();
    public static readonly Comp<ClT6TransVel> Vel = Register<ClT6TransVel>();
}

[Archetype(621)]
partial class ClT6PureT : Archetype<ClT6PureT>
{
    public static readonly Comp<ClT6TransVel> Vel = Register<ClT6TransVel>();
    public static readonly Comp<ClT6TransTag> Tag = Register<ClT6TransTag>();
}

[Archetype(622)]
partial class ClT6ThreeWay : Archetype<ClT6ThreeWay>
{
    public static readonly Comp<ClT6SvPos> Pos = Register<ClT6SvPos>();
    public static readonly Comp<ClT6TransVel> Vel = Register<ClT6TransVel>();
    public static readonly Comp<ClT6VHealth> Health = Register<ClT6VHealth>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
class ClusterTransientTests : TestBase<ClusterTransientTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClT6SvPos>();
        dbe.RegisterComponentFromAccessor<ClT6TransVel>();
        dbe.RegisterComponentFromAccessor<ClT6TransTag>();
        dbe.RegisterComponentFromAccessor<ClT6VHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1 — Eligibility
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MixedSvTransient_IsClusterEligible()
    {
        using var dbe = SetupEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClT6MixedSvT>();
        Assert.That(meta.IsClusterEligible, Is.True);
        Assert.That(meta.TransientSlotMask, Is.Not.EqualTo(0));
        Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState, Is.Not.Null);
        Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState.ClusterSegment, Is.Not.Null, "Mixed has PersistentStore segment");
        Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState.TransientSegment, Is.Not.Null, "Mixed has TransientStore segment");
    }

    [Test]
    public void PureTransient_IsClusterEligible()
    {
        using var dbe = SetupEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClT6PureT>();
        Assert.That(meta.IsClusterEligible, Is.True);
        Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState, Is.Not.Null);
        Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState.ClusterSegment, Is.Null, "Pure-T has no PersistentStore segment");
        Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState.TransientSegment, Is.Not.Null, "Pure-T has TransientStore segment");
    }

    [Test]
    public void ThreeWay_IsClusterEligible()
    {
        using var dbe = SetupEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClT6ThreeWay>();
        Assert.That(meta.IsClusterEligible, Is.True);
        Assert.That(meta.VersionedSlotMask, Is.Not.EqualTo(0));
        Assert.That(meta.TransientSlotMask, Is.Not.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2 — Spawn and Read
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MixedSvT_SpawnAndRead_BothComponents()
    {
        using var dbe = SetupEngine();
        var pos = new ClT6SvPos(10f, 20f);
        var vel = new ClT6TransVel(1f, 2f);

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClT6MixedSvT>(ClT6MixedSvT.Pos.Set(in pos), ClT6MixedSvT.Vel.Set(in vel));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            ref readonly var readPos = ref entity.Read(ClT6MixedSvT.Pos);
            ref readonly var readVel = ref entity.Read(ClT6MixedSvT.Vel);
            Assert.That(readPos.X, Is.EqualTo(10f));
            Assert.That(readPos.Y, Is.EqualTo(20f));
            Assert.That(readVel.VX, Is.EqualTo(1f));
            Assert.That(readVel.VY, Is.EqualTo(2f));
        }
    }

    [Test]
    public void PureTransient_SpawnAndRead()
    {
        using var dbe = SetupEngine();
        var vel = new ClT6TransVel(3f, 4f);
        var tag = new ClT6TransTag(42);

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClT6PureT>(ClT6PureT.Vel.Set(in vel), ClT6PureT.Tag.Set(in tag));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            ref readonly var readVel = ref entity.Read(ClT6PureT.Vel);
            ref readonly var readTag = ref entity.Read(ClT6PureT.Tag);
            Assert.That(readVel.VX, Is.EqualTo(3f));
            Assert.That(readVel.VY, Is.EqualTo(4f));
            Assert.That(readTag.Value, Is.EqualTo(42));
        }
    }

    [Test]
    public void ThreeWay_SpawnAndRead_AllThree()
    {
        using var dbe = SetupEngine();
        var pos = new ClT6SvPos(1f, 2f);
        var vel = new ClT6TransVel(3f, 4f);
        var hp = new ClT6VHealth(100);

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClT6ThreeWay>(
                ClT6ThreeWay.Pos.Set(in pos),
                ClT6ThreeWay.Vel.Set(in vel),
                ClT6ThreeWay.Health.Set(in hp));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            Assert.That(entity.Read(ClT6ThreeWay.Pos).X, Is.EqualTo(1f));
            Assert.That(entity.Read(ClT6ThreeWay.Vel).VX, Is.EqualTo(3f));
            Assert.That(entity.Read(ClT6ThreeWay.Health).Current, Is.EqualTo(100));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3 — Write
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Transient_Write_InPlace()
    {
        using var dbe = SetupEngine();
        var pos = new ClT6SvPos(1f, 2f);
        var vel = new ClT6TransVel(10f, 20f);

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClT6MixedSvT>(ClT6MixedSvT.Pos.Set(in pos), ClT6MixedSvT.Vel.Set(in vel));
            tx.Commit();
        }

        // Write to Transient component
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(ClT6MixedSvT.Vel) = new ClT6TransVel(99f, 88f);
            tx.Commit();
        }

        // Verify updated value
        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            Assert.That(entity.Read(ClT6MixedSvT.Vel).VX, Is.EqualTo(99f));
            Assert.That(entity.Read(ClT6MixedSvT.Vel).VY, Is.EqualTo(88f));
            // SV unchanged
            Assert.That(entity.Read(ClT6MixedSvT.Pos).X, Is.EqualTo(1f));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4 — Bulk iteration
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void BulkIteration_DualSegment_CorrectValues()
    {
        using var dbe = SetupEngine();
        const int count = 100;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var pos = new ClT6SvPos(i, i * 10f);
                var vel = new ClT6TransVel(i + 0.5f, i + 1.5f);
                tx.Spawn<ClT6MixedSvT>(ClT6MixedSvT.Pos.Set(in pos), ClT6MixedSvT.Vel.Set(in vel));
            }
            tx.Commit();
        }

        // Iterate via cluster enumerator
        using var txView = dbe.CreateQuickTransaction();
        using var ants = txView.For<ClT6MixedSvT>();
        using var clusters = ants.GetClusterEnumerator();
        int total = 0;
        foreach (var cluster in clusters)
        {
            ulong bits = cluster.OccupancyBits;
            var positions = cluster.GetReadOnlySpan(ClT6MixedSvT.Pos);
            var velocities = cluster.GetReadOnlySpan(ClT6MixedSvT.Vel);
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                // Verify data is readable and consistent
                Assert.That(positions[idx].Y, Is.EqualTo(positions[idx].X * 10f));
                Assert.That(velocities[idx].VY, Is.EqualTo(velocities[idx].VX + 1f));
                total++;
            }
        }
        Assert.That(total, Is.EqualTo(count));
    }

    [Test]
    public void BulkIteration_PureTransient_CorrectValues()
    {
        using var dbe = SetupEngine();
        const int count = 50;

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var vel = new ClT6TransVel(i, i * 2f);
                var tag = new ClT6TransTag(i * 100);
                tx.Spawn<ClT6PureT>(ClT6PureT.Vel.Set(in vel), ClT6PureT.Tag.Set(in tag));
            }
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var ants = txView.For<ClT6PureT>();
        using var clusters = ants.GetClusterEnumerator();
        int total = 0;
        foreach (var cluster in clusters)
        {
            ulong bits = cluster.OccupancyBits;
            var vels = cluster.GetReadOnlySpan(ClT6PureT.Vel);
            var tags = cluster.GetReadOnlySpan(ClT6PureT.Tag);
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                Assert.That(tags[idx].Value, Is.EqualTo((int)(vels[idx].VX * 100)));
                total++;
            }
        }
        Assert.That(total, Is.EqualTo(count));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5 — Destroy
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_BothSegmentsFreed()
    {
        using var dbe = SetupEngine();
        var pos = new ClT6SvPos(1f, 2f);
        var vel = new ClT6TransVel(3f, 4f);

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClT6MixedSvT>(ClT6MixedSvT.Pos.Set(in pos), ClT6MixedSvT.Vel.Set(in vel));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        // Entity should not be openable
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.TryOpen(id, out _), Is.False);
        }
    }

    [Test]
    public void Destroy_PureTransient()
    {
        using var dbe = SetupEngine();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var vel = new ClT6TransVel(1f, 2f);
            var tag = new ClT6TransTag(99);
            id = tx.Spawn<ClT6PureT>(ClT6PureT.Vel.Set(in vel), ClT6PureT.Tag.Set(in tag));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.TryOpen(id, out _), Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6 — Many entities across clusters
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ManyEntities_MultiCluster()
    {
        using var dbe = SetupEngine();
        const int count = 500;
        var ids = new EntityId[count];

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var pos = new ClT6SvPos(i, i);
                var vel = new ClT6TransVel(i + 0.1f, i + 0.2f);
                ids[i] = tx.Spawn<ClT6MixedSvT>(ClT6MixedSvT.Pos.Set(in pos), ClT6MixedSvT.Vel.Set(in vel));
            }
            tx.Commit();
        }

        // Verify all entities readable
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < count; i++)
            {
                var entity = tx.Open(ids[i]);
                Assert.That(entity.Read(ClT6MixedSvT.Pos).X, Is.EqualTo((float)i));
                Assert.That(entity.Read(ClT6MixedSvT.Vel).VX, Is.EqualTo(i + 0.1f));
            }
        }

        // Verify cluster count > 1 (N is typically 16-32 for small components)
        var meta = ArchetypeRegistry.GetMetadata<ClT6MixedSvT>();
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(1), "Should span multiple clusters");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7 — Dirty tracking
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DirtyBit_SetForTransientWrite()
    {
        using var dbe = SetupEngine();
        var pos = new ClT6SvPos(1f, 2f);
        var vel = new ClT6TransVel(3f, 4f);

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClT6MixedSvT>(ClT6MixedSvT.Pos.Set(in pos), ClT6MixedSvT.Vel.Set(in vel));
            tx.Commit();
        }

        var meta = ArchetypeRegistry.GetMetadata<ClT6MixedSvT>();
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        // Write to Transient component
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(ClT6MixedSvT.Vel) = new ClT6TransVel(99f, 88f);
            tx.Commit();
        }

        // Cluster dirty bitmap should be set
        Assert.That(cs.ClusterDirtyBitmap.HasDirty, Is.True, "Transient write should set cluster dirty bitmap");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8 — PureTransient segment validation
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void PureTransient_NoPageCacheSegment()
    {
        using var dbe = SetupEngine();
        var meta = ArchetypeRegistry.GetMetadata<ClT6PureT>();
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        Assert.That(cs.ClusterSegment, Is.Null, "Pure-Transient should not have PersistentStore cluster segment");
        Assert.That(cs.TransientSegment, Is.Not.Null, "Pure-Transient should have TransientStore cluster segment");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 9 — Three-way write + Versioned chain walk
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ThreeWay_WriteAll_CorrectValues()
    {
        using var dbe = SetupEngine();
        var pos = new ClT6SvPos(1f, 2f);
        var vel = new ClT6TransVel(3f, 4f);
        var hp = new ClT6VHealth(50);

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<ClT6ThreeWay>(
                ClT6ThreeWay.Pos.Set(in pos),
                ClT6ThreeWay.Vel.Set(in vel),
                ClT6ThreeWay.Health.Set(in hp));
            tx.Commit();
        }

        // Write all three modes
        using (var tx = dbe.CreateQuickTransaction())
        {
            var e = tx.OpenMut(id);
            e.Write(ClT6ThreeWay.Pos) = new ClT6SvPos(10f, 20f);      // SV: in-place
            e.Write(ClT6ThreeWay.Vel) = new ClT6TransVel(30f, 40f);    // Transient: in-place (different segment)
            e.Write(ClT6ThreeWay.Health) = new ClT6VHealth(999);        // Versioned: COW
            tx.Commit();
        }

        // Verify all three updated
        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.Open(id);
            Assert.That(entity.Read(ClT6ThreeWay.Pos).X, Is.EqualTo(10f));
            Assert.That(entity.Read(ClT6ThreeWay.Vel).VX, Is.EqualTo(30f));
            Assert.That(entity.Read(ClT6ThreeWay.Health).Current, Is.EqualTo(999));
        }
    }
}
