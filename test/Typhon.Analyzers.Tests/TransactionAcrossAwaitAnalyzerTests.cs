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
/// <see cref="TransactionAcrossAwaitAnalyzer"/> (TYPHON016, #1653): an <c>await</c> inside a transaction's scope is reported, at the <c>await</c>; outside
/// it, in a nested lambda or local function, or for another type, it is not.
/// </summary>
[TestFixture]
class TransactionAcrossAwaitAnalyzerTests
{
    private const string Stubs = @"
namespace Typhon.Engine
{
    public class EntityAccessor : System.IDisposable { public virtual void Dispose() { } }
    public class Transaction : EntityAccessor { public bool Commit() => true; }
    public class Engine
    {
        public Transaction CreateQuickTransaction() => new Transaction();
    }
}
public class NotATransaction : System.IDisposable { public void Dispose() { } }
";

    private static Task<ImmutableArray<Diagnostic>> Run(string body) => RunSource(@"
using System.Threading.Tasks;
using Typhon.Engine;
public static class Caller
{
    public static async Task M(Engine dbe, System.Collections.Generic.IAsyncEnumerable<int> stream, System.IAsyncDisposable ad, int k)
    {
" + body + @"
    }
}", OutputKind.DynamicallyLinkedLibrary);

    private static async Task<ImmutableArray<Diagnostic>> RunSource(string source, OutputKind kind)
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IAsyncEnumerable<>).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create("T016", [CSharpSyntaxTree.ParseText(Stubs), CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(kind));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, "the test source compiles");
        var diagnostics = await compilation.WithAnalyzers([new TransactionAcrossAwaitAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return [..diagnostics.Where(d => d.Id == TransactionAcrossAwaitAnalyzer.DiagnosticId)];
    }

    [TestCase("using var tx = dbe.CreateQuickTransaction(); await Task.Yield(); tx.Commit();", TestName = "UsingDeclaration")]
    [TestCase("using (var tx = dbe.CreateQuickTransaction()) { await Task.Yield(); tx.Commit(); }", TestName = "UsingStatement")]
    [TestCase("var tx = dbe.CreateQuickTransaction(); await Task.Delay(1); tx.Dispose();", TestName = "PlainLocal_BeforeItsDispose")]
    [TestCase("using var tx = dbe.CreateQuickTransaction(); await foreach (var i in stream) { }", TestName = "AwaitForeach")]
    [TestCase("using var tx = dbe.CreateQuickTransaction(); await using (ad) { }", TestName = "AwaitUsing")]
    [TestCase("using var tx = dbe.CreateQuickTransaction(); if (tx.Commit()) { await Task.Yield(); }", TestName = "NestedBlock")]
    [TestCase("using (dbe.CreateQuickTransaction()) { await Task.Yield(); }", TestName = "UsingStatement_WithoutADeclaration")]
    [TestCase("Transaction tx; tx = dbe.CreateQuickTransaction(); await Task.Yield(); tx.Dispose();", TestName = "PlainLocal_AssignedLater")]
    [TestCase("var tx = dbe.CreateQuickTransaction(); try { await Task.Yield(); } finally { tx.Dispose(); }",
        TestName = "PlainLocal_AwaitBeforeAFinallyDispose")]
    [TestCase("switch (k) { case 1: var tx = dbe.CreateQuickTransaction(); await Task.Yield(); tx.Dispose(); break; }", TestName = "InASwitchSection")]
    public async Task AnAwaitInsideTheScope_IsReported(string body)
    {
        var diagnostics = await Run(body);
        Assert.That(diagnostics, Has.Length.EqualTo(1));
        Assert.That(diagnostics[0].Location.SourceTree.ToString().Substring(diagnostics[0].Location.SourceSpan.Start, 5), Is.EqualTo("await"));
    }

    [TestCase("await Task.Yield(); using var tx = dbe.CreateQuickTransaction(); tx.Commit();", TestName = "AwaitBeforeTheCreation")]
    [TestCase("using (var tx = dbe.CreateQuickTransaction()) { tx.Commit(); } await Task.Yield();", TestName = "AwaitAfterTheUsingStatement")]
    [TestCase("var tx = dbe.CreateQuickTransaction(); tx.Commit(); tx.Dispose(); await Task.Yield();", TestName = "PlainLocal_AwaitAfterItsDispose")]
    [TestCase("using var tx = dbe.CreateQuickTransaction(); System.Func<Task> f = async () => await Task.Yield(); tx.Commit();", TestName = "InALambda")]
    [TestCase("using var tx = dbe.CreateQuickTransaction(); async Task L() => await Task.Yield(); tx.Commit();", TestName = "InALocalFunction")]
    [TestCase("using var other = new NotATransaction(); await Task.Yield();", TestName = "AnotherDisposable")]
    [TestCase("var tx = dbe.CreateQuickTransaction(); tx?.Dispose(); await Task.Yield();", TestName = "PlainLocal_AwaitAfterAConditionalDispose")]
    [TestCase("var tx = dbe.CreateQuickTransaction(); try { tx.Commit(); } finally { tx.Dispose(); } await Task.Yield();",
        TestName = "PlainLocal_AwaitAfterAFinallyDispose")]
    public async Task AnAwaitOutsideTheScope_IsNotReported(string body) => Assert.That(await Run(body), Is.Empty);

    [Test]
    public async Task TopLevelStatements_AreAnalyzed()
    {
        var diagnostics = await RunSource(@"
using System.Threading.Tasks;
using Typhon.Engine;
var dbe = new Engine();
using var tx = dbe.CreateQuickTransaction();
await Task.Yield();
", OutputKind.ConsoleApplication);
        Assert.That(diagnostics, Has.Length.EqualTo(1));
    }
}
