using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Typhon.Generators;

namespace Typhon.Generators.Tests;

/// <summary>
/// Tests for the component-registration pipeline of <see cref="ArchetypeAccessorGenerator"/> (feature #514, phase 5): every <c>[Component]</c> struct in the
/// assembly is registered from a single generated <c>[ModuleInitializer]</c> (<c>Typhon.Generated.__TyphonRegistry_*</c>) that calls
/// <c>GeneratedSchemaRegistry.RegisterComponent(typeof(T), new ComponentSchemaSpec(...))</c> — reflection-free, and NOT via an interface on the struct, so
/// components no longer need to be <c>partial</c>.
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
    public static class GeneratedSchemaRegistry { public static void RegisterComponent(System.Type t, ComponentSchemaSpec s) { } }
}
namespace Typhon.Engine
{
    // Presence of this type makes the generator treat the compilation as engine-referencing (HasEngine == true), so the AOT-safe collection factory is emitted.
    public class DatabaseEngine { public static void RegisterComponentCollectionFactory<T>() where T : unmanaged { } }
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

    // The single per-assembly registrar source (Typhon.Generated.__TyphonRegistry_*), or "" if none was emitted.
    private static string Registrar(string[] sources) => sources.FirstOrDefault(s => s.Contains("__TyphonRegistry_")) ?? "";

    [Test]
    public void Component_EmitsModuleInitRegistration_AllAttributeKinds()
    {
        const string source = @"
using Typhon.Schema.Definition;
using System.Runtime.InteropServices;

namespace Game
{
    [Component(""Game.Fk"", 1)]
    [StructLayout(LayoutKind.Sequential)]
    public struct Fk { public long Id; }

    [Component(""Game.Rep"", 3, StorageMode = StorageMode.SingleVersion, DefaultDiscipline = DurabilityDiscipline.Commit)]
    [StructLayout(LayoutKind.Sequential)]
    public struct Rep
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
        var reg = Registrar(sources);

        Assert.That(parseErrors, Is.Empty, "Generated registrar code must parse. Errors: " + string.Join("; ", parseErrors.Select(d => d.ToString())));
        Assert.That(reg, Is.Not.Empty, "A per-assembly __TyphonRegistry module-init must be generated.");

        Assert.Multiple(() =>
        {
            // Registrar shape: a module-init in Typhon.Generated that pushes registrations to the engine.
            Assert.That(reg, Does.Contain("namespace Typhon.Generated"));
            Assert.That(reg, Does.Contain("internal static class __TyphonRegistry_ComponentGeneratorTestAssembly"));
            Assert.That(reg, Does.Contain("[global::System.Runtime.CompilerServices.ModuleInitializer]"));
            // Registration is by type, off the struct — components need not be partial.
            Assert.That(reg, Does.Contain("global::Typhon.Schema.Definition.GeneratedSchemaRegistry.RegisterComponent(typeof(global::Game.Rep)"));
            Assert.That(reg, Does.Contain("new global::Typhon.Schema.Definition.ComponentSchemaSpec("));
            // Offsets are the one residual runtime call.
            Assert.That(reg, Does.Contain("global::System.Runtime.InteropServices.Marshal.OffsetOf<global::Game.Rep>(\"X\")"));
            // Field metadata carried faithfully.
            Assert.That(reg, Does.Contain("previousName: \"Hitpoints\""));
            Assert.That(reg, Does.Contain("hasIndex: true, indexAllowMultiple: true"));
            Assert.That(reg, Does.Contain("isForeignKey: true, foreignKeyTargetType: typeof(global::Game.Fk)"));
            Assert.That(reg, Does.Contain("hasSpatialIndex: true"));
            Assert.That(reg, Does.Contain("spatialMargin: 2f"));
            Assert.That(reg, Does.Contain("spatialCellSize: 4f"));
            Assert.That(reg, Does.Contain("spatialMode: (global::Typhon.Schema.Definition.SpatialMode)1"));
            Assert.That(reg, Does.Contain("spatialCategory: 7u"));
            // Component-level storage/discipline emitted as casts from the attribute values.
            Assert.That(reg, Does.Contain("storageMode: (global::Typhon.Schema.Definition.StorageMode)1"));
            Assert.That(reg, Does.Contain("defaultDiscipline: (global::Typhon.Schema.Definition.DurabilityDiscipline)1"));
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
    public struct Bag { public ComponentCollection<int> Items; public int Count; }
}
";
        var (sources, parseErrors) = Run(source);
        var reg = Registrar(sources);

        Assert.That(parseErrors, Is.Empty, "Generated code must parse. Errors: " + string.Join("; ", parseErrors.Select(d => d.ToString())));
        Assert.Multiple(() =>
        {
            // AOT-safe Type→delegate registration for the collection element type (B2, #409) — replaces MakeGenericType/Activator.
            Assert.That(reg, Does.Contain("global::Typhon.Engine.DatabaseEngine.RegisterComponentCollectionFactory<int>()"));
            // The schema is registered alongside, by type.
            Assert.That(reg, Does.Contain("global::Typhon.Schema.Definition.GeneratedSchemaRegistry.RegisterComponent(typeof(global::Game.Bag)"));
        });
    }

    [Test]
    public void NonPartialComponent_IsIncluded()
    {
        // Feature #514 phase 5: registration moved off the struct into the module-init, so a NON-partial [Component] is now registered like any other.
        const string source = @"
using Typhon.Schema.Definition;

namespace Game
{
    [Component(""Game.Plain"", 1)]
    public struct Plain { public int Value; public int Pad; }
}
";
        var (sources, parseErrors) = Run(source);
        var reg = Registrar(sources);

        Assert.That(parseErrors, Is.Empty, "Generated code must parse. Errors: " + string.Join("; ", parseErrors.Select(d => d.ToString())));
        Assert.That(reg, Does.Contain("global::Typhon.Schema.Definition.GeneratedSchemaRegistry.RegisterComponent(typeof(global::Game.Plain)"),
            "A non-partial [Component] struct must now be registered from the module-init (no interface, no partial requirement).");
    }

    [Test]
    public void PrivateNestedComponent_IsSkipped()
    {
        // A component nested in a private/protected scope cannot be referenced from the top-level registrar class, so it is skipped (reflection fallback).
        const string source = @"
using Typhon.Schema.Definition;

namespace Game
{
    internal class Host
    {
        [Component(""Game.Secret"", 1)]
        private struct Secret { public int Value; public int Pad; }
    }

    [Component(""Game.Visible"", 1)]
    public struct Visible { public int Value; public int Pad; }
}
";
        var (sources, parseErrors) = Run(source);
        var reg = Registrar(sources);

        Assert.That(parseErrors, Is.Empty, "Generated code must parse. Errors: " + string.Join("; ", parseErrors.Select(d => d.ToString())));
        Assert.Multiple(() =>
        {
            Assert.That(reg, Does.Contain("GeneratedSchemaRegistry.RegisterComponent(typeof(global::Game.Visible)"), "A reachable component must be registered.");
            Assert.That(reg, Does.Not.Contain("Secret"), "A private-nested component is unreachable from the registrar and must be skipped.");
        });
    }

    [Test]
    public void GlobalNamespaceComponent_EmitsParseableRegistration()
    {
        const string source = @"
using Typhon.Schema.Definition;
using System.Runtime.InteropServices;

[Component(""Repro.Data"", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct Data { public int Value; public int Pad; }
";
        var (sources, parseErrors) = Run(source);
        var reg = Registrar(sources);

        Assert.That(parseErrors, Is.Empty, "Global-namespace component must parse. Errors: " + string.Join("; ", parseErrors.Select(d => d.ToString())));
        Assert.Multiple(() =>
        {
            // The registrar always lives in Typhon.Generated; the global-namespace component is referenced by its global:: type name.
            Assert.That(reg, Does.Contain("namespace Typhon.Generated"));
            Assert.That(reg, Does.Contain("global::Typhon.Schema.Definition.GeneratedSchemaRegistry.RegisterComponent(typeof(global::Data)"));
        });
    }
}
