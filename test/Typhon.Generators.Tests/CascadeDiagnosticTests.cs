using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Typhon.Generators;

namespace Typhon.Generators.Tests;

/// <summary>
/// Tests for the build-time cascade-delete diagnostic of <see cref="ArchetypeAccessorGenerator"/> (feature #514, phase 6, AC6.2): cascade cycles (TPH1001)
/// and diamonds (TPH1002) visible within the compilation are compile errors, mirroring the runtime <c>ValidateCascadeDfs</c>. A legit tree produces no diagnostic.
/// </summary>
[TestFixture]
class CascadeDiagnosticTests
{
    private const string Stubs = @"
namespace Typhon.Schema.Definition
{
    public sealed class ArchetypeAttribute : System.Attribute { public ArchetypeAttribute(int id) { } }
    public sealed class ComponentAttribute : System.Attribute { public ComponentAttribute(string name, int revision) { } }
    public enum CascadeAction { None = 0, Delete = 1 }
    public sealed class IndexAttribute : System.Attribute { public bool AllowMultiple { get; set; } public CascadeAction OnParentDelete { get; set; } }
}
namespace Typhon.Engine
{
    public struct Comp<T> { }
    public readonly struct EntityLink<T> where T : class { }
    public abstract class Archetype<TSelf> where TSelf : Archetype<TSelf> { protected static Comp<T> Register<T>() => default; }
    public abstract class Archetype<TSelf, TParent> : Archetype<TSelf> where TSelf : Archetype<TSelf, TParent> where TParent : class { }
}
";

    private static ImmutableArray<Diagnostic> RunDiagnostics(string testSource)
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "CascadeDiagnosticTestAssembly",
            new[] { CSharpSyntaxTree.ParseText(Stubs), CSharpSyntaxTree.ParseText(testSource) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new ArchetypeAccessorGenerator().AsSourceGenerator());
        return driver.RunGenerators(compilation).GetRunResult().Diagnostics;
    }

    [Test]
    public void LegitCascadeTree_ProducesNoDiagnostic()
    {
        // Parent → Child (single edge). No cycle, no diamond.
        const string source = @"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype(1)] public partial class ParentArch : Archetype<ParentArch> { public static readonly Comp<PData> D = Register<PData>(); }
[Archetype(2)] public partial class ChildArch : Archetype<ChildArch> { public static readonly Comp<CData> D = Register<CData>(); }

[Component(""Test.PData"", 1)] public struct PData { public int V; public int Pad; }
[Component(""Test.CData"", 1)] public struct CData { [Index(OnParentDelete = CascadeAction.Delete)] public EntityLink<ParentArch> Link; public int Pad; }
";
        var diags = RunDiagnostics(source);
        Assert.That(diags.Where(d => d.Id is "TPH1001" or "TPH1002"), Is.Empty,
            "A legit cascade tree must not produce a cascade diagnostic. Got: " + string.Join("; ", diags.Select(d => d.ToString())));
    }

    [Test]
    public void CascadeCycle_ReportsTph1001()
    {
        // A ↔ B: each archetype's component links to the other with OnParentDelete=Delete → cycle.
        const string source = @"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype(1)] public partial class AArch : Archetype<AArch> { public static readonly Comp<AData> D = Register<AData>(); }
[Archetype(2)] public partial class BArch : Archetype<BArch> { public static readonly Comp<BData> D = Register<BData>(); }

[Component(""Test.AData"", 1)] public struct AData { [Index(OnParentDelete = CascadeAction.Delete)] public EntityLink<BArch> Link; public int Pad; }
[Component(""Test.BData"", 1)] public struct BData { [Index(OnParentDelete = CascadeAction.Delete)] public EntityLink<AArch> Link; public int Pad; }
";
        var diags = RunDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TPH1001"), Is.True,
            "A cascade cycle must report TPH1001. Got: " + string.Join("; ", diags.Select(d => d.ToString())));
    }

    [Test]
    public void CascadeDiamond_ReportsTph1002()
    {
        // Root → L, Root → R, L → Leaf, R → Leaf: Leaf reachable via two paths from Root → diamond.
        const string source = @"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype(1)] public partial class RootArch : Archetype<RootArch> { public static readonly Comp<RootData> D = Register<RootData>(); }
[Archetype(2)] public partial class LArch : Archetype<LArch> { public static readonly Comp<LData> D = Register<LData>(); }
[Archetype(3)] public partial class RArch : Archetype<RArch> { public static readonly Comp<RData> D = Register<RData>(); }
[Archetype(4)] public partial class LeafArch : Archetype<LeafArch> { public static readonly Comp<LeafFromL> A = Register<LeafFromL>(); public static readonly Comp<LeafFromR> B = Register<LeafFromR>(); }

[Component(""Test.RootData"", 1)] public struct RootData { public int V; public int Pad; }
[Component(""Test.LData"", 1)] public struct LData { [Index(OnParentDelete = CascadeAction.Delete)] public EntityLink<RootArch> Link; public int Pad; }
[Component(""Test.RData"", 1)] public struct RData { [Index(OnParentDelete = CascadeAction.Delete)] public EntityLink<RootArch> Link; public int Pad; }
[Component(""Test.LeafFromL"", 1)] public struct LeafFromL { [Index(OnParentDelete = CascadeAction.Delete)] public EntityLink<LArch> Link; public int Pad; }
[Component(""Test.LeafFromR"", 1)] public struct LeafFromR { [Index(OnParentDelete = CascadeAction.Delete)] public EntityLink<RArch> Link; public int Pad; }
";
        var diags = RunDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TPH1002"), Is.True,
            "A cascade diamond must report TPH1002. Got: " + string.Join("; ", diags.Select(d => d.ToString())));
    }
}
