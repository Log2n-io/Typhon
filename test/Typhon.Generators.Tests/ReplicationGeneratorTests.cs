using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Typhon.Generators;

namespace Typhon.Generators.Tests;

/// <summary>
/// <see cref="ReplicationGenerator"/> (design/Subscriptions/11 § 5): what each attribute emits — the builder call an application would write — and every
/// compile-time diagnostic of § 5.5. The engine suite's <c>ReplicationAttributeTests</c> proves the emitted calls compile to the builder's catalog to the
/// byte; this fixture proves the emission and the refusals, which an in-memory compilation is the only place to exercise.
/// </summary>
[TestFixture]
class ReplicationGeneratorTests
{
    private const string Stubs = @"
namespace Typhon.Schema.Definition
{
    public sealed class ArchetypeAttribute : System.Attribute { }
    public sealed class ComponentAttribute : System.Attribute { public ComponentAttribute(string name, int revision) { } }
    public sealed class FieldAttribute : System.Attribute { }
}
namespace Typhon.Engine
{
    using System;
    using System.Linq.Expressions;
    using Typhon.Protocol;
    public struct Comp<T> { }
    public struct EntityId { }
    public abstract class Archetype<TSelf> where TSelf : Archetype<TSelf> { protected static Comp<T> Register<T>() => default; }
    public abstract class Archetype<TSelf, TParent> : Archetype<TSelf> where TSelf : Archetype<TSelf, TParent> where TParent : class { }
    public readonly struct Codec
    {
        public static Codec Declared<TField>(CodecKind kind, int bits = 0, double min = 0, double max = 0, double scale = 0, int maxBytes = 0,
            bool saturate = false) => default;
    }
    public interface IReplicatedArchetype
    {
        static abstract bool ReplicatedStatic { get; }
        static abstract void DeclareReplication(ArchetypeProjectionBuilder archetype);
    }
    public sealed class MotionBuilder
    {
        public MotionBuilder Tolerance(double metres) => this;
        public MotionBuilder Teleport(double maxSpeedMps) => this;
        public MotionBuilder MaxAge(double seconds) => this;
    }
    public sealed class OwnerBuilder
    {
        public OwnerBuilder Field<TC, TF>(Comp<TC> c, Expression<Func<TC, TF>> s, Codec codec, string name = null, string group = null) where TC : unmanaged
            => this;
    }
    public sealed class ArchetypeProjectionBuilder
    {
        public ArchetypeProjectionBuilder Motion<TC>(Comp<TC> c, Action<MotionBuilder> configure = null) where TC : unmanaged => this;
        public ArchetypeProjectionBuilder Position<TC>(Comp<TC> c) where TC : unmanaged => this;
        public ArchetypeProjectionBuilder Field<TC, TF>(Comp<TC> c, Expression<Func<TC, TF>> s, Codec codec, string name = null, string group = null)
            where TC : unmanaged => this;
        public ArchetypeProjectionBuilder OnEnter<TC, TF>(Comp<TC> c, Expression<Func<TC, TF>> s, Codec codec, string name = null) where TC : unmanaged
            => this;
        public ArchetypeProjectionBuilder Fraction<TC, TV>(Comp<TC> c, Expression<Func<TC, TV>> v, Expression<Func<TC, TV>> m, int bits, string name,
            string group = null) where TC : unmanaged => this;
        public ArchetypeProjectionBuilder Heading<TC, TF>(Comp<TC> c, Expression<Func<TC, TF>> s, int bits, double toleranceDeg, string name = null,
            string group = null) where TC : unmanaged => this;
        public ArchetypeProjectionBuilder Owner(Action<OwnerBuilder> configure) => this;
    }
}
";

    private static readonly MetadataReference[] References =
    [
        .. ((string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(System.IO.Path.PathSeparator)
            .Where(p => System.IO.Path.GetFileName(p).StartsWith("System.") || System.IO.Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
        MetadataReference.CreateFromFile(typeof(Typhon.Protocol.ReplicatedAttribute).Assembly.Location),
    ];

    private const string Usings = "using Typhon.Engine; using Typhon.Protocol; using Typhon.Schema.Definition;\n";

    /// <summary>
    /// Runs the generator, and compiles its output with the source: the emitted text is only proven when it compiles against the builder's signatures.
    /// </summary>
    private static (string Source, ImmutableArray<Diagnostic> Diagnostics, Diagnostic[] CompileErrors) Run(string source,
        MetadataReference extra = null)
    {
        var compilation = CSharpCompilation.Create("ReplicationGeneratorTestAssembly",
            [CSharpSyntaxTree.ParseText(Stubs), CSharpSyntaxTree.ParseText(Usings + source)],
            extra == null ? References : [.. References, extra],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new ReplicationGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var result = driver.GetRunResult();
        Assert.That(result.Results[0].Exception, Is.Null, "the generator must never throw: that drops every output it has");
        var text = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        return (text, result.Diagnostics, errors);
    }

    private static string[] Ids(string source) => Run(source).Diagnostics.Select(d => d.Id).ToArray();

    private const string Components = @"
[Component(""Vitals"", 1)] public struct Vitals
{
    [Field, Fraction(nameof(MaxHealth), Bits = 7, Name = ""hp"", Group = ""vitals"")] public int Health;
    [Field] public int MaxHealth;
    [Field, Owner(CodecKind.Varu, Name = ""credits"", Saturate = true)] public int Credits;
}
[Component(""Brain"", 1)] public struct Brain
{
    [Field, OnEnter(CodecKind.F16, Name = ""aggro"")] public float Aggro;
    [Field, Replicate(CodecKind.Quant, Min = -10, Max = 10, Bits = 16, Group = ""g"")] public float Level;
    [Field, Replicate] public byte Mode;
    [Field, Heading(Bits = 8, ToleranceDeg = 5, Name = ""yaw"")] public float Yaw;
}
[Component(""Place"", 1)] public struct Place { [Field] public float X; }
";

    // ── emission ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AnArchetypesAttributesBecomeItsBuilderCalls()
    {
        var (source, diagnostics, errors) = Run(Components + @"
namespace Game
{
    [Archetype, Replicated] public partial class Creature : Archetype<Creature>
    {
        [Motion(ToleranceM = 0.05, TeleportMps = 12)] public static readonly Comp<Place> Bounds = Register<Place>();
        public static readonly Comp<Brain> Ai = Register<Brain>();
        public static readonly Comp<Vitals> Life = Register<Vitals>();
    }
}");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(errors, Is.Empty, "the emitted source compiles against the builder's signatures");
            Assert.That(source, Does.Contain("namespace Game"));
            Assert.That(source, Does.Contain("partial class Creature : global::Typhon.Engine.IReplicatedArchetype"));
            Assert.That(source, Does.Contain("ReplicatedStatic => false;"));
            Assert.That(source, Does.Contain("archetype.Motion(global::Game.Creature.Bounds, m => m.Tolerance(0.05d).Teleport(12d));"));
            Assert.That(source, Does.Contain(
                "archetype.OnEnter(global::Game.Creature.Ai, (global::Brain x) => x.Aggro, "
                    + "global::Typhon.Engine.Codec.Declared<float>(global::Typhon.Protocol.CodecKind.F16), name: \"aggro\");"));
            Assert.That(source, Does.Contain(
                "archetype.Field(global::Game.Creature.Ai, (global::Brain x) => x.Level, "
                    + "global::Typhon.Engine.Codec.Declared<float>(global::Typhon.Protocol.CodecKind.Quant, bits: 16, min: -10d, max: 10d), group: \"g\");"));
            Assert.That(source, Does.Contain(
                "archetype.Field(global::Game.Creature.Ai, (global::Brain x) => x.Mode, "
                    + "global::Typhon.Engine.Codec.Declared<byte>(global::Typhon.Protocol.CodecKind.Unknown));"),
                "no codec named: the engine's default for the type");
            Assert.That(source, Does.Contain("archetype.Heading(global::Game.Creature.Ai, (global::Brain x) => x.Yaw, 8, 5d, name: \"yaw\");"));
            Assert.That(source, Does.Contain(
                "archetype.Fraction(global::Game.Creature.Life, (global::Vitals x) => x.Health, "
                    + "(global::Vitals x) => x.MaxHealth, bits: 7, name: \"hp\", group: \"vitals\");"));
            Assert.That(source, Does.Contain(
                "owner.Field(global::Game.Creature.Life, (global::Vitals x) => x.Credits, "
                    + "global::Typhon.Engine.Codec.Declared<int>(global::Typhon.Protocol.CodecKind.Varu, saturate: true), name: \"credits\");"));
        });
    }

    [Test]
    public void AStaticArchetypeWithAPositionAndANestedOne()
    {
        var (source, diagnostics, errors) = Run(Components + @"
public partial class Outer
{
    [Archetype, Replicated(Static = true)] public partial class Rock : Archetype<Rock>
    {
        [Position] public static readonly Comp<Place> Bounds = Register<Place>();
    }
}");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(errors, Is.Empty, "the emitted source compiles against the builder's signatures");
            Assert.That(source, Does.Contain("partial class Outer"));
            Assert.That(source, Does.Contain("ReplicatedStatic => true;"));
            Assert.That(source, Does.Contain("archetype.Position(global::Outer.Rock.Bounds);"));
        });
    }

    [Test]
    public void AMessagesAttributesBecomeItsDescriptor()
    {
        var (source, diagnostics, errors) = Run(@"
[ReplicatedMessage] public partial struct MoveTo
{
    [Quant(-8192, 8192, 24)] public double X;
    [EntityRef] public uint Target;
    [Codec(CodecKind.U16, Name = ""spd"")] public int Speed;
    public byte Plain;
}");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(errors, Is.Empty, "the emitted source compiles against the builder's signatures");
            Assert.That(source, Does.Contain("partial struct MoveTo : global::Typhon.Protocol.IReplicatedMessage"));
            Assert.That(source, Does.Contain("new global::Typhon.Protocol.MessageFieldDeclaration(\"X\", "
                + "global::Typhon.Protocol.CodecKind.Quant, 24, -8192d, 8192d, 0d, 0, null, false)"));
            Assert.That(source, Does.Contain("new global::Typhon.Protocol.MessageFieldDeclaration(\"Target\", "
                + "global::Typhon.Protocol.CodecKind.EntityRef, 0, 0d, 0d, 0d, 0, null, false)"));
            Assert.That(source, Does.Contain("new global::Typhon.Protocol.MessageFieldDeclaration(\"Speed\", "
                + "global::Typhon.Protocol.CodecKind.U16, 0, 0d, 0d, 0d, 0, \"spd\", false)"));
            Assert.That(source, Does.Not.Contain("\"Plain\""), "an unattributed field is the builder's default, not the descriptor's");
        });
    }

    // ── diagnostics (§ 5.5) ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ANonPartialArchetypeOrMessageIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Ids(Components + "[Archetype, Replicated] public class Creature : Archetype<Creature> { }"), Is.EqualTo(new[] { "TPH1101" }));
            Assert.That(Ids("[ReplicatedMessage] public struct M { [EntityRef] public uint T; }"), Is.EqualTo(new[] { "TPH1101" }));
        });
    }

    [Test]
    public void TwoPositionsAreRefused()
        => Assert.That(Ids(Components + @"
[Archetype, Replicated] public partial class Creature : Archetype<Creature>
{
    [Motion] public static readonly Comp<Place> A = Register<Place>();
    [Position] public static readonly Comp<Brain> B = Register<Brain>();
}"), Does.Contain("TPH1102"));

    /// <summary>
    /// A field needs no <c>[Field]</c> to be replicated: every instance field of a supported type is stored, and <c>[Field]</c> only renames it or pins
    /// its id. (A retired diagnostic, TPH1103, refused this and fired on ordinary components.)
    /// </summary>
    [Test]
    public void AFieldWithoutFieldAttributeIsReplicated()
    {
        var (source, diagnostics, errors) = Run(@"
[Component(""C"", 1)] public struct C { [Replicate(CodecKind.U8)] public byte Loose; [Field] public float X; }
[Archetype, Replicated] public partial class A : Archetype<A> { [Position] public static readonly Comp<C> X = Register<C>(); }");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(errors, Is.Empty);
            Assert.That(source, Does.Contain("x) => x.Loose"));
        });
    }

    [Test]
    public void AWideTypeWithNoCodecIsRefused()
        => Assert.That(Ids(@"
[Component(""C"", 1)] public struct C { [Field, Replicate] public long Big; [Field, Replicate] public double Real; }
[Archetype, Replicated] public partial class A : Archetype<A> { public static readonly Comp<C> X = Register<C>(); }"),
    Is.EqualTo(new[] { "TPH1104", "TPH1104" }));

    [TestCase("[Field, Fraction(\"Nope\", Name = \"hp\")] public int Value; [Field] public int Max;", TestName = "AFractionOfAMissingFieldIsRefused")]
    [TestCase("[Field, Fraction(nameof(Max), Name = \"hp\")] public float Value; [Field] public int Max;", TestName = "AFractionOfAnotherTypeIsRefused")]
    [TestCase("[Field, Fraction(nameof(Max))] public int Value; [Field] public int Max;", TestName = "AFractionWithoutANameIsRefused")]
    public void AnInvalidFractionIsRefused(string fields)
        => Assert.That(Ids("[Component(\"C\", 1)] public struct C { " + fields + " } "
            + "[Archetype, Replicated] public partial class A : Archetype<A> { public static readonly Comp<C> X = Register<C>(); }"),
                Is.EqualTo(new[] { "TPH1105" }));

    [Test]
    public void AMotionOffACompFieldIsRefused()
        => Assert.That(Ids(Components + @"
[Archetype, Replicated] public partial class A : Archetype<A>
{
    [Motion] public static readonly int NotAComp = 0;
    [Position] public static readonly Comp<Place> Bounds = Register<Place>();
}"), Is.EqualTo(new[] { "TPH1106" }));

    /// <summary>A parent archetype's Comp&lt;T&gt; carries the [Motion] its [Replicated] child inherits: no false TPH1106, and the child emits it.</summary>
    [Test]
    public void AParentArchetypesCompIsReplicatedThroughItsChild()
    {
        var (source, diagnostics, errors) = Run(Components + @"
[Archetype] public partial class Base : Archetype<Base>
{
    [Motion(TeleportMps = 3)] public static readonly Comp<Place> Bounds = Register<Place>();
}
[Archetype, Replicated] public partial class Child : Archetype<Child, Base>
{
    public static readonly Comp<Brain> Ai = Register<Brain>();
}");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(errors, Is.Empty);
            Assert.That(source, Does.Contain("archetype.Motion(global::Base.Bounds, m => m.Teleport(3d));"));
            Assert.That(source, Does.Contain("x) => x.Aggro"));
        });
    }

    /// <summary>A component compiled in another assembly is read from metadata, attributes and all.</summary>
    [Test]
    public void AComponentFromAnotherAssemblyIsReadFromMetadata()
    {
        var library = CSharpCompilation.Create("ComponentLibrary",
            [CSharpSyntaxTree.ParseText(Stubs), CSharpSyntaxTree.ParseText(Usings + Components)], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new System.IO.MemoryStream();
        Assert.That(library.Emit(image).Success, Is.True);

        // The stubs are compiled into the library; the consuming compilation references them from there.
        var consumer = CSharpCompilation.Create("Consumer",
            [CSharpSyntaxTree.ParseText(Usings + @"
[Archetype, Replicated] public partial class Creature : Archetype<Creature>
{
    [Position] public static readonly Comp<Place> Bounds = Register<Place>();
    public static readonly Comp<Vitals> Life = Register<Vitals>();
}")],
            [.. References, MetadataReference.CreateFromImage(image.ToArray())], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver.Create(new ReplicationGenerator().AsSourceGenerator()).RunGeneratorsAndUpdateCompilation(consumer, out var output, out var run);

        Assert.Multiple(() =>
        {
            Assert.That(run, Is.Empty);
            Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
            var generated = string.Join("\n", output.SyntaxTrees.Skip(1).Select(t => t.ToString()));
            Assert.That(generated, Does.Contain("(global::Vitals x) => x.Health, (global::Vitals x) => x.MaxHealth, bits: 7"));
            Assert.That(generated, Does.Contain("owner.Field(global::Creature.Life, (global::Vitals x) => x.Credits"));
        });
    }

    [Test]
    public void AFieldBothPublicAndOwnerOnlyIsRefused()
        => Assert.That(Ids(@"
[Component(""C"", 1)] public struct C { [Field, Replicate(CodecKind.Varu), Owner(CodecKind.Varu)] public int Credits; }
[Archetype, Replicated] public partial class A : Archetype<A> { public static readonly Comp<C> X = Register<C>(); }"), Is.EqualTo(new[] { "TPH1108" }));

    [Test]
    public void AttributesOfTheOtherVocabularyAreRefused()
        => Assert.Multiple(() =>
        {
            Assert.That(Ids("[Component(\"C\", 1)] public struct C { [Field, Quant(0, 1, 8)] public float V; }"), Is.EqualTo(new[] { "TPH1106" }),
                "a message codec on a component field");
            Assert.That(Ids("[ReplicatedMessage] public partial struct M { [Replicate(CodecKind.U16)] public int Damage; }"), Is.EqualTo(new[] { "TPH1106" }),
                "a component attribute on a message field");
            Assert.That(Ids("[ReplicatedMessage] public partial struct M { [EntityRef] internal uint Target; }"), Is.EqualTo(new[] { "TPH1106" }),
                "a codec on a field that does not travel");
        });

    /// <summary>An attribute still being typed binds to nothing: it is skipped, and never throws the generator's other outputs away.</summary>
    [Test]
    public void AHalfTypedAttributeNeverThrows()
    {
        var (_, _, errors) = Run(@"[ReplicatedMessage] public partial struct M { [Quant(0, 1)] public float V; [EntityRef] public uint T; }");
        Assert.That(errors.Select(e => e.Id), Does.Contain("CS7036"), "the compiler reports the missing argument; the generator stays up (asserted in Run)");
    }

    [Test]
    public void AGenericArchetypeIsRefused()
        => Assert.That(Ids(Components + @"
public partial class Holder<T>
{
    [Archetype, Replicated] public partial class A : Archetype<A> { [Position] public static readonly Comp<Place> X = Register<Place>(); }
}"), Is.EqualTo(new[] { "TPH1109" }));

    /// <summary>Keyword identifiers are escaped, and a non-finite tolerance is a literal that compiles.</summary>
    [Test]
    public void KeywordNamesAndNonFiniteValuesCompile()
    {
        var (source, diagnostics, errors) = Run(@"
[Component(""C"", 1)] public struct C { [Field, Heading(ToleranceDeg = double.NaN)] public float @event; [Field] public float X; }
[Archetype, Replicated] public partial class @class : Archetype<@class>
{
    [Position] public static readonly Comp<C> @base = Register<C>();
}");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(errors, Is.Empty);
            Assert.That(source, Does.Contain("x) => x.@event, 16, double.NaN"));
        });
    }

    [Test]
    public void ANumericCodecOnABoolIsRefused()
        => Assert.Multiple(() =>
        {
            Assert.That(Ids(@"
[Component(""C"", 1)] public struct C { [Field, Replicate(CodecKind.Quant, Min = 0, Max = 1, Bits = 8)] public bool Flag; }
[Archetype, Replicated] public partial class A : Archetype<A> { public static readonly Comp<C> X = Register<C>(); }"), Is.EqualTo(new[] { "TPH1107" }));
            Assert.That(Ids("[ReplicatedMessage] public partial struct M { [Quant(0, 1, 8)] public bool Flag; }"), Is.EqualTo(new[] { "TPH1107" }));
        });
}
