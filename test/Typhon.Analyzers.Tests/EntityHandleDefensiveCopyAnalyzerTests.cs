using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Typhon.Analyzers.Tests;

/// <summary>
/// Tests for <see cref="EntityHandleDefensiveCopyAnalyzer"/> (TYPHON012): a non-readonly member of an entity handle called on a read-only receiver.
/// </summary>
[TestFixture]
class EntityHandleDefensiveCopyAnalyzerTests
{
    // Stubs shaped like the real handles: readonly readers, non-readonly mutators. The analyzer binds on namespace + type name.
    private const string Stubs = @"
namespace Typhon.Engine
{
    public ref struct EntityRef
    {
        public readonly bool IsValid => true;
        public readonly int Read() => 0;
        public int NotYetReadonly() => 0;
    }

    public ref struct EntityRefMut
    {
        private int _state;
        public readonly bool IsValid => true;
        public readonly int Read() => 0;
        public ref int Write() => throw null;
        public void Enable() { _state = 1; }
        public int Mode { readonly get => _state; set => _state = value; }
    }

    public ref struct HandleEnumerator
    {
        public EntityRefMut Current => default;
        public bool MoveNext() => false;
    }

    public ref struct HandleQuery
    {
        public HandleEnumerator GetEnumerator() => default;
    }

    public ref struct RefHandleEnumerator
    {
        public ref EntityRefMut Current => throw null;
        public bool MoveNext() => false;
    }

    public ref struct RefHandleQuery
    {
        public RefHandleEnumerator GetEnumerator() => default;
    }

    public ref struct HandleBox
    {
        public EntityRefMut H;
        public ref readonly EntityRefMut ReadOnlyH => throw null;
    }
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
            [CSharpSyntaxTree.ParseText(Stubs), CSharpSyntaxTree.ParseText(testSource)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(compileErrors, Is.Empty, "test source must compile: " + string.Join("; ", compileErrors.Select(e => e.GetMessage())));

        var withAnalyzer = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityHandleDefensiveCopyAnalyzer()));
        var diagnostics = await withAnalyzer.GetAnalyzerDiagnosticsAsync();
        return diagnostics.Where(d => d.Id == EntityHandleDefensiveCopyAnalyzer.DiagnosticId).ToImmutableArray();
    }

    private static string Wrap(string body) => @"
using Typhon.Engine;

public static class Caller
{
" + body + @"
}
";

    [Test]
    public async Task InParameter_Write_Fires()
    {
        var d = await RunAnalyzerAsync(Wrap("public static void M(in EntityRefMut e) { e.Write() = 1; }"));
        Assert.That(d, Has.Length.EqualTo(1));
        Assert.That(d[0].GetMessage(), Does.Contain("'Write'").And.Contain("'e'"));
    }

    [Test]
    public async Task InParameter_ReadonlyMembers_DoNotFire()
    {
        var d = await RunAnalyzerAsync(Wrap("public static int M(in EntityRefMut e) => e.Read() + (e.IsValid ? 1 : 0) + e.Mode;"));
        Assert.That(d, Is.Empty);
    }

    [Test]
    public async Task InParameter_NonReadonlyReaderOnEntityRef_Fires()
    {
        var d = await RunAnalyzerAsync(Wrap("public static int M(in EntityRef e) => e.NotYetReadonly();"));
        Assert.That(d, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task ForeachVariable_Enable_Fires()
    {
        var d = await RunAnalyzerAsync(Wrap("public static void M(HandleQuery q) { foreach (var e in q) { e.Enable(); } }"));
        Assert.That(d, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task RefReadonlyLocal_Enable_Fires()
    {
        var d = await RunAnalyzerAsync(Wrap(@"public static void M(ref EntityRefMut src)
    {
        ref readonly var e = ref src;
        e.Enable();
    }"));
        Assert.That(d, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task ReadonlyField_Fires()
    {
        var d = await RunAnalyzerAsync(@"
using Typhon.Engine;

public ref struct Holder
{
    private readonly EntityRefMut _h;
    public void M() { _h.Enable(); }
}
");
        Assert.That(d, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task FieldOfInParameter_Fires()
    {
        var d = await RunAnalyzerAsync(Wrap("public static void M(in HandleBox b) { b.H.Enable(); }"));
        Assert.That(d, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task RefReadonlyReturns_Fire()
    {
        var d = await RunAnalyzerAsync(Wrap(@"private static ref readonly EntityRefMut Pick(in EntityRefMut e) => ref e;
    public static void M(ref EntityRefMut src, HandleBox b)
    {
        Pick(in src).Enable();
        b.ReadOnlyH.Enable();
    }"));
        Assert.That(d, Has.Length.EqualTo(2), "a ref readonly method return and a ref readonly property");
    }

    [Test]
    public async Task ForeachRefVariable_DoesNotFire()
    {
        var d = await RunAnalyzerAsync(Wrap("public static void M(RefHandleQuery q) { foreach (ref var e in q) { e.Enable(); } }"));
        Assert.That(d, Is.Empty, "a `foreach (ref var …)` variable is writable");
    }

    [Test]
    public async Task ThisInReadonlyMember_Fires()
    {
        var d = await RunAnalyzerAsync(@"
using Typhon.Engine;

public ref struct Holder
{
    private EntityRefMut _h;
    public readonly void M() { _h.Enable(); }
    public void Ok() { _h.Enable(); }
}
");
        Assert.That(d, Has.Length.EqualTo(1), "only the call inside the readonly member");
    }

    [Test]
    public async Task MutableLocal_And_RefParameter_DoNotFire()
    {
        var d = await RunAnalyzerAsync(Wrap(@"public static void M(ref EntityRefMut r)
    {
        r.Write() = 1;
        r.Mode = 2;
        EntityRefMut local = default;
        local.Enable();
        local.Write() = 3;
    }"));
        Assert.That(d, Is.Empty);
    }
}
