using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// StorageMode test component types
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.SM.Versioned", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompSmVersioned
{
    public int Value;
    public int _pad;
    public CompSmVersioned(int v) { Value = v; }
}

[Component("Typhon.Test.SM.SingleVersion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CompSmSingleVersion
{
    public int Value;
    public int _pad;
    public CompSmSingleVersion(int v) { Value = v; }
}

[Component("Typhon.Test.SM.Transient", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct CompSmTransient
{
    public int Value;
    public int _pad;
    public CompSmTransient(int v) { Value = v; }
}

[Component("Typhon.Test.SM.TransientOverride", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct CompSmTransientOverride
{
    public int Value;
    public int _pad;
}

// ═══════════════════════════════════════════════════════════════════════

class StorageModeInfrastructureTests : TestBase<StorageModeInfrastructureTests>
{
    // ── Enum tests ──

    [Test]
    public void Enum_DefaultIsVersioned()
    {
        Assert.That(default(StorageMode), Is.EqualTo(StorageMode.Versioned));
    }

    [Test]
    public void Enum_ValuesAreDistinct()
    {
        Assert.That((byte)StorageMode.Versioned, Is.EqualTo(0));
        Assert.That((byte)StorageMode.SingleVersion, Is.EqualTo(1));
        Assert.That((byte)StorageMode.Transient, Is.EqualTo(2));
    }

    // ── Attribute tests ──

    [Test]
    public void Attribute_DefaultStorageMode_IsVersioned()
    {
        var attr = typeof(CompSmVersioned).GetCustomAttributes(typeof(ComponentAttribute), false);
        Assert.That(attr, Has.Length.EqualTo(1));
        Assert.That(((ComponentAttribute)attr[0]).StorageMode, Is.EqualTo(StorageMode.Versioned));
    }

    [Test]
    public void Attribute_ExplicitSV_IsRespected()
    {
        var attr = typeof(CompSmSingleVersion).GetCustomAttributes(typeof(ComponentAttribute), false);
        Assert.That(((ComponentAttribute)attr[0]).StorageMode, Is.EqualTo(StorageMode.SingleVersion));
    }

    [Test]
    public void Attribute_ExplicitTransient_IsRespected()
    {
        var attr = typeof(CompSmTransient).GetCustomAttributes(typeof(ComponentAttribute), false);
        Assert.That(((ComponentAttribute)attr[0]).StorageMode, Is.EqualTo(StorageMode.Transient));
    }

    // ── Registration tests: segment allocation ──

    [Test]
    public void Register_Versioned_AllSegmentsAllocated()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmVersioned>();

        var table = dbe.GetComponentTable<CompSmVersioned>();
        Assert.That(table, Is.Not.Null);
        Assert.That(table.StorageMode, Is.EqualTo(StorageMode.Versioned));
        Assert.That(table.ComponentSegment, Is.Not.Null);
        Assert.That(table.CompRevTableSegment, Is.Not.Null);
        Assert.That(table.DefaultIndexSegment, Is.Not.Null);
        Assert.That(table.TransientComponentSegment, Is.Null);
    }

    [Test]
    public void Register_SV_NoCompRevTableSegment()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();

        var table = dbe.GetComponentTable<CompSmSingleVersion>();
        Assert.That(table, Is.Not.Null);
        Assert.That(table.StorageMode, Is.EqualTo(StorageMode.SingleVersion));
        Assert.That(table.ComponentSegment, Is.Not.Null, "SV uses PersistentStore (MMF checkpoint)");
        Assert.That(table.CompRevTableSegment, Is.Null, "SV has no revision chains");
        Assert.That(table.DefaultIndexSegment, Is.Not.Null);
        Assert.That(table.TransientComponentSegment, Is.Null);
    }

    [Test]
    public void Register_Transient_TransientSegmentsAllocated()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();

        var table = dbe.GetComponentTable<CompSmTransient>();
        Assert.That(table, Is.Not.Null);
        Assert.That(table.StorageMode, Is.EqualTo(StorageMode.Transient));

        // Persistent segments should be null
        Assert.That(table.ComponentSegment, Is.Null);
        Assert.That(table.CompRevTableSegment, Is.Null);
        Assert.That(table.DefaultIndexSegment, Is.Null);

        // Transient segments should be non-null
        Assert.That(table.TransientComponentSegment, Is.Not.Null);
        Assert.That(table.TransientDefaultIndexSegment, Is.Not.Null);
    }

    // (Removed Register_StorageModeOverride_OverridesAttribute: StorageMode is now fixed by the [Component] attribute per (name, revision) — there is no
    //  per-registration override. Changing a component's storage mode requires a new revision. See rules/ecs.md.)

    // ── Chunk allocation smoke tests ──

    [Test]
    public void SV_CanAllocateChunks()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var table = dbe.GetComponentTable<CompSmSingleVersion>();
        var chunkId = table.ComponentSegment.AllocateChunk(true);
        Assert.That(chunkId, Is.GreaterThan(0));
    }

    [Test]
    public void Transient_CanAllocateChunks()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var table = dbe.GetComponentTable<CompSmTransient>();
        var chunkId = table.TransientComponentSegment.AllocateChunk(true);
        Assert.That(chunkId, Is.GreaterThan(0));
    }

    [Test]
    public void Transient_NoWalTypeId()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();

        var table = dbe.GetComponentTable<CompSmTransient>();
        Assert.That(table.WalTypeId, Is.EqualTo(0), "Transient components have no WAL involvement");
    }

    // ── Schema persistence round-trip ──

    [Test]
    public void Schema_Persistence_SV_Roundtrip()
    {
        // Phase 1: Create database with SV component
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();

            var table = dbe.GetComponentTable<CompSmSingleVersion>();
            Assert.That(table.StorageMode, Is.EqualTo(StorageMode.SingleVersion));
        }

        // Phase 2: Reopen — verify StorageMode persisted in ComponentR1
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();

            var table = dbe.GetComponentTable<CompSmSingleVersion>();
            Assert.That(table.StorageMode, Is.EqualTo(StorageMode.SingleVersion));
            Assert.That(table.CompRevTableSegment, Is.Null, "SV reload should not create CompRevTableSegment");
            Assert.That(table.ComponentSegment, Is.Not.Null, "SV reload should load persistent ComponentSegment");
        }
    }

    [Test]
    public void Schema_Persistence_Transient_Roundtrip()
    {
        // Phase 1: Create database with Transient component, allocate a chunk
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompSmTransient>();

            var table = dbe.GetComponentTable<CompSmTransient>();
            using (var guard = EpochGuard.Enter(dbe.EpochManager))
            {
                var chunkId = table.TransientComponentSegment.AllocateChunk(true);
                Assert.That(chunkId, Is.GreaterThan(0));
            }
        }

        // Phase 2: Reopen — Transient creates a fresh empty table (data lost, schema survives)
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompSmTransient>();

            var table = dbe.GetComponentTable<CompSmTransient>();
            Assert.That(table.StorageMode, Is.EqualTo(StorageMode.Transient));
            Assert.That(table.TransientComponentSegment, Is.Not.Null, "Transient recreated fresh on reload");
            // PK B+Tree removed — just verify transient segments are fresh
            Assert.That(table.ComponentSegment, Is.Null, "Transient should have no persistent segments");
        }
    }
}
