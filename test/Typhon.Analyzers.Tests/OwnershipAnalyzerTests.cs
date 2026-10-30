using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;

namespace Typhon.Analyzers.Tests;

/// <summary>
/// The ownership rules together (#1538): TYPHON004 (a must-dispose value not disposed), TYPHON001/003 (a no-copy value copied), TYPHON005–007 (a holder
/// that does not release what it holds) and TYPHON014 (an inferred owner without its marker) — over a root marked like <c>ChunkAccessor</c> and wrappers
/// that own one by value, the shape of the <c>ArchetypeAccessor</c> that leaked.
/// </summary>
[TestFixture]
class OwnershipAnalyzerTests
{
    private const string Attributes = @"
namespace Typhon.Engine.Internals
{
    [System.AttributeUsage(System.AttributeTargets.Struct | System.AttributeTargets.Class)]
    internal sealed class MustDisposeAttribute : System.Attribute { public string Reason { get; set; } }

    [System.AttributeUsage(System.AttributeTargets.Struct)]
    internal sealed class NoCopyAttribute : System.Attribute { public string Reason { get; set; } }

    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Property)]
    internal sealed class AllowCopyAttribute : System.Attribute;
}
";

    /// <summary>The root, as the engine declares <c>ChunkAccessor</c> — in <c>Typhon.Engine.Internals</c>, which TYPHON004 did not match until #1538.</summary>
    private const string Root = @"
namespace Typhon.Engine.Internals
{
    [MustDispose(Reason = ""pins its pages"")]
    [NoCopy(Reason = ""mutable slots"")]
    public struct ChunkAccessor<TStore> : System.IDisposable
    {
        public int Slot;
        public void Dispose() { }
        public int Read() => Slot;
    }

    [MustDispose]
    public sealed class Segment : System.IDisposable
    {
        private ChunkAccessor<int> _rented;

        public void Dispose() => _rented.Dispose();

        public ChunkAccessor<int> CreateChunkAccessor() => default;

        public ref ChunkAccessor<int> Rent() => ref _rented;
    }
}
";

    private static readonly DiagnosticAnalyzer[] All =
        [new DisposableNotDisposedAnalyzer(), new NoCopyAnalyzer(), new CriticalFieldDisposalAnalyzer(), new OwnershipMarkerAnalyzer()];

    private static MetadataReference[] Framework =>
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    ];

    private static CSharpCompilation Compile(string name, string[] sources, params MetadataReference[] references) =>
        CSharpCompilation.Create(name, sources.Select(s => CSharpSyntaxTree.ParseText(s)), Framework.Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    private static async Task<ImmutableArray<Diagnostic>> Analyze(CSharpCompilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, "test source must compile: " + string.Join("; ", errors.Select(e => e.GetMessage())));
        return await compilation.WithAnalyzers([.. All]).GetAnalyzerDiagnosticsAsync();
    }

    private static Task<ImmutableArray<Diagnostic>> AnalyzeOne(string source) => Analyze(Compile("T", [Attributes, Root, source]));

    private static string[] Ids(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Select(d => d.Id).OrderBy(i => i).ToArray();

    // ═══════════════════════════════════════════════════════════════════════
    // The root
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ARootNeverDisposed_IsReported()
    {
        var d = await AnalyzeOne(@"using Typhon.Engine.Internals;
static class C { static int M(Segment s) { var a = s.CreateChunkAccessor(); return a.Read(); } }");
        Assert.That(Ids(d), Is.EqualTo(new[] { "TYPHON004" }), "the namespace of the root was not matched before #1538");
    }

    [Test]
    public async Task ARootUsed_ARefLocal_AndADefaultLaterAssigned_AreNot()
    {
        var d = await AnalyzeOne(@"using Typhon.Engine.Internals;
static class C
{
    static int M(Segment s)
    {
        using var a = s.CreateChunkAccessor();
        ref var rented = ref s.Rent();
        var b = default(ChunkAccessor<int>);
        try { b = s.CreateChunkAccessor(); return a.Read() + rented.Read() + b.Read(); }
        finally { b.Dispose(); }
    }
}");
        Assert.That(Ids(d), Is.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A wrapper owning the root by value — the ArchetypeAccessor shape
    // ═══════════════════════════════════════════════════════════════════════

    private const string Wrapper = @"
namespace Typhon.Engine.Internals
{
    [MustDispose(Reason = ""owns its accessor"")]
    [NoCopy(Reason = ""holds an accessor"")]
    public ref struct Wrapper
    {
        private ChunkAccessor<int> _accessor;
        public bool TryOpen(int id) => _accessor.Read() == id;
        public void Dispose() => _accessor.Dispose();
    }

    public sealed class Tx
    {
        public Wrapper For() => default;
    }
}
";

    private static Task<ImmutableArray<Diagnostic>> AnalyzeWithWrapper(string source) => Analyze(Compile("T", [Attributes, Root, Wrapper, source]));

    [Test]
    public async Task AWrapperNeverDisposed_UsedOnceAndDropped_OrPassedByValue_IsReported()
    {
        var d = await AnalyzeWithWrapper(@"using Typhon.Engine.Internals;
static class C
{
    static bool Undisposed(Tx tx) { var a = tx.For(); return a.TryOpen(1); }
    static bool Dropped(Tx tx) => tx.For().TryOpen(1);
    static bool ByValue(Wrapper a) => a.TryOpen(1);
}");
        Assert.Multiple(() =>
        {
            Assert.That(d.Count(x => x.Id == "TYPHON004" && x.GetMessage().Contains("never disposed")), Is.EqualTo(1), "the local never disposed");
            Assert.That(d.Count(x => x.Id == "TYPHON004" && x.GetMessage().Contains("used once and dropped")), Is.EqualTo(1), "the receiver temporary");
            Assert.That(d.Count(x => x.Id == "TYPHON001"), Is.EqualTo(1), "the by-value parameter");
            Assert.That(d.Length, Is.EqualTo(3), string.Join("; ", d.Select(x => x.GetMessage())));
        });
    }

    [Test]
    public async Task AWrapperUsedCorrectly_IsNot()
    {
        var d = await AnalyzeWithWrapper(@"using Typhon.Engine.Internals;
static class C
{
    static bool Used(Tx tx)
    {
        var a = tx.For();
        try { return Check(ref a); }
        finally { a.Dispose(); }
    }
    static bool Check(ref Wrapper a) => a.TryOpen(1);
    static void Disposed(Tx tx) => tx.For().Dispose();
}");
        Assert.That(Ids(d), Is.Empty, string.Join("; ", d.Select(x => x.GetMessage())));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TYPHON014 — and why the marker, not the inference, is what reaches a caller
    // ═══════════════════════════════════════════════════════════════════════

    private const string UnmarkedWrapper = @"
namespace Typhon.Engine.Internals
{
    public ref struct Unmarked
    {
        private ChunkAccessor<int> _accessor;
        public bool TryOpen(int id) => _accessor.Read() == id;
        public void Dispose() => _accessor.Dispose();
    }

    public sealed class UnmarkedTx
    {
        public Unmarked For() => default;
    }
}
";

    [Test]
    public async Task AnUnmarkedOwner_IsInferredInItsAssembly_AndMustCarryBothMarkers()
    {
        var d = await Analyze(Compile("T", [Attributes, Root, UnmarkedWrapper, @"using Typhon.Engine.Internals;
static class C { static bool M(UnmarkedTx tx) { var a = tx.For(); return a.TryOpen(1); } }"]));
        Assert.Multiple(() =>
        {
            Assert.That(d.Count(x => x.Id == "TYPHON014"), Is.EqualTo(2), "[MustDispose] and [NoCopy] missing on Unmarked");
            Assert.That(d.Count(x => x.Id == "TYPHON004"), Is.EqualTo(1), "inside its assembly the ownership is inferred from the field");
        });
    }

    /// <summary>A class owning an accessor, unmarked: a reference assembly keeps a struct's private fields but drops a class's.</summary>
    private const string UnmarkedClass = @"
namespace Typhon.Engine.Internals
{
    public sealed class Cursor : System.IDisposable
    {
        private ChunkAccessor<int> _accessor;
        public bool TryOpen(int id) => _accessor.Read() == id;
        public void Dispose() => _accessor.Dispose();
    }

    public sealed class CursorTx
    {
        public Cursor Open() => new Cursor();
    }
}
";

    [Test]
    public async Task InAnotherAssembly_AStructsOwnershipIsInferred_AClassesOnlyThroughItsMarker()
    {
        // The library as a referencing project sees it: a reference assembly.
        var library = Compile("Lib", [Attributes, Root, Wrapper, UnmarkedWrapper, UnmarkedClass,
            "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"App\")]"]);
        using var image = new MemoryStream();
        var emitted = library.Emit(image, options: new EmitOptions(metadataOnly: true));
        Assert.That(emitted.Success, Is.True, string.Join("; ", emitted.Diagnostics));
        var reference = MetadataReference.CreateFromImage(image.ToArray());

        var d = await Analyze(Compile("App", [@"using Typhon.Engine.Internals;
static class C
{
    static bool Marked(Tx tx) { var a = tx.For(); return a.TryOpen(1); }
    static bool Unmarked(UnmarkedTx tx) { var a = tx.For(); return a.TryOpen(1); }
    static bool Class(CursorTx tx) { var c = tx.Open(); return c.TryOpen(1); }
}"], reference));
        var messages = d.Select(x => x.GetMessage()).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Some.Contains("'Wrapper'"), "the marked struct");
            Assert.That(messages, Has.Some.Contains("'Unmarked'"), "an unmarked struct: its private field is in the reference assembly");
            Assert.That(messages, Has.None.Contains("'Cursor'"), "an unmarked class: its private field is not — what TYPHON014 prevents");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TYPHON005–007 on holders
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ARefStructHoldingARootWithoutDispose_IsReported()
    {
        var d = await AnalyzeOne(@"using Typhon.Engine.Internals;
ref struct Holder { private ChunkAccessor<int> _a; public int Read() => _a.Read(); }");
        Assert.That(d.Select(x => x.Id), Has.Some.EqualTo("TYPHON005"), "a ref struct is no longer exempt: 'using' takes its pattern Dispose");
    }

    [Test]
    public async Task ADisposeThatDelegates_AndAnIdempotencyGuard_AreNotReported()
    {
        var d = await AnalyzeOne(@"using Typhon.Engine.Internals;
[MustDispose] [NoCopy]
struct Holder : System.IDisposable
{
    private ChunkAccessor<int> _a;
    private bool _disposed;
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Close();
    }
    private void Close() => _a.Dispose();
}");
        Assert.That(Ids(d), Is.Empty, string.Join("; ", d.Select(x => x.GetMessage())));
    }
}
