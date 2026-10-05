using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Typhon.Analyzers.Tests;

/// <summary>
/// Tests for <see cref="ComponentLayoutAnalyzer"/> (TYPHON010, TYPHON011, TYPHON013). The analyzer reimplements sequential struct layout, so the cases below pin the
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

    private static Task<ImmutableArray<Diagnostic>> RunInlinePayloadAnalyzerAsync(string testSource)
        => RunAnalyzerAsync(testSource, ComponentLayoutAnalyzer.InlineSpawnPayloadDiagnosticId);

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

    // =====================================================================
    // TYPHON013 - too large for ComponentValue's inline spawn payload
    // =====================================================================

    /// <summary>
    /// A component that is BOTH marshal-divergent and too large to spawn inline reports both, not just the divergence.
    /// </summary>
    /// <remarks>
    /// TYPHON011's block returns, so whichever of the two ran second was swallowed. The size bound was the one being lost, and it is the
    /// one that matters most: it is a correctness bound — such a component cannot be passed to <c>Spawn</c> at all — where the other two
    /// are advice. Fixing the divergence used to reveal a second diagnostic the analyzer had already computed and discarded.
    /// </remarks>
    [Test]
    public async Task AComponentThatIsBothDivergentAndOversizedReportsBoth()
    {
        // Two bools before an int is the divergence TwoBools_DivergeAndAreReported measures — managed B@1, marshalled B@4 — and the
        // fifteen longs put the struct past 112 bytes. A single bool would NOT do: it pads to 4 either way, so no offset moves.
        var source = Component(@"
[Component(""T"", 1)]
public struct BigAndDivergent
{
    public bool A;
    public bool B;
    public int C;
    public long A0; public long A1; public long A2; public long A3; public long A4;
    public long A5; public long A6; public long A7; public long A8; public long A9;
    public long A10; public long A11; public long A12; public long A13; public long A14;
}
");

        var divergence = await RunDivergenceAsync(source);
        var oversized = await RunInlinePayloadAnalyzerAsync(source);

        Assert.Multiple(() =>
        {
            Assert.That(divergence, Has.Length.EqualTo(1), "the marshal divergence must still be reported");
            Assert.That(
                oversized,
                Has.Length.EqualTo(1),
                "the inline-payload bound was swallowed by the divergence report, which returns");
        });
    }

    /// <summary>
    /// The constant this analyzer compares against is the engine's, and nothing but this test holds the two copies together.
    /// </summary>
    /// <remarks>
    /// <b>The analyzer cannot read the real one.</b> <c>Typhon.Analyzers</c> targets <c>netstandard2.0</c> and must not reference the assembly it analyses, so
    /// <see cref="ComponentLayoutAnalyzer.InlineSpawnPayloadBytes"/> is a copy of <c>ComponentValue.MaxPayloadSize</c>. A copy with nothing checking it is a copy
    /// that rots, and both directions of rot are bad: too high and the analyzer stays quiet about a component that will throw, too low and it nags about
    /// components that fit. This test project references the engine for this one assertion.
    /// </remarks>
    [Test]
    public void TheInlinePayloadConstantMatchesTheEngines()
        => Assert.That(ComponentLayoutAnalyzer.InlineSpawnPayloadBytes, Is.EqualTo(Typhon.Engine.ComponentValue.MaxPayloadSize),
            "the analyzer's copy of the inline spawn payload has drifted from the engine's");

    /// <summary>
    /// A component past the inline payload is reported, with the byte count and both remedies.
    /// </summary>
    /// <remarks>
    /// 128 bytes of <c>long</c>, so the size is exact and owes nothing to padding - this is about the limit, not about layout.
    /// </remarks>
    [Test]
    public async Task PastTheInlinePayload_ProducesDiagnostic()
    {
        var diagnostics = await RunInlinePayloadAnalyzerAsync(Component(@"
[Component(""T"", 1)]
public struct TooBig { public long A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("is 128 bytes, past the 112"));
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("Split it"), "the better remedy comes first");
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("OpenMut"), "and the escape hatch is named, since the component is legal");
            Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning),
                "a component over the limit is legal through the two-step spawn, so this must be suppressible");
        });
    }

    /// <summary>
    /// Exactly at the limit is silent: the comparison is strictly greater than, as the engine's is.
    /// </summary>
    /// <remarks>
    /// The off-by-one that matters. <c>ComponentValue.Create</c> refuses <c>sizeof(T) &gt; 112</c>, so 112 itself must spawn - and an analyzer that disagreed
    /// with the runtime by one byte would be worse than none.
    /// </remarks>
    [Test]
    public async Task ExactlyTheInlinePayload_IsSilent()
    {
        var diagnostics = await RunInlinePayloadAnalyzerAsync(Component(@"
[Component(""T"", 1)]
public struct JustFits { public long A, B, C, D, E, F, G, H, I, J, K, L, M, N; }
"));

        Assert.That(diagnostics, Is.Empty, "112 bytes is what the engine accepts, so the analyzer must accept it too");
    }

    /// <summary>An ordinary small component says nothing, which is almost every component in the repository.</summary>
    [Test]
    public async Task UnderTheInlinePayload_IsSilent()
    {
        var diagnostics = await RunInlinePayloadAnalyzerAsync(Component(@"
[Component(""T"", 1)]
public struct Small { public int Hp; public float Speed; }
"));

        Assert.That(diagnostics, Is.Empty);
    }

    /// <summary>
    /// A fixed buffer counts for its whole width, which is the shape that actually tripped #1140.
    /// </summary>
    /// <remarks>
    /// The component that found the bug was a 64-byte <c>String64</c> key plus a 32-byte digest, a 16-byte salt and some scalars. A size walk that measured a
    /// <c>fixed</c> buffer as one element would have called that 69 bytes and stayed silent, so this pins the walk and not just the comparison.
    /// </remarks>
    [Test]
    public async Task AFixedBufferCountsForItsWholeWidth()
    {
        var diagnostics = await RunInlinePayloadAnalyzerAsync(Component(@"
public unsafe struct Key64 { public fixed byte Data[64]; }

[Component(""T"", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Credential { public Key64 Name; public long D0, D1, D2, D3; public long S0, S1; public long Created, LastSeen; public int Iterations; }
"));

        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("is 132 bytes, past the 112"),
            "the fixed buffer must count for 64, or this component measures as 69 and the diagnostic never fires");
    }

    /// <summary>
    /// A component that is both oversized and padded gets BOTH diagnostics, because the two remedies are independent.
    /// </summary>
    /// <remarks>
    /// Splitting does not remove padding from either half, and <c>Pack</c> almost never brings a struct from over 112 to under it. Reporting one and swallowing
    /// the other would mean the author fixes the first, rebuilds, and meets the second - which is the behaviour TYPHON011 deliberately has (it stands down while
    /// a layout divergence is outstanding, because the padding advice would then be advice about the wrong layout) and which would be wrong here.
    /// </remarks>
    [Test]
    public async Task OversizedAndPadded_ProducesBoth()
    {
        // 15 longs and an int: 124 bytes of field, rounded to 128 for the longs' alignment. Over the limit, and 4 bytes of avoidable padding.
        const string source = @"
[Component(""T"", 1)]
public struct BigAndPadded { public long A, B, C, D, E, F, G, H, I, J, K, L, M, N, O; public int P; }
";

        var oversized = await RunInlinePayloadAnalyzerAsync(Component(source));
        var padding = await RunAnalyzerAsync(Component(source));

        Assert.Multiple(() =>
        {
            Assert.That(oversized, Has.Length.EqualTo(1), "the size is still past the inline payload");
            Assert.That(padding, Has.Length.EqualTo(1), "and the padding is still avoidable");
        });
    }

    /// <summary>A struct with no <c>[Component]</c> is not this analyzer's business, however large.</summary>
    [Test]
    public async Task ALargePlainStruct_IsSilent()
    {
        var diagnostics = await RunInlinePayloadAnalyzerAsync(Component(@"
public struct NotAComponent { public long A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P; }
"));

        Assert.That(diagnostics, Is.Empty, "only a [Component] travels through ComponentValue");
    }
}
