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

        public Accessor Create() => default;

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

    /// <summary>
    /// A getter's copy is reported where it is made — at its own return — and not again at the call: a call site cannot tell a getter from a factory,
    /// and need not, since every by-value copy of live state is a return that TYPHON003 sees. This replaced <c>[AllowCopy]</c> on every factory.
    /// </summary>
    [Test]
    public async Task AGetterCopyingAField_IsReportedAtItsReturn_NotAtItsCall()
    {
        var d = await RunAnalyzerAsync(Wrap(@"
private static Accessor _live;
public static Accessor Getter() => _live;
public static int M() { var a = Getter(); return a.State; }"));
        Assert.Multiple(() =>
        {
            Assert.That(d.Select(x => x.Id), Is.EqualTo(new[] { NoCopyAnalyzer.CopyDiagnosticId }));
            Assert.That(d[0].GetMessage(), Does.Contain("'_live'"), "the copy is the getter's return of the field");
        });
    }

    /// <summary>
    /// A member declared to return a type parameter — <c>Nullable&lt;T&gt;.Value</c>, a generic <c>T Get&lt;T&gt;()</c> — is still reported at the call: its
    /// body returns a <c>T</c>, which no rule can tell is a no-copy type, so the call is the only place its copy can be seen.
    /// </summary>
    [TestCase("var a = n.Value;", TestName = "NullableValue_Fires")]
    [TestCase("var a = Pick<Accessor>(k);", TestName = "GenericReturn_Fires")]
    public async Task AMemberDeclaredToReturnATypeParameter_IsReportedAtTheCall(string statement)
    {
        var d = await RunAnalyzerAsync(Wrap($@"
private static T Pick<T>(int k) => default;
public static int M(System.Nullable<Accessor> n, int k) {{ {statement} return a.State; }}"));
        Assert.That(d.Select(x => x.Id), Is.EqualTo(new[] { NoCopyAnalyzer.CopyDiagnosticId }));
    }

    /// <summary>
    /// <c>GetEnumerator() =&gt; this</c> is a real copy of a live value, safe only because <c>foreach</c> consumes it: what <c>[AllowCopy]</c> is for now.
    /// </summary>
    [TestCase("", ExpectedResult = 1, TestName = "SelfCopy_Unmarked_Fires")]
    [TestCase("[AllowCopy]", ExpectedResult = 0, TestName = "SelfCopy_MarkedAllowCopy_DoesNotFire")]
    public async Task<int> ASelfCopy_NeedsAllowCopy(string marker)
    {
        var d = await RunAnalyzerAsync(@"
using Typhon.Engine.Internals;

[NoCopy]
internal struct Walker
{
    public int Position;
    " + marker + @"
    public Walker GetEnumerator() => this;
}
");
        return d.Count(x => x.Id == NoCopyAnalyzer.CopyDiagnosticId);
    }

    /// <summary>A by-value factory produces a fresh value, through <c>?.</c> and <c>??</c> as directly — no attribute needed.</summary>
    [Test]
    public async Task AFactory_DirectConditionalAndCoalesced_DoesNotFire()
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

    [TestCase("var a = s?.Field ?? default;", TestName = "ConditionalFieldRead_Fires")]
    [TestCase("var a = s?.Create() ?? l;", TestName = "CoalesceOntoALocal_Fires")]
    [TestCase("var a = s.Rent();", TestName = "RefReturningIntoAValueLocal_Fires")]
    [TestCase("var a = k switch { 0 => s.Create(), _ => l };", TestName = "SwitchWithALocalArm_Fires")]
    public async Task CopyShape_Fires(string statement)
    {
        var d = await RunAnalyzerAsync(Wrap($"public static int M(Segment s, ref Accessor l, int k) {{ {statement} return a.State; }}"));
        Assert.That(d.Select(x => x.Id), Is.EqualTo(new[] { NoCopyAnalyzer.CopyDiagnosticId }));
    }

    [TestCase("ref var a = ref s.Rent();", TestName = "RefReturningIntoARefLocal_DoesNotFire")]
    [TestCase("var a = k switch { 0 => s.Create(), _ => default };", TestName = "SwitchOfFactories_DoesNotFire")]
    public async Task FreshOrRefShape_DoesNotFire(string statement)
    {
        var d = await RunAnalyzerAsync(Wrap($"public static int M(Segment s, int k) {{ {statement} return a.State; }}"));
        Assert.That(d, Is.Empty);
    }
}
