using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace Typhon.Analyzers.Tests;

/// <summary>
/// TYPHON017 (<see cref="IndexedFieldInOpaqueWhereAnalyzer"/>) and its code fix, compiled against the real <c>Typhon.Engine</c> so the analyzer binds the
/// same <c>EcsQuery.Where</c> a user's build does (#1586 §4).
/// </summary>
[TestFixture]
class IndexedFieldInOpaqueWhereAnalyzerTests
{
    private const string Prelude = @"
using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Engine;
using Typhon.Schema.Definition;

public struct Inner { public int Value; }

public struct C
{
    [Index(AllowMultiple = true)] public int Level;
    [Index(AllowMultiple = true)] public int Other;
    [Index(AllowMultiple = true)] public float Speed;
    [Index] public bool Flag;
    public int Payload;
    public Inner Inner;
}

public struct D
{
    [Index(AllowMultiple = true)] public int Code;
}

public class A : Archetype<A>
{
    public static readonly Comp<C> X = Register<C>();
    public static readonly Comp<D> Y = Register<D>();
}

public class Opt { public int Min; }

public static class Limits
{
    public static int Max = 9;
    public static Opt Optional;
    public static int? Nullable;
    public static dynamic Dyn = 3;
    public static int Pick(int a, int b = 2) => a + b;
}
";

    private static readonly Lazy<MetadataReference[]> References = new(() =>
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator);
        var refs = tpa.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(Typhon.Engine.DatabaseEngine).Assembly.Location));
        // The schema attributes' assembly, reached through the engine's references (the test project does not name it directly).
        var schema = typeof(Typhon.Engine.DatabaseEngine).Assembly.GetReferencedAssemblies().Single(a => a.Name!.StartsWith("Typhon.Schema"));
        refs.Add(MetadataReference.CreateFromFile(Assembly.Load(schema).Location));
        return refs.ToArray();
    });

    private static string Wrap(string predicate, string extraParams = "") => Prelude + $@"
public static class Caller
{{
    public static void M(Transaction tx{extraParams})
    {{
        var r = tx.Query<A>().Where<C>({predicate}).Execute();
    }}
}}";

    private static CSharpCompilation Compile(string source) =>
        CSharpCompilation.Create("T017", [CSharpSyntaxTree.ParseText(source)], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var compilation = Compile(source);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, "the test source must compile: " + string.Join("; ", errors.Select(e => e.ToString())));
        var diags = await compilation.WithAnalyzers([new IndexedFieldInOpaqueWhereAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return [.. diags.Where(d => d.Id == IndexedFieldInOpaqueWhereAnalyzer.DiagnosticId)];
    }

    private static bool Fixable(Diagnostic d) => d.Properties.ContainsKey(IndexedFieldInOpaqueWhereAnalyzer.FixableProperty);

    [TestCase("c => c.Level > 3", true)]
    [TestCase("c => 3 < c.Level && c.Level <= 9", true)]
    [TestCase("c => c.Level == Limits.Max", true)]
    [TestCase("c => c.Speed >= 1.5", true)]
    [TestCase("c => c.Flag == true", true)]
    [TestCase("(C c) => c.Level != 0", true)]
    [TestCase("c => c.Level > 3 && c.Payload < 5", false)]
    [TestCase("c => c.Level > 3 || c.Level < 1", false)]
    [TestCase("c => c.Level > c.Other", false)]
    [TestCase("c => (byte)c.Level == 3", false)]
    [TestCase("c => c.Level.CompareTo(3) > 0", false)]
    [TestCase("c => { return c.Level > 3; }", false)]
    [TestCase("c => c.Flag", false)]
    [TestCase("c => c.Level > (Limits.Optional?.Min ?? 0)", false)]
    [TestCase("c => c.Level > Limits.Pick(1)", false)]
    [TestCase("c => c.Level > -Limits.Max * 2", true)]
    [TestCase("c => c.Level > Limits.Nullable", false)]
    [TestCase("c => c.Level > (int)Limits.Dyn", false)]
    [TestCase("c => c.Speed == (float)Limits.Max", true)]
    public async Task IndexedField_Warns_FixOnlyWhenWhereFieldAcceptsTheShape(string predicate, bool fixable)
    {
        var diags = await AnalyzeAsync(Wrap(predicate));
        Assert.That(diags, Has.Length.EqualTo(1), predicate);
        Assert.That(diags[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(Fixable(diags[0]), Is.EqualTo(fixable), predicate);
    }

    [Test]
    public async Task CapturedVariable_Warns_AndIsFixable()
    {
        var diags = await AnalyzeAsync(Wrap("c => c.Level > threshold", ", int threshold"));
        Assert.That(diags, Has.Length.EqualTo(1));
        Assert.That(Fixable(diags[0]), Is.True);
    }

    [TestCase("c => c.Payload > 3")]
    [TestCase("c => c.Inner.Value > 3")]
    [TestCase("c => c.Payload > 3 && c.Inner.Value < 2")]
    public async Task NonIndexedPredicate_NoDiagnostic(string predicate) =>
        Assert.That(await AnalyzeAsync(Wrap(predicate)), Is.Empty, predicate);

    /// <summary>
    /// A <c>WhereField</c> on another component in the same chain: renaming would add a second component to <c>WhereField</c>, which throws, and
    /// <c>Where</c> is the engine's own advice for that case — so no warning either way round.
    /// </summary>
    [TestCase("tx.Query<A>().WhereField<D>(d => d.Code == 1).Where<C>(c => c.Level > 3).Execute()")]
    [TestCase("tx.Query<A>().Where<C>(c => c.Level > 3).WhereField<D>(d => d.Code == 1).Execute()")]
    public async Task WhereFieldOnAnotherComponentInTheChain_NoDiagnostic(string chain)
    {
        var source = Prelude + $@"
public static class Caller
{{
    public static void M(Transaction tx) => {chain};
}}";
        Assert.That(await AnalyzeAsync(source), Is.Empty);
    }

    [Test]
    public async Task WhereFieldOnTheSameComponentInTheChain_StillWarns()
    {
        var source = Prelude + @"
public static class Caller
{
    public static void M(Transaction tx) => tx.Query<A>().WhereField<C>(c => c.Other == 1).Where<C>(c => c.Level > 3).Execute();
}";
        var diags = await AnalyzeAsync(source);
        Assert.That(diags, Has.Length.EqualTo(1));
        Assert.That(Fixable(diags[0]), Is.True);
    }

    [Test]
    public async Task DelegateVariable_NoDiagnostic()
    {
        var source = Prelude + @"
public static class Caller
{
    public static void M(Transaction tx, Func<C, bool> pred) => tx.Query<A>().Where<C>(pred).Execute();
}";
        Assert.That(await AnalyzeAsync(source), Is.Empty);
    }

    [Test]
    public async Task LinqWhere_NoDiagnostic()
    {
        var source = Prelude + @"
public static class Caller
{
    public static int M(List<C> list) => list.Where(c => c.Level > 3).Count();
}";
        Assert.That(await AnalyzeAsync(source), Is.Empty);
    }

    /// <summary>
    /// The fix renames the call and nothing else; the result must compile, and the lambda — now an expression tree — must be accepted by the engine's own
    /// <c>ExpressionParser</c> as a single-branch predicate, which is what every one-shot terminal runs.
    /// </summary>
    [TestCase("c => c.Level > 3", "")]
    [TestCase("c => 3 < c.Level && c.Level <= 9", "")]
    [TestCase("c => c.Level == Limits.Max", "")]
    [TestCase("c => c.Speed >= 1.5", "")]
    [TestCase("c => c.Flag == true", "")]
    [TestCase("(C c) => c.Level != 0", "")]
    [TestCase("c => c.Level > threshold && c.Other < threshold + 4", ", int threshold")]
    [TestCase("c => c.Level > -Limits.Max * 2", "")]
    [TestCase("c => c.Speed == (float)Limits.Max", "")]
    public async Task Fix_RewritesToWhereField_AndTheParserAcceptsIt(string predicate, string extraParams)
    {
        var source = Wrap(predicate, extraParams);
        var fixedSource = await ApplyFixAsync(source);
        Assert.That(fixedSource, Does.Contain($".WhereField<C>({predicate})"));

        var compilation = Compile(fixedSource);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join("; ", errors.Select(e => e.ToString())));
        Assert.That(await AnalyzeAsync(fixedSource), Is.Empty, "the fixed call no longer warns");

        var branches = ParseWithEngine(predicate, extraParams);
        Assert.That(branches, Is.EqualTo(1), "WhereField's one-shot terminals take a single DNF branch");
    }

    private static async Task<string> ApplyFixAsync(string source)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.CurrentSolution.AddProject("T017", "T017", LanguageNames.CSharp)
            .WithMetadataReferences(References.Value)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var document = project.AddDocument("Test.cs", SourceText.From(source));
        var compilation = await document.Project.GetCompilationAsync();
        var diags = await compilation!.WithAnalyzers([new IndexedFieldInOpaqueWhereAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        var diagnostic = diags.Single(d => d.Id == IndexedFieldInOpaqueWhereAnalyzer.DiagnosticId);

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None);
        await new IndexedFieldInOpaqueWhereCodeFixProvider().RegisterCodeFixesAsync(context);
        Assert.That(actions, Has.Count.EqualTo(1));

        var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
        var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetDocument(document.Id);
        return (await changed!.GetTextAsync()).ToString();
    }

    [Test]
    public async Task NotFixableShape_OffersNoFix()
    {
        using var workspace = new AdhocWorkspace();
        var source = Wrap("c => c.Level > 3 || c.Level < 1");
        var project = workspace.CurrentSolution.AddProject("T017", "T017", LanguageNames.CSharp).WithMetadataReferences(References.Value);
        var document = project.AddDocument("Test.cs", SourceText.From(source));
        var compilation = await document.Project.GetCompilationAsync();
        var diagnostic = (await compilation!.WithAnalyzers([new IndexedFieldInOpaqueWhereAnalyzer()]).GetAnalyzerDiagnosticsAsync()).Single();

        var actions = new List<CodeAction>();
        await new IndexedFieldInOpaqueWhereCodeFixProvider().RegisterCodeFixesAsync(
            new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None));
        Assert.That(actions, Is.Empty);
    }

    /// <summary>
    /// Compiles <c>(Expression&lt;Func&lt;C, bool&gt;&gt;)(predicate)</c> into an in-memory assembly, then hands it to the engine's internal
    /// <c>ExpressionParser.ParseDnf</c> by reflection; returns the branch count, or fails the test on a parser refusal.
    /// </summary>
    private static int ParseWithEngine(string predicate, string extraParams)
    {
        var source = Prelude + $@"
public static class Harness
{{
    public static object E(int unused{extraParams}) => (System.Linq.Expressions.Expression<Func<C, bool>>)({predicate});
}}";
        using var stream = new MemoryStream();
        var emit = Compile(source).Emit(stream);
        Assert.That(emit.Success, Is.True, string.Join("; ", emit.Diagnostics));

        var assembly = Assembly.Load(stream.ToArray());
        var harness = assembly.GetType("Harness")!.GetMethod("E")!;
        var args = harness.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        var expression = harness.Invoke(null, args);

        var parser = typeof(Typhon.Engine.DatabaseEngine).Assembly.GetType("Typhon.Engine.Internals.ExpressionParser", throwOnError: true)!;
        var parse = parser.GetMethod("ParseDnf", BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(assembly.GetType("C")!);
        try
        {
            return ((Array)parse.Invoke(null, [expression])!).Length;
        }
        catch (TargetInvocationException e)
        {
            Assert.Fail($"ExpressionParser refused '{predicate}': {e.InnerException?.Message}");
            return -1;
        }
    }
}
