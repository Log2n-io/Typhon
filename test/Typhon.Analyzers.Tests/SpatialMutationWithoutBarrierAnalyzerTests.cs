using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;
using Typhon.Analyzers;

namespace Typhon.Analyzers.Tests;

/// <summary>
/// Tests for <see cref="SpatialMutationWithoutBarrierAnalyzer"/> (TYPHON009). Focus: bug E — <c>HasSpatialIndexField</c> only scanned a component's DIRECT
/// members, so a component that embeds a struct carrying the <c>[SpatialIndex]</c> field was missed and no diagnostic fired.
/// </summary>
[TestFixture]
class SpatialMutationWithoutBarrierAnalyzerTests
{
    // Stub declarations the analyzer binds against: it resolves the SpatialIndexAttribute by its fully-qualified name
    // "Typhon.Schema.Definition.SpatialIndexAttribute" and the receiver type by the simple name "ClusterRef".
    private const string Stubs = @"
namespace Typhon.Schema.Definition
{
    public sealed class SpatialIndexAttribute : System.Attribute
    {
        public SpatialIndexAttribute(float margin) { }
    }
}

public struct Comp<T> { }

public ref struct ClusterRef<TArchetype>
{
    public System.Span<T> GetSpan<T>(Comp<T> comp) => default;
    public System.Span<T> GetReadOnlySpan<T>(Comp<T> comp) => default;
}
";

    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(string testSource)
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Span<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            new[]
            {
                CSharpSyntaxTree.ParseText(Stubs),
                CSharpSyntaxTree.ParseText(testSource),
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var withAnalyzer = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new SpatialMutationWithoutBarrierAnalyzer()));

        var diagnostics = await withAnalyzer.GetAnalyzerDiagnosticsAsync();
        return diagnostics
            .Where(d => d.Id == SpatialMutationWithoutBarrierAnalyzer.DiagnosticId)
            .ToImmutableArray();
    }

    [Test]
    public async Task DirectSpatialField_ProducesDiagnostic()
    {
        // Baseline: the spatial-indexed field sits directly on the component type — must fire.
        const string source = @"
using Typhon.Schema.Definition;

public struct DirectComp
{
    [SpatialIndex(1.0f)]
    public int Bounds;
}

public static class Caller
{
    public static void Use(ClusterRef<object> cluster, Comp<DirectComp> comp)
    {
        var span = cluster.GetSpan(comp);
    }
}
";
        var diagnostics = await RunAnalyzerAsync(source);
        Assert.That(diagnostics, Has.Length.EqualTo(1), "TYPHON009 must fire for a directly spatial-indexed component.");
    }

    [Test]
    public async Task NestedSpatialField_InEmbeddedStruct_ProducesDiagnostic()
    {
        // Bug E: the component embeds a struct that carries the [SpatialIndex] field. Pre-fix HasSpatialIndexField scanned only direct members and missed it.
        const string source = @"
using Typhon.Schema.Definition;

public struct SpatialPayload
{
    [SpatialIndex(1.0f)]
    public int Bounds;
}

public struct NestedComp
{
    public SpatialPayload Payload;
    public int Tag;
}

public static class Caller
{
    public static void Use(ClusterRef<object> cluster, Comp<NestedComp> comp)
    {
        var span = cluster.GetSpan(comp);
    }
}
";
        var diagnostics = await RunAnalyzerAsync(source);
        Assert.That(diagnostics, Has.Length.EqualTo(1),
            "TYPHON009 must fire when the spatial-indexed field lives inside an embedded struct field of the component.");
    }

    [Test]
    public async Task DeeplyNestedSpatialField_ProducesDiagnostic()
    {
        // Two levels of struct nesting — the recursion must descend through both.
        const string source = @"
using Typhon.Schema.Definition;

public struct Inner
{
    [SpatialIndex(1.0f)]
    public int Bounds;
}

public struct Middle
{
    public Inner Inner;
}

public struct Outer
{
    public Middle Middle;
}

public static class Caller
{
    public static void Use(ClusterRef<object> cluster, Comp<Outer> comp)
    {
        var span = cluster.GetSpan(comp);
    }
}
";
        var diagnostics = await RunAnalyzerAsync(source);
        Assert.That(diagnostics, Has.Length.EqualTo(1),
            "TYPHON009 must fire when the spatial-indexed field is nested two struct levels deep.");
    }

    [Test]
    public async Task NoSpatialField_ProducesNoDiagnostic()
    {
        // Negative control: nothing spatial anywhere → no diagnostic.
        const string source = @"
public struct PlainComp
{
    public int A;
    public float B;
}

public static class Caller
{
    public static void Use(ClusterRef<object> cluster, Comp<PlainComp> comp)
    {
        var span = cluster.GetSpan(comp);
    }
}
";
        var diagnostics = await RunAnalyzerAsync(source);
        Assert.That(diagnostics, Is.Empty, "TYPHON009 must not fire for a component with no spatial-indexed field anywhere.");
    }
}
