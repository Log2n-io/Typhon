using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Typhon.Generators;

namespace Typhon.Generators.Tests;

/// <summary>
/// Tests for the component-schema pipeline of <see cref="ArchetypeAccessorGenerator"/> (feature #514, phase 4): each partial <c>[Component]</c>
/// struct gets a reflection-free <c>IComponentSchemaProvider</c> implementation; non-partial structs are skipped (the engine falls back to reflection).
/// </summary>
[TestFixture]
class ComponentSchemaGeneratorTests
{
    // Faithful stand-ins mirroring the real Typhon.Schema.Definition signatures so the generated ctor calls are shape-checked.
    private const string Stubs = @"
namespace Typhon.Schema.Definition
{
    public sealed class ComponentAttribute : System.Attribute
    {
        public ComponentAttribute(string name, int revision) { }
        public StorageMode StorageMode { get; set; }
        public DurabilityDiscipline DefaultDiscipline { get; set; }
    }
    public sealed class FieldAttribute : System.Attribute { public int? FieldId { get; set; } public string Name { get; set; } public string PreviousName { get; set; } }
    public sealed class IndexAttribute : System.Attribute { public bool AllowMultiple { get; set; } public CascadeAction OnParentDelete { get; set; } }
    public sealed class ForeignKeyAttribute : System.Attribute { public ForeignKeyAttribute(System.Type t) { } }
    public sealed class SpatialIndexAttribute : System.Attribute { public SpatialIndexAttribute(float margin, float cellSize = 0f) { } public SpatialMode Mode { get; set; } public uint Category { get; set; } }
    public enum StorageMode { Versioned = 0, SingleVersion = 1, Transient = 2 }
    public enum DurabilityDiscipline { TickFence = 0, Commit = 1 }
    public enum SpatialMode : byte { Dynamic = 0, Static = 1 }
    public enum CascadeAction { None = 0, Delete = 1 }
    public interface IComponentSchemaProvider { ComponentSchemaSpec GetComponentSchema(); }
    public readonly struct ComponentSchemaSpec
    {
        public ComponentSchemaSpec(string name, int revision, ComponentFieldSpec[] fields,
            StorageMode storageMode = StorageMode.Versioned, DurabilityDiscipline defaultDiscipline = DurabilityDiscipline.TickFence) { }
    }
    public readonly struct ComponentFieldSpec
    {
        public ComponentFieldSpec(string name, System.Type dotNetType, int offset, string previousName = null, int? explicitFieldId = null,
            bool isStatic = false, int arrayLength = 0, bool hasIndex = false, bool indexAllowMultiple = false, bool isForeignKey = false,
            System.Type foreignKeyTargetType = null, bool hasSpatialIndex = false, float spatialMargin = 0f, float spatialCellSize = 0f,
            SpatialMode spatialMode = SpatialMode.Dynamic, uint spatialCategory = 4294967295u) { }
    }
    public struct AABB2F { public float MinX, MinY, MaxX, MaxY; }
    public struct ComponentCollection<T> where T : unmanaged { int _bufferId; }
}
";

    private static (string[] GeneratedSources, ImmutableArray<Diagnostic> ParseErrors) Run(string testSource)
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "ComponentGeneratorTestAssembly",
            new[]
            {
                CSharpSyntaxTree.ParseText(Stubs),
                CSharpSyntaxTree.ParseText(testSource),
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new ArchetypeAccessorGenerator().AsSourceGenerator());
        var runResult = driver.RunGenerators(compilation).GetRunResult();

        var parseErrors = runResult.GeneratedTrees
            .SelectMany(t => t.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        var sources = runResult.GeneratedTrees.Select(t => t.ToString()).ToArray();
        return (sources, parseErrors);
    }

    private static string SchemaSource(string[] sources, string structName = null) =>
        (structName == null
            ? sources.FirstOrDefault(s => s.Contains("IComponentSchemaProvider"))
            : sources.FirstOrDefault(s => s.Contains($"struct {structName} :"))) ?? "";

    [Test]
    public void PartialComponent_EmitsSchemaProvider_AllAttributeKinds()
    {
        const string source = @"
using Typhon.Schema.Definition;
using System.Runtime.InteropServices;

namespace Game
{
    [Component(""Game.Fk"", 1)]
    [StructLayout(LayoutKind.Sequential)]
    public partial struct Fk { public long Id; }

    [Component(""Game.Rep"", 3, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = DurabilityDiscipline.Commit)]
    [StructLayout(LayoutKind.Sequential)]
    public partial struct Rep
    {
        public float X;

        [Field(PreviousName = ""Hitpoints"")]
        [Index(AllowMultiple = true)]
        public int Health;

        [ForeignKey(typeof(Fk))]
        [Index]
        public long ParentLink;

        [SpatialIndex(2.0f, cellSize: 4.0f, Mode = SpatialMode.Static, Category = 7)]
        public AABB2F Bounds;
    }
}
";
        var (sources, parseErrors) = Run(source);
        var schema = SchemaSource(sources, "Rep");

        Assert.That(parseErrors, Is.Empty, "Generated schema code must parse. Errors: " + string.Join("; ", parseErrors.Select(d => d.ToString())));
        Assert.That(schema, Is.Not.Empty, "A schema provider must be generated for the partial [Component] struct.");

        Assert.Multiple(() =>
        {
            Assert.That(schema, Does.Contain("partial struct Rep : global::Typhon.Schema.Definition.IComponentSchemaProvider"));
            Assert.That(schema, Does.Contain("IComponentSchemaProvider.GetComponentSchema()"), "explicit interface implementation");
            Assert.That(schema, Does.Contain("namespace Game"));
            // Offsets are the one residual runtime call.
            Assert.That(schema, Does.Contain("global::System.Runtime.InteropServices.Marshal.OffsetOf<global::Game.Rep>(\"X\")"));
            // Field metadata carried faithfully.
            Assert.That(schema, Does.Contain("previousName: \"Hitpoints\""));
            Assert.That(schema, Does.Contain("hasIndex: true, indexAllowMultiple: true"));
            Assert.That(schema, Does.Contain("isForeignKey: true, foreignKeyTargetType: typeof(global::Game.Fk)"));
            Assert.That(schema, Does.Contain("hasSpatialIndex: true"));
            Assert.That(schema, Does.Contain("spatialMargin: 2f"));
            Assert.That(schema, Does.Contain("spatialCellSize: 4f"));
            Assert.That(schema, Does.Contain("spatialMode: (global::Typhon.Schema.Definition.SpatialMode)1"));
            Assert.That(schema, Does.Contain("spatialCategory: 7u"));
            // Component-level storage/discipline emitted as casts from the attribute values.
            Assert.That(schema, Does.Contain("storageMode: (global::Typhon.Schema.Definition.StorageMode)1"));
            Assert.That(schema, Does.Contain("defaultDiscipline: (global::Typhon.Schema.Definition.DurabilityDiscipline)1"));
        });
    }

    [Test]
    public void ComponentCollectionField_EmitsAotSafeFactoryRegistration()
    {
        const string source = @"
using Typhon.Schema.Definition;
using System.Runtime.InteropServices;

namespace Game
{
    [Component(""Game.Bag"", 1)]
    [StructLayout(LayoutKind.Sequential)]
    public partial struct Bag { public ComponentCollection<int> Items; public int Count; }
}
";
        var (sources, parseErrors) = Run(source);
        var schema = SchemaSource(sources, "Bag");

        Assert.That(parseErrors, Is.Empty, "Generated code must parse. Errors: " + string.Join("; ", parseErrors.Select(d => d.ToString())));
        Assert.Multiple(() =>
        {
            // AOT-safe Type→delegate registration for the collection element type (B2, #409) — replaces MakeGenericType/Activator.
            Assert.That(schema, Does.Contain("global::Typhon.Engine.DatabaseEngine.RegisterComponentCollectionFactory<int>()"));
            // GetComponentSchema becomes a block body (register factories, then return the spec).
            Assert.That(schema, Does.Contain("return new global::Typhon.Schema.Definition.ComponentSchemaSpec("));
        });
    }

    [Test]
    public void NonPartialComponent_IsSkipped()
    {
        const string source = @"
using Typhon.Schema.Definition;

namespace Game
{
    [Component(""Game.Plain"", 1)]
    public struct Plain { public int Value; public int Pad; }
}
";
        var (sources, _) = Run(source);
        Assert.That(SchemaSource(sources), Is.Empty, "A non-partial [Component] struct must not get a generated provider (reflection fallback).");
    }

    [Test]
    public void GlobalNamespaceComponent_EmitsParseableTopLevelProvider()
    {
        const string source = @"
using Typhon.Schema.Definition;
using System.Runtime.InteropServices;

[Component(""Repro.Data"", 1)]
[StructLayout(LayoutKind.Sequential)]
public partial struct Data { public int Value; public int Pad; }
";
        var (sources, parseErrors) = Run(source);
        var schema = SchemaSource(sources);

        Assert.That(parseErrors, Is.Empty, "Global-namespace component must parse. Errors: " + string.Join("; ", parseErrors.Select(d => d.ToString())));
        Assert.That(schema, Does.Contain("partial struct Data : global::Typhon.Schema.Definition.IComponentSchemaProvider"));
        Assert.That(schema, Does.Not.Contain("namespace "), "A global-namespace component must emit top-level code with no namespace wrapper.");
        Assert.That(schema, Does.Not.Contain("<global namespace>"));
    }
}
