using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Typhon.Generators;

namespace Typhon.Generators.Tests;

/// <summary>
/// Tests for <see cref="ArchetypeAccessorGenerator"/>. Focus: #505 — a <c>[Archetype]</c> declared in the global (unnamed)
/// namespace (idiomatic for a top-level-statement console app) must generate parseable code, not <c>namespace &lt;global namespace&gt;</c>.
/// </summary>
[TestFixture]
class ArchetypeAccessorGeneratorTests
{
    // Minimal stand-ins the generator binds against: it matches [Archetype] by the fully-qualified metadata name
    // "Typhon.Schema.Definition.ArchetypeAttribute" and collects static readonly Comp<T> fields (matched by simple name "Comp").
    private const string Stubs = @"
namespace Typhon.Schema.Definition
{
    public sealed class ArchetypeAttribute : System.Attribute { public ArchetypeAttribute(int id) { } }
    public sealed class ComponentAttribute : System.Attribute { public ComponentAttribute(string name, int version) { } }
}

namespace Typhon.Engine
{
    public struct Comp<T> { }
    public abstract class Archetype<TSelf> { protected static Comp<T> Register<T>() => default; }
}
";

    private static (string[] GeneratedSources, ImmutableArray<Diagnostic> GeneratedParseErrors) Run(string testSource)
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "GeneratorTestAssembly",
            new[]
            {
                CSharpSyntaxTree.ParseText(Stubs),
                CSharpSyntaxTree.ParseText(testSource),
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new ArchetypeAccessorGenerator().AsSourceGenerator());
        var runResult = driver.RunGenerators(compilation).GetRunResult();

        // The bug emitted `namespace <global namespace>` — a PARSE error. GeneratedTrees carry syntactic diagnostics only
        // (references to engine types like Transaction/EntityId are semantic and irrelevant to parseability), so this is
        // the faithful signal: a well-formed generated file has zero error-severity syntax diagnostics.
        var parseErrors = runResult.GeneratedTrees
            .SelectMany(t => t.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        var sources = runResult.GeneratedTrees.Select(t => t.ToString()).ToArray();
        return (sources, parseErrors);
    }

    [Test]
    public void GlobalNamespace_Archetype_GeneratesParseableCode()
    {
        // #505 repro: [Archetype] in the global namespace. Pre-fix this emitted `namespace <global namespace>` → unparseable.
        const string source = @"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Component(""Repro.Data"", 1)] public struct Data { public int Value; public int Pad; }
[Archetype] public sealed partial class E : Archetype<E> { public static readonly Comp<Data> D = Register<Data>(); }
";
        var (sources, parseErrors) = Run(source);

        Assert.That(sources, Is.Not.Empty, "Generator must emit a source for the global-namespace archetype.");
        Assert.That(parseErrors, Is.Empty,
            "Generated code for a global-namespace archetype must be syntactically valid (no `namespace <global namespace>`). "
            + "Errors: " + string.Join("; ", parseErrors.Select(d => d.ToString())));
        Assert.That(sources[0], Does.Not.Contain("<global namespace>"),
            "The literal `<global namespace>` must never appear in generated code.");
        Assert.That(sources[0], Does.Not.Contain("namespace "),
            "A global-namespace archetype must emit top-level code with no namespace wrapper.");
        Assert.That(sources[0], Does.Contain("partial class E"), "The archetype accessor partial must be emitted.");
    }

    [Test]
    public void NamedNamespace_Archetype_WrapsInNamespace()
    {
        // Regression the other direction: a named namespace must still emit `namespace Foo`.
        const string source = @"
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Foo
{
    [Component(""Repro.Data"", 1)] public struct Data { public int Value; public int Pad; }
    [Archetype] public sealed partial class E : Archetype<E> { public static readonly Comp<Data> D = Register<Data>(); }
}
";
        var (sources, parseErrors) = Run(source);

        Assert.That(parseErrors, Is.Empty,
            "Generated code for a named-namespace archetype must be syntactically valid. "
            + "Errors: " + string.Join("; ", parseErrors.Select(d => d.ToString())));
        Assert.That(sources[0], Does.Contain("namespace Foo"), "A named-namespace archetype must wrap generated code in `namespace Foo`.");
        Assert.That(sources[0], Does.Contain("partial class E"), "The archetype accessor partial must be emitted.");
    }
}
