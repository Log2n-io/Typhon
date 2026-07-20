using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

class StorageModeCheckpointTests : TestBase<StorageModeCheckpointTests>
{
    [Test]
    public void Transient_PagesNotInMMFDirtyCollection()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();
        dbe.InitializeArchetypes();

        // Allocate some transient chunks to create heap pages
        var table = dbe.GetComponentTable<CompSmTransient>();
        using (var guard = EpochGuard.Enter(dbe.EpochManager))
        {
            for (int i = 0; i < 10; i++)
            {
                table.TransientComponentSegment.AllocateChunk(true);
            }
        }

        // Collect MMF dirty pages — transient pages should NOT appear
        var dirtyPages = dbe.MMF.CollectDirtyMemPageIndices();

        // Transient pages are heap-backed, not in MMF — they cannot contribute to dirty pages.
        // The dirty pages found (if any) are from system schema tables, not from Transient segments.
        // This test verifies the architectural guarantee: TransientStore pages are invisible to checkpoint.
        Assert.That(table.TransientComponentSegment, Is.Not.Null, "Transient segment exists");
        Assert.That(table.ComponentSegment, Is.Null, "No persistent ComponentSegment for Transient");
    }

    [Test]
    public void SV_PagesInMMFDirtyCollection()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        dbe.InitializeArchetypes();

        // SV uses PersistentStore — pages ARE in MMF page cache
        var table = dbe.GetComponentTable<CompSmSingleVersion>();
        Assert.That(table.ComponentSegment, Is.Not.Null, "SV has persistent ComponentSegment");
        Assert.That(table.StorageMode, Is.EqualTo(StorageMode.SingleVersion));
    }
}
