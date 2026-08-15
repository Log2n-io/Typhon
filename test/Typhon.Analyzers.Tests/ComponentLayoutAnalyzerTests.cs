using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Typhon.Analyzers.Tests;

/// <summary>
/// Tests for <see cref="ComponentLayoutAnalyzer"/> (TYPHON010 + TYPHON011). The analyzer reimplements sequential struct layout, so the cases below pin the
/// byte counts it reports — each expected size was measured against the real runtime with <c>Unsafe.SizeOf&lt;T&gt;</c> before being written down here.
/// <para>
/// TYPHON010's baseline is what the struct would measure at <c>Pack = 4</c>, rounded up to 4 — not where its last field ends, which would make the diagnostic
/// depend on field ORDER. So <c>{ AABB2F; byte }</c> (20 bytes, packs to 17 → 20) is accepted while both <c>{ long; int }</c> and <c>{ int; long }</c>
/// (16 bytes, pack to 12) are reported.
/// </para>
/// </summary>
[TestFixture]
class ComponentLayoutAnalyzerTests
{
    // The analyzer resolves [Component] by its fully-qualified name and nothing else, so a one-line stub is the whole dependency surface.
    private const string Stubs = @"
namespace Typhon.Schema.Definition
{
    public sealed class ComponentAttribute : System.Attribute
    {
        public ComponentAttribute(string name, int revision) { }
    }
}
";

    private static Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(string testSource)
        => RunAnalyzerAsync(testSource, ComponentLayoutAnalyzer.PaddingDiagnosticId);

    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(string testSource, string diagnosticId)
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.InteropServices.StructLayoutAttribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            new[]
            {
                CSharpSyntaxTree.ParseText(Stubs),
                CSharpSyntaxTree.ParseText(testSource),
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Every "produces no diagnostic" assertion below is vacuous if the snippet simply failed to compile, and a stub that drifts out of sync with a test
        // source is exactly how that happens quietly. Fail loudly here instead.
        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        Assert.That(compileErrors, Is.Empty, $"test source did not compile: {string.Join("; ", compileErrors.Select(d => d.GetMessage()))}");

        var withAnalyzer = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ComponentLayoutAnalyzer()));

        var diagnostics = await withAnalyzer.GetAnalyzerDiagnosticsAsync();
        return diagnostics
            .Where(d => d.Id == diagnosticId)
            .ToImmutableArray();
    }

    private static string Component(string body) => @"
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;
" + body;

    // ═══════════════════════════════════════════════════════════════════════
    // Fires
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task NaturalTailPadding_ProducesDiagnostic()
    {
        // { long; int } — extent 12, rounded to 16 for the long's alignment. The shape 12 of this repo's 14 offenders had.
        var diagnostics = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
public struct Padded { public long A; public int B; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("is 16 bytes but packs to 12").And.Contain("Pack = 4"));
    }

    [Test]
    public async Task ExplicitOversize_ProducesDiagnostic()
    {
        // The #816 shape: one 4-byte field declared into an 8-byte struct. Deliberate or not, four bytes per entity carry nothing.
        var diagnostics = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct Padded { public int Hp; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("is 8 bytes but packs to 4"));

        // Pack cannot shrink a struct whose size the author declared outright, so the remedy names the declaration instead.
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("Lower Size to 4"));
    }

    [Test]
    public async Task NestedStructField_LayoutIsComputedThroughIt()
    {
        // Inner is 16 (12 rounded up); Outer places it at 0 and the int at 16, so the extent is 20 and the size 24. Measured: Unsafe.SizeOf<Outer>() == 24.
        var diagnostics = await RunAnalyzerAsync(Component(@"
public struct Inner { public long A; public int B; }

[Component(""T"", 1)]
public struct Outer { public Inner I; public int C; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("is 24 bytes but packs to 20"));
    }

    [Test]
    public async Task FixedSizeBufferField_CountsAsElementSizeTimesLength()
    {
        // The String64 shape. Buffer 0..64, long 64..72, int 72..76, alignment 8 → 80. Measured: Unsafe.SizeOf == 80.
        var diagnostics = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
public unsafe struct WithBuffer { public fixed byte D[64]; public long L; public int I; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("is 80 bytes but packs to 76"));
    }

    [Test]
    public async Task PaddingWithinAFourByteMultiple_ProducesNoDiagnostic()
    {
        // AntHill's Obstacle: four floats then a byte. Fields end at 17, the type is 20 — already a 4-byte multiple, so the 3 bytes are accepted and no
        // attribute is wanted. This is the case that separates "rounded to 4" (fine) from "rounded to 8" (reported).
        var diagnostics = await RunAnalyzerAsync(Component(@"
public struct Aabb2F { public float MinX, MinY, MaxX, MaxY; }

[Component(""T"", 1)]
public struct Obstacle { public Aabb2F Bounds; public byte Kind; }
"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task PackedLayout_ReportsAgainstThePackedSize()
    {
        // Pack caps every field's alignment: { long; int } at Pack=4 is 12 with no padding at all, so nothing fires. Pack=8 leaves the natural 16.
        var packed = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Packed { public long A; public int B; }
"));

        Assert.That(packed, Is.Empty, "Pack = 4 removes the padding as effectively as Size = 12");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Stays quiet
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SizePinnedToTheExtent_ProducesNoDiagnostic()
    {
        // Still accepted — Size is the remedy for a component whose field offsets are already on disk, since Pack would move them.
        var diagnostics = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
[StructLayout(LayoutKind.Sequential, Size = 12)]
public struct Tight { public long A; public int B; }
"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task NaturallyTightStruct_ProducesNoDiagnostic()
    {
        var diagnostics = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
public struct Tight { public int A; public int B; }
"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task StructWithoutComponentAttribute_ProducesNoDiagnostic()
    {
        // Padding only costs storage when the struct becomes a column; a plain struct is none of the analyzer's business.
        var diagnostics = await RunAnalyzerAsync(Component(@"
public struct NotAComponent { public long A; public int B; }
"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task ExplicitLayout_ProducesNoDiagnostic()
    {
        // Offsets are the author's, not the compiler's — the analyzer cannot predict the size, and a wrong Size in the message is worse than silence.
        var diagnostics = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct Overlaid
{
    [FieldOffset(0)] public long A;
    [FieldOffset(8)] public int B;
}
"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task UnmodelledFieldType_ProducesNoDiagnostic()
    {
        // A field the layout model cannot size (here a pointer, whose width is platform-dependent) must abort the whole computation rather than guess.
        var diagnostics = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
public unsafe struct WithPointer { public byte* P; public int B; }
"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task StaticAndConstMembers_AreNotPartOfTheLayout()
    {
        var diagnostics = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
public struct WithStatics
{
    public const string SchemaName = ""T"";
    public static int Shared;
    public int A;
    public int B;
}
"));

        Assert.That(diagnostics, Is.Empty, "only instance fields occupy storage");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TYPHON011 — managed vs marshalled layout
    // ═══════════════════════════════════════════════════════════════════════

    private static Task<ImmutableArray<Diagnostic>> RunDivergenceAsync(string testSource)
        => RunAnalyzerAsync(testSource, ComponentLayoutAnalyzer.MarshalDivergenceDiagnosticId);

    [Test]
    public async Task TwoBools_DivergeAndAreReported()
    {
        // Measured: managed A@0 B@1 C@4 (8 bytes); marshalled A@0 B@4 C@8 (12 bytes). The schema would record 4 and 8.
        var diagnostics = await RunDivergenceAsync(Component(@"
[Component(""T"", 1)]
public struct Flags { public bool A; public bool B; public int C; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("field 'B' occupies 1 at offset 1 in the managed layout but 4 at offset 4 in the marshalled one"));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Error));
    }

    [Test]
    public async Task TwoChars_DivergeWithIdenticalTotalSize()
    {
        // The case no runtime check can catch: managed A@0 B@2 C@4 and marshalled A@0 B@1 C@4 are BOTH 8 bytes total, only the middle offset differs.
        var diagnostics = await RunDivergenceAsync(Component(@"
[Component(""T"", 1)]
public struct Codes { public char A; public char B; public int C; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("field 'B' occupies 2 at offset 2 in the managed layout but 1 at offset 1 in the marshalled one"));
    }

    [Test]
    public async Task BoolInsideANestedStruct_IsStillReported()
    {
        // The walk recurses, so a divergence hidden one level down is not a way around the check.
        var diagnostics = await RunDivergenceAsync(Component(@"
public struct Inner { public bool A; public bool B; }

[Component(""T"", 1)]
public struct Outer { public Inner I; public int C; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task LoneBoolBeforeAnInt_DoesNotDiverge()
    {
        // Managed pads the bool out to the int's alignment anyway, so both layouts put C at 4. No false positive on the common single-flag shape.
        var diagnostics = await RunDivergenceAsync(Component(@"
[Component(""T"", 1)]
public struct OneFlag { public bool A; public int C; }
"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task ByteAndUshortSubstitutes_DoNotDiverge()
    {
        // The remedy the message recommends must itself be clean.
        var diagnostics = await RunDivergenceAsync(Component(@"
[Component(""T"", 1)]
public struct Substitutes { public byte A; public byte B; public ushort C; public ushort D; public int E; }
"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task DivergentComponent_ReportsOnlyTheDivergence_NotThePadding()
    {
        // { bool; bool; long } diverges AND pads. Reporting both would send the author to fix the padding of a layout that is wrong to begin with.
        var padding = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
public struct Both { public bool A; public bool B; public long C; }
"));

        Assert.That(padding, Is.Empty, "TYPHON010 stands down while TYPHON011 is outstanding");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Layout-model regressions — each of these was a defect found in review
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SelfReferentialStruct_DoesNotRecurseForever()
    {
        // `struct C { C Self; }` is CS0523, but analyzers run on invalid source and the symbol graph still has the cycle. Without a guard this is a stack
        // overflow, which cannot be caught and takes the compiler or the IDE down with it. The compile-error assertion in the harness is bypassed on purpose.
        var source = Component(@"
[Component(""T"", 1)]
public struct Cyclic { public Cyclic Self; public int X; }
");
        var compilation = CSharpCompilation.Create(
            "CyclicTestAssembly",
            new[] { CSharpSyntaxTree.ParseText(Stubs), CSharpSyntaxTree.ParseText(source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ComponentLayoutAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        Assert.That(diagnostics.Where(d => d.Id.StartsWith("TYPHON")), Is.Empty, "an unmodellable type must bail, not report");
    }

    [Test]
    public async Task EnumField_IsModelledRatherThanBailedOn()
    {
        // An enum is TypeKind.Enum, not TypeKind.Struct. Getting the order of those two checks wrong makes every component with an enum field silently
        // unanalysed — and enum fields are everywhere. { long; EInt } is the { long; int } shape, so it must report identically.
        var diagnostics = await RunAnalyzerAsync(Component(@"
public enum EInt { A }

[Component(""T"", 1)]
public struct WithEnum { public long A; public EInt B; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("is 16 bytes but packs to 12"));
    }

    [Test]
    public async Task CharSetUnicode_ReconcilesTheLayouts_NoDivergenceReported()
    {
        // Under CharSet.Unicode a char marshals as 2 bytes — the same as managed — so the layouts agree and TYPHON011 must stay silent. Reporting here would
        // be an ERROR on a correct struct, i.e. a broken build.
        var diagnostics = await RunDivergenceAsync(Component(@"
[Component(""T"", 1)]
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct Codes { public char A; public char B; public int C; }
"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task ExplicitMarshalAs_SuppressesTheDivergenceCheck()
    {
        // [MarshalAs] redefines the marshalled width; modelling every UnmanagedType is not something to guess at, so the marshalled pass abandons rather than
        // accusing a correctly-annotated field.
        var diagnostics = await RunDivergenceAsync(Component(@"
[Component(""T"", 1)]
public struct Flags
{
    [MarshalAs(UnmanagedType.U1)] public bool A;
    [MarshalAs(UnmanagedType.U1)] public bool B;
    public int C;
}
"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task DeclaredSize_DoesNotBlindTheDivergenceCheck()
    {
        // The marshalled extent (12) exceeds the declared Size (8). Gating the marshalled pass on "size >= extent" would treat that as unmodellable and let
        // the bool divergence through unreported.
        var diagnostics = await RunDivergenceAsync(Component(@"
[Component(""T"", 1)]
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct Flags { public bool A; public bool B; public int C; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("field 'B'"));
    }

    [Test]
    public async Task InteriorPadding_IsReportedJustLikeTrailingPadding()
    {
        // { int; long } and { long; int } are both 16 bytes carrying 12 bytes of payload, and both pack to 12. Measuring the distance to the end of the LAST
        // field would report only the second — making the diagnostic a function of field ORDER rather than of wasted storage.
        var diagnostics = await RunAnalyzerAsync(Component(@"
[Component(""T"", 1)]
public struct Interior { public int A; public long B; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("is 16 bytes but packs to 12"));
    }

    [Test]
    public async Task NestedDivergenceThatShiftsNothing_IsStillReported()
    {
        // Inner diverges (managed 2 bytes, marshalled 8) but sits LAST, so no following field's offset moves. Comparing offsets alone would miss it; the
        // field's own width is what gives it away.
        var diagnostics = await RunDivergenceAsync(Component(@"
public struct Inner { public bool A; public bool B; }

[Component(""T"", 1)]
public struct Outer { public int C; public Inner I; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("field 'I'"));
    }
}
