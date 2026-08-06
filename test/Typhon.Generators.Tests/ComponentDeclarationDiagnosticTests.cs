using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Typhon.Generators;

namespace Typhon.Generators.Tests;

/// <summary>
/// Tests for the build-time component-declaration diagnostics of <see cref="ArchetypeAccessorGenerator"/> (#678 step 1): a unique <c>[Index]</c> on a
/// component declared by more than one archetype <b>of the same tree</b> (TPH1003), and an archetype re-declaring a component it already inherits (TPH1004).
/// </summary>
/// <remarks>
/// <para>
/// <b>The scope is per tree, because the index is stored per archetype.</b> There is one B+Tree per (archetype, indexed field), so two declarers under one
/// root own two trees with nothing spanning them — enforcing uniqueness would mean probing every sibling tree on each insert, so it is rejected. Two
/// declarers in unrelated trees already own their own trees: independent constraints, nothing to probe. The price is that "unique" means unique within a
/// tree, not database-wide.
/// </para>
/// <para>
/// <b>Why every negative case lives here rather than in the engine suite.</b> The runtime equivalent throws from <c>ArchetypeRegistry.Freeze()</c>, and the
/// generated <c>[ModuleInitializer]</c> registers every <c>[Archetype]</c> in an assembly at load. A violating archetype declared in the engine test assembly
/// would therefore poison assembly load for the whole suite — the negative case cannot exist as a real archetype. An in-memory compilation is the only place
/// it can, which is also why the rule is a compile error first and a runtime throw second.
/// </para>
/// <para>
/// <b>Why TPH1004 exists at all.</b> #678's rule counts declaring archetypes and relies on that set being an antichain — "a child copies its parent's slots
/// and appends only its own, so a component is declared exactly once per inheritance chain". Nothing enforced it: a child re-declaring an ancestor's
/// component was silently accepted and produced a second, unaddressable slot (verified against the registry — <c>ComponentCount</c> 3 with the component at
/// slots 1 <i>and</i> 2, <c>TryGetSlot</c> resolving to 2). TPH1004 makes the assumption true.
/// </para>
/// </remarks>
[TestFixture]
class ComponentDeclarationDiagnosticTests
{
    private const string Stubs = @"
namespace Typhon.Schema.Definition
{
    public sealed class ArchetypeAttribute : System.Attribute { public ArchetypeAttribute() { } }
    public sealed class ComponentAttribute : System.Attribute { public ComponentAttribute(string name, int revision) { } }
    public enum CascadeAction { None = 0, Delete = 1 }
    public sealed class IndexAttribute : System.Attribute { public bool AllowMultiple { get; set; } public CascadeAction OnParentDelete { get; set; } }
    public sealed class SpatialIndexAttribute : System.Attribute { public SpatialIndexAttribute(float margin) { } }
}
namespace Typhon.Engine
{
    public struct Comp<T> { }
    public readonly struct EntityLink<T> where T : class { }
    public abstract class Archetype<TSelf> where TSelf : Archetype<TSelf> { protected static Comp<T> Register<T>() => default; }
    public abstract class Archetype<TSelf, TParent> : Archetype<TSelf> where TSelf : Archetype<TSelf, TParent> where TParent : class { }
}
";

    private static ImmutableArray<Diagnostic> RunDiagnostics(string testSource)
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "ComponentDeclarationDiagnosticTestAssembly",
            new[] { CSharpSyntaxTree.ParseText(Stubs), CSharpSyntaxTree.ParseText(testSource) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new ArchetypeAccessorGenerator().AsSourceGenerator());
        return driver.RunGenerators(compilation).GetRunResult().Diagnostics;
    }

    private static void AssertNoDeclarationDiagnostic(string source)
    {
        var diags = RunDiagnostics(source);
        Assert.That(diags.Where(d => d.Id is "TPH1003" or "TPH1004"), Is.Empty,
            "Expected no declaration diagnostic. Got: " + string.Join("; ", diags.Select(d => d.ToString())));
    }

    // ── TPH1003 — a unique index admits one declaring archetype PER TREE ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two siblings under one root. A query over the root matches both subtrees at once, so a per-subtree structure cannot enforce the constraint between
    /// them — the case the rule exists for.
    /// </summary>
    [Test]
    public void UniqueIndex_TwoDeclarersInOneTree_ReportsTph1003()
    {
        const string source = @"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype] public class LivingArch : Archetype<LivingArch> { public static readonly Comp<Other> O = Register<Other>(); }
[Archetype] public class NpcArch : Archetype<NpcArch, LivingArch> { public static readonly Comp<Account> A = Register<Account>(); }
[Archetype] public class BossArch : Archetype<BossArch, LivingArch> { public static readonly Comp<Account> A = Register<Account>(); }

[Component(""Game.Account"", 1)] public struct Account { [Index] public int AccountId; public int Pad; }
[Component(""Game.Other"", 1)] public struct Other { public int V; public int Pad; }
";
        var diags = RunDiagnostics(source);
        var hit = diags.SingleOrDefault(d => d.Id == "TPH1003");
        Assert.That(hit, Is.Not.Null, "Two declarers in ONE tree must report TPH1003. Got: " + string.Join("; ", diags.Select(d => d.ToString())));

        var message = hit.GetMessage();
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("Account"), "the message must name the component");
            Assert.That(message, Does.Contain("AccountId"), "the message must name the unique field");
            Assert.That(message, Does.Contain("NpcArch").And.Contain("BossArch"), "the message must name every declarer, so the fix needs no investigation");
            Assert.That(message, Does.Contain("LivingArch"), "the message must name the tree root — that is what makes the two declarers collide");
            Assert.That(message, Does.Contain("AllowMultiple"), "the message must state the second of the two fixes");
        });
    }

    /// <summary>
    /// Grandparent and grandchild of one tree, several levels apart. Sharing ANY ancestor is what matters, not being siblings.
    /// </summary>
    [Test]
    public void UniqueIndex_TwoDeclarersDeepInOneTree_ReportsTph1003()
    {
        const string source = @"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype] public class RootArch : Archetype<RootArch> { public static readonly Comp<Other> O = Register<Other>(); }
[Archetype] public class BranchArch : Archetype<BranchArch, RootArch> { public static readonly Comp<Account> A = Register<Account>(); }
[Archetype] public class MidArch : Archetype<MidArch, RootArch> { public static readonly Comp<Other2> O2 = Register<Other2>(); }
[Archetype] public class DeepArch : Archetype<DeepArch, MidArch> { public static readonly Comp<Account> A = Register<Account>(); }

[Component(""Game.Account"", 1)] public struct Account { [Index] public int AccountId; public int Pad; }
[Component(""Game.Other"", 1)] public struct Other { public int V; public int Pad; }
[Component(""Game.Other2"", 1)] public struct Other2 { public int V; public int Pad; }
";
        var diags = RunDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TPH1003"), Is.True,
            "Declarers at different depths of one tree still share a root. Got: " + string.Join("; ", diags.Select(d => d.ToString())));
    }

    /// <summary>
    /// <b>The rule's boundary.</b> Two unrelated trees may each declare the same unique-indexed component: a query names an archetype and matches only its own
    /// subtree, so no query can ever see both. Two independent constraints, each enforceable — and this is what keeps the schema-evolution fixtures legal,
    /// where a V1 and a V2 archetype declare the same component through different CLR structs.
    /// </summary>
    [Test]
    public void UniqueIndex_TwoDeclarersInUnrelatedTrees_IsSilent() => AssertNoDeclarationDiagnostic(@"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype] public class PlayerArch : Archetype<PlayerArch> { public static readonly Comp<Account> A = Register<Account>(); }
[Archetype] public class AdminArch : Archetype<AdminArch> { public static readonly Comp<Account> A = Register<Account>(); }

[Component(""Game.Account"", 1)] public struct Account { [Index] public int AccountId; public int Pad; }
");

    /// <summary>The shape the rule exists to bless: one declarer, any number of archetypes below it inheriting the field.</summary>
    [Test]
    public void UniqueIndex_OneDeclarerWithDescendants_IsSilent() => AssertNoDeclarationDiagnostic(@"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype] public class BaseArch : Archetype<BaseArch> { public static readonly Comp<Account> A = Register<Account>(); }
[Archetype] public class MidArch : Archetype<MidArch, BaseArch> { }
[Archetype] public class LeafArch : Archetype<LeafArch, MidArch> { }

[Component(""Game.Account"", 1)] public struct Account { [Index] public int AccountId; public int Pad; }
");

    /// <summary>Siblings sharing a component is legal and stays legal — the rule is about the CONSTRAINT, not about sharing.</summary>
    [Test]
    public void AllowMultipleIndex_ThreeDeclarers_IsSilent() => AssertNoDeclarationDiagnostic(@"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype] public class RootArch : Archetype<RootArch> { public static readonly Comp<Other> O = Register<Other>(); }
[Archetype] public class LeftArch : Archetype<LeftArch, RootArch> { public static readonly Comp<Loot> L = Register<Loot>(); }
[Archetype] public class RightArch : Archetype<RightArch, RootArch> { public static readonly Comp<Loot> L = Register<Loot>(); }
[Archetype] public class StrangerArch : Archetype<StrangerArch> { public static readonly Comp<Loot> L = Register<Loot>(); }

[Component(""Game.Loot"", 1)] public struct Loot { [Index(AllowMultiple = true)] public int Rarity; public int Pad; }
[Component(""Game.Other"", 1)] public struct Other { public int V; public int Pad; }
");

    [Test]
    public void UnindexedComponent_ThreeDeclarers_IsSilent() => AssertNoDeclarationDiagnostic(@"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype] public class A1 : Archetype<A1> { public static readonly Comp<Plain> P = Register<Plain>(); }
[Archetype] public class A2 : Archetype<A2> { public static readonly Comp<Plain> P = Register<Plain>(); }
[Archetype] public class A3 : Archetype<A3> { public static readonly Comp<Plain> P = Register<Plain>(); }

[Component(""Game.Plain"", 1)] public struct Plain { public int V; public int Pad; }
");

    /// <summary><c>[SpatialIndex]</c> is a different attribute with no uniqueness constraint — the SWG sample shares one across siblings.</summary>
    [Test]
    public void SpatialIndexOnly_TwoDeclarers_IsSilent() => AssertNoDeclarationDiagnostic(@"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype] public class StructureArch : Archetype<StructureArch> { public static readonly Comp<Owner> O = Register<Owner>(); }
[Archetype] public class HarvesterArch : Archetype<HarvesterArch, StructureArch> { public static readonly Comp<Pos> P = Register<Pos>(); }
[Archetype] public class FactoryArch : Archetype<FactoryArch, StructureArch> { public static readonly Comp<Pos> P = Register<Pos>(); }

[Component(""Game.Pos"", 1)] public struct Pos { [SpatialIndex(1.0f)] public long Bounds; public int Pad; }
[Component(""Game.Owner"", 1)] public struct Owner { public int V; public int Pad; }
");

    // ── TPH1004 — a component is declared once per inheritance chain ───────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ChildRedeclaresInheritedComponent_ReportsTph1004()
    {
        const string source = @"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype] public class ParentArch : Archetype<ParentArch> { public static readonly Comp<Shared> S = Register<Shared>(); }
[Archetype] public class ChildArch : Archetype<ChildArch, ParentArch> { public static readonly Comp<Shared> S2 = Register<Shared>(); }

[Component(""Game.Shared"", 1)] public struct Shared { public int V; public int Pad; }
";
        var diags = RunDiagnostics(source);
        var hit = diags.SingleOrDefault(d => d.Id == "TPH1004");
        Assert.That(hit, Is.Not.Null, "Re-declaring an inherited component must report TPH1004. Got: "
                                      + string.Join("; ", diags.Select(d => d.ToString())));

        var message = hit.GetMessage();
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("ChildArch"), "the message must name the offending archetype");
            Assert.That(message, Does.Contain("Shared"), "the message must name the duplicated component");
            Assert.That(message, Does.Contain("ParentArch"), "the message must name the ancestor that already declares it");
        });
    }

    /// <summary>Two levels up — the walk must cover the whole chain, not just the direct parent.</summary>
    [Test]
    public void GrandchildRedeclaresGrandparentComponent_ReportsTph1004()
    {
        const string source = @"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype] public class TopArch : Archetype<TopArch> { public static readonly Comp<Shared> S = Register<Shared>(); }
[Archetype] public class MidArch : Archetype<MidArch, TopArch> { }
[Archetype] public class BottomArch : Archetype<BottomArch, MidArch> { public static readonly Comp<Shared> S2 = Register<Shared>(); }

[Component(""Game.Shared"", 1)] public struct Shared { public int V; public int Pad; }
";
        var diags = RunDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TPH1004"), Is.True,
            "The inherited set spans the whole ancestor chain, not just the direct parent. Got: " + string.Join("; ", diags.Select(d => d.ToString())));
    }

    [Test]
    public void ChildDeclaresADifferentComponent_IsSilent() => AssertNoDeclarationDiagnostic(@"
using Typhon.Engine;
using Typhon.Schema.Definition;

[Archetype] public class ParentArch : Archetype<ParentArch> { public static readonly Comp<Shared> S = Register<Shared>(); }
[Archetype] public class ChildArch : Archetype<ChildArch, ParentArch> { public static readonly Comp<Extra> E = Register<Extra>(); }

[Component(""Game.Shared"", 1)] public struct Shared { public int V; public int Pad; }
[Component(""Game.Extra"", 1)] public struct Extra { public int V; public int Pad; }
");
}
