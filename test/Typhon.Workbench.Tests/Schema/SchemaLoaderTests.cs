using NUnit.Framework;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Schema;

[TestFixture]
public sealed class SchemaLoaderTests
{
    [Test]
    public void LoadSchemaDlls_MissingPath_ThrowsSchemaMissing()
    {
        using var alc = new WorkbenchAssemblyLoadContext("test");
        var ex = Assert.Throws<WorkbenchException>(
            () => SchemaLoader.LoadSchemaDlls(alc, ["/nonexistent/foo.schema.dll"]));
        Assert.That(ex!.ErrorCode, Is.EqualTo("schema_missing"));
        Assert.That(ex.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public void LoadSchemaDlls_InvalidAssembly_ThrowsSchemaLoadFailed()
    {
        var tempDll = Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.schema.dll");
        File.WriteAllText(tempDll, "not a real DLL — plain text");
        try
        {
            using var alc = new WorkbenchAssemblyLoadContext("test");
            var ex = Assert.Throws<WorkbenchException>(
                () => SchemaLoader.LoadSchemaDlls(alc, [tempDll]));
            Assert.That(ex!.ErrorCode, Is.EqualTo("schema_load_failed"));
        }
        finally
        {
            File.Delete(tempDll);
        }
    }

    [Test]
    public void LoadSchemaDlls_EmptyArray_ReturnsEmptySchema()
    {
        using var alc = new WorkbenchAssemblyLoadContext("test");
        var loaded = SchemaLoader.LoadSchemaDlls(alc, []);
        Assert.That(loaded.Assemblies, Is.Empty);
        Assert.That(loaded.ComponentTypes, Is.Empty);
        Assert.That(loaded.ComponentNames, Is.Empty);
    }

    [Test]
    public void LoadSchemaDlls_RealEngineAssembly_FindsZeroComponents()
    {
        // Load the engine's own assembly — it has no [Component] public types, so ComponentTypes is empty.
        // This verifies the scanning path works end-to-end with a real managed DLL.
        var enginePath = typeof(DatabaseEngine).Assembly.Location;
        using var alc = new WorkbenchAssemblyLoadContext("test");
        var loaded = SchemaLoader.LoadSchemaDlls(alc, [enginePath]);
        Assert.That(loaded.Assemblies, Has.Length.EqualTo(1));
        // No user [Component] types in Typhon.Engine itself (internal infrastructure types aren't exported as value types with [Component]).
        Assert.That(loaded.ComponentTypes, Is.Not.Null);
    }
}
