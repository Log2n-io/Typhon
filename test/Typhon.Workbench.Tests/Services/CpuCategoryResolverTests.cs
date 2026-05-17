using NUnit.Framework;
using Typhon.Workbench.Services;

namespace Typhon.Workbench.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CpuCategoryResolver"/> (#351 §8.6) — the file/method → subsystem-category mapping that
/// drives the Call Tree panel's category breakdown.
/// </summary>
[TestFixture]
public sealed class CpuCategoryResolverTests
{
    [Test]
    public void Resolve_EngineSourcePath_YieldsFirstSegmentUnderEngineSrc()
    {
        var r = new CpuCategoryResolver();
        var id = r.Resolve("/_/src/Typhon.Engine/Ecs/ComponentTable.cs", "Foo.Bar");
        Assert.That(r.Categories[id], Is.EqualTo("Ecs"));
    }

    [Test]
    public void Resolve_BackslashEnginePath_NormalizesSeparators()
    {
        var r = new CpuCategoryResolver();
        var id = r.Resolve(@"C:\Dev\Typhon\src\Typhon.Engine\Storage\PagedMmf.cs", "Foo");
        Assert.That(r.Categories[id], Is.EqualTo("Storage"));
    }

    [Test]
    public void Resolve_NonEngineSourcePath_BucketsAsUser()
    {
        var r = new CpuCategoryResolver();
        var id = r.Resolve("demo/AntHill/AntHill.Core/Ai/AntBrain.cs", "AntHill.AntBrain.Step");
        Assert.That(r.Categories[id], Is.EqualTo("User"));
    }

    [Test]
    public void Resolve_NoSource_SystemMethod_BucketsAsBcl()
    {
        var r = new CpuCategoryResolver();
        var id = r.Resolve("", "System.Threading.Thread.Sleep");
        Assert.That(r.Categories[id], Is.EqualTo("BCL"));
    }

    [Test]
    public void Resolve_NoSource_ModuleBangSymbol_BucketsAsNative()
    {
        var r = new CpuCategoryResolver();
        var id = r.Resolve("", "coreclr!SomeNativeRoutine");
        Assert.That(r.Categories[id], Is.EqualTo("Native"));
    }

    [Test]
    public void Resolve_NoSource_UnclassifiableMethod_BucketsAsUnknown()
    {
        var r = new CpuCategoryResolver();
        var id = r.Resolve("", "Anonymous.Lambda");
        Assert.That(r.Categories[id], Is.EqualTo("Unknown"));
    }

    [Test]
    public void Resolve_SameCategoryTwice_InternsToOneId()
    {
        var r = new CpuCategoryResolver();
        var first = r.Resolve("src/Typhon.Engine/Ecs/A.cs", "A");
        var second = r.Resolve("src/Typhon.Engine/Ecs/B.cs", "B");
        Assert.That(second, Is.EqualTo(first));
        Assert.That(r.Categories, Has.Count.EqualTo(1));
    }
}
