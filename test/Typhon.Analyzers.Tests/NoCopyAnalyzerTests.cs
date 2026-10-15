using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Typhon.Analyzers.Tests;

/// <summary>
/// Tests for <see cref="NoCopyAnalyzer"/> (TYPHON001 by-value parameter, TYPHON003 copy) over <c>[NoCopy]</c> types.
/// </summary>
/// <remarks>
/// The stubs declare the attributes where the engine does, <c>Typhon.Engine.Internals</c>. The analyzer matched <c>Typhon.Engine</c> instead and so never
/// fired (#1226): every case below that expects a diagnostic fails if the attribute is not recognised.
/// </remarks>
[TestFixture]
class NoCopyAnalyzerTests
{
    private const string Stubs = @"
namespace Typhon.Engine.Internals
{
    [System.AttributeUsage(System.AttributeTargets.Struct)]
    internal sealed class NoCopyAttribute : System.Attribute
    {
        public string Reason { get; set; }
    }

    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Property)]
    internal sealed class AllowCopyAttribute : System.Attribute;

    [NoCopy(Reason = ""test"")]
    internal struct Accessor
    {
        public int State;
    }

    internal sealed class Segment
    {
        private Accessor _cached;

        [AllowCopy]
        public Accessor Create() => default;

        public Accessor Getter() => default;

        [AllowCopy]
        public ref Accessor Rent() => ref _cached;

        public Accessor Field;
    }
}
";

    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(string testSource)
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            [CSharpSyntaxTree.ParseText(Stubs), CSharpSyntaxTree.ParseText(testSource)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(compileErrors, Is.Empty, "test source must compile: " + string.Join("; ", compileErrors.Select(e => e.GetMessage())));

        var withAnalyzer = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new NoCopyAnalyzer()));
        return await withAnalyzer.GetAnalyzerDiagnosticsAsync();
    }

    private static string Wrap(string body) => @"
using Typhon.Engine.Internals;

internal static class Caller
{
" + body + @"
}
";

    [Test]
    public async Task ByValueParameter_Fires()
    {
        var d = await RunAnalyzerAsync(Wrap("public static int M(Accessor a) => a.State;"));
        Assert.That(d.Select(x => x.Id), Is.EqualTo(new[] { NoCopyAnalyzer.RefDiagnosticId }));
    }

    [Test]
    public async Task RefParameter_DoesNotFire()
    {
        var d = await RunAnalyzerAsync(Wrap("public static int M(ref Accessor a) => a.State;"));
        Assert.That(d, Is.Empty);
    }

    [Test]
    public async Task CopyOfALocal_Fires()
    {
        var d = await RunAnalyzerAsync(Wrap("public static int M(ref Accessor a) { var copy = a; return copy.State; }"));
        Assert.That(d.Select(x => x.Id), Is.EqualTo(new[] { NoCopyAnalyzer.CopyDiagnosticId }));
    }

    [Test]
    public async Task GetterWithoutAllowCopy_Fires()
    {
        var d = await RunAnalyzerAsync(Wrap("public static int M(Segment s) { var a = s.Getter(); return a.State; }"));
        Assert.That(d.Select(x => x.Id), Is.EqualTo(new[] { NoCopyAnalyzer.CopyDiagnosticId }));
    }

    /// <summary>A factory marked <c>[AllowCopy]</c> produces a fresh value, through <c>?.</c> and <c>??</c> as directly.</summary>
    [Test]
    public async Task AllowCopyFactory_DirectConditionalAndCoalesced_DoesNotFire()
    {
        var d = await RunAnalyzerAsync(Wrap(@"
public static int M(Segment s)
{
    var a = s.Create();
    var b = s?.Create() ?? default;
    var c = s != null ? s.Create() : default;
    return a.State + b.State + c.State;
}"));
        Assert.That(d, Is.Empty);
    }

    [TestCase("var a = s?.Getter() ?? default;", TestName = "CoalesceOverAGetter_Fires")]
    [TestCase("var a = s?.Field ?? default;", TestName = "ConditionalFieldRead_Fires")]
    [TestCase("var a = s?.Create() ?? l;", TestName = "CoalesceOntoALocal_Fires")]
    [TestCase("var a = s.Rent();", TestName = "RefReturningAllowCopyIntoAValueLocal_Fires")]
    [TestCase("var a = k switch { 0 => s.Create(), _ => l };", TestName = "SwitchWithALocalArm_Fires")]
    public async Task CopyShape_Fires(string statement)
    {
        var d = await RunAnalyzerAsync(Wrap($"public static int M(Segment s, ref Accessor l, int k) {{ {statement} return a.State; }}"));
        Assert.That(d.Select(x => x.Id), Is.EqualTo(new[] { NoCopyAnalyzer.CopyDiagnosticId }));
    }

    [TestCase("ref var a = ref s.Rent();", TestName = "RefReturningAllowCopyIntoARefLocal_DoesNotFire")]
    [TestCase("var a = k switch { 0 => s.Create(), _ => default };", TestName = "SwitchOfFactories_DoesNotFire")]
    public async Task FreshOrRefShape_DoesNotFire(string statement)
    {
        var d = await RunAnalyzerAsync(Wrap($"public static int M(Segment s, int k) {{ {statement} return a.State; }}"));
        Assert.That(d, Is.Empty);
    }
}
