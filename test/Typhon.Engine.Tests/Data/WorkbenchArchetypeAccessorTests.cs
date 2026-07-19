using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests;

class WorkbenchArchetypeAccessorTests : TestBase<WorkbenchArchetypeAccessorTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    [Test]
    public void GetAllArchetypes_ReturnsRegisteredArchetypes()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var archetypes = ArchetypeRegistry.GetAllArchetypes().ToArray();

        Assert.That(archetypes.Length, Is.GreaterThan(0), "At least one archetype should be registered");
        // Our test archetypes declared in TestBase should appear.
        var ids = archetypes.Select(a => (int)a.ArchetypeId).ToArray();
        Assert.That(ids, Does.Contain(200), "CompAArch (id 200) should be registered");
    }

    [Test]
    public void ArchetypeMetadata_GetComponentTypes_ReturnsExpectedTypes()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var compAArch = ArchetypeRegistry.GetAllArchetypes().Single(a => a.ArchetypeId == 200);

        var types = compAArch.GetComponentTypes().ToArray();
        Assert.That(types, Does.Contain(typeof(CompA)), "CompAArch should contain CompA");
    }

    [Test]
    public void GetArchetypeEntityCount_EmptyArchetype_ReturnsZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var count = dbe.GetArchetypeEntityCount(200);
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void GetArchetypeEntityCount_UnknownArchetype_ReturnsZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var count = dbe.GetArchetypeEntityCount(9999);
        Assert.That(count, Is.EqualTo(0), "Unknown archetype id must not throw — external tooling may query arbitrary ids");
    }
}
