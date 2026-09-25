using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Typhon.Generators;

/// <summary>
/// Replication declared on the data (design/Subscriptions/11 § 5). Two outputs, both reflection-free:
/// <list type="bullet">
/// <item>every <c>[Replicated]</c> archetype gets an <c>IReplicatedArchetype</c> implementation whose <c>DeclareReplication</c> is the builder calls its
/// attributes mean — the same calls an application would write — applied by <c>subs.Archetype&lt;T&gt;()</c>;</item>
/// <item>every <c>[ReplicatedMessage]</c> command or event gets an <c>IReplicatedMessage</c> implementation listing its attributed fields, which the
/// engine's command and event builders read. Engine-free, so a contracts assembly referencing only <c>Typhon.Protocol</c> can carry it.</item>
/// </list>
/// Codecs are emitted as <c>Codec.Declared&lt;TField&gt;(kind, …)</c>, so the one mapping from a declared kind to a codec — and its validation, and the
/// defaults by type — is the engine's, not a second copy here. The generator checks only what the engine cannot word as well at run time (§ 5.5), and a
/// type with any diagnostic gets no source at all: never half a projection.
/// </summary>
/// <remarks>
/// The transform output is not equatable (it carries <see cref="Diagnostic"/>s), so the source step reruns on every compilation change. Accepted: the
/// transform itself must rerun anyway — an archetype's projection depends on its components, which live in other files and other assemblies.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ReplicationGenerator : IIncrementalGenerator
{
    private const string Protocol = "Typhon.Protocol.";
    private const string ReplicatedFqn = Protocol + "ReplicatedAttribute";
    private const string MessageFqn = Protocol + "ReplicatedMessageAttribute";
    private const string MotionFqn = Protocol + "MotionAttribute";
    private const string PositionFqn = Protocol + "PositionAttribute";
    private const string ReplicateFqn = Protocol + "ReplicateAttribute";
    private const string OnEnterFqn = Protocol + "OnEnterAttribute";
    private const string OwnerFqn = Protocol + "OwnerAttribute";
    private const string FractionFqn = Protocol + "FractionAttribute";
    private const string HeadingFqn = Protocol + "HeadingAttribute";
    private const string CodecFqn = Protocol + "CodecAttribute";
    private const string QuantFqn = Protocol + "QuantAttribute";
    private const string EntityRefFqn = Protocol + "EntityRefAttribute";

    private static readonly string[] ComponentAttributes = [ReplicateFqn, OnEnterFqn, OwnerFqn, FractionFqn, HeadingFqn];
    private static readonly string[] MessageAttributes = [CodecFqn, QuantFqn, EntityRefFqn];
    private static readonly string[] NumericKinds = ["U8", "I8", "U16", "I16", "U32", "I32", "Varu", "Vari", "F32", "F16", "Quant", "Unorm", "Snorm",
        "Angle", "Bits"];

    private const string Category = "Typhon.Replication";

    internal static readonly DiagnosticDescriptor NotPartial = new(
        "TPH1101", "A replicated type must be partial",
        "'{0}' carries [{1}] but is not partial, and the generator implements {2} on it: add 'partial'",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor TwoPositions = new(
        "TPH1102", "More than one position",
        "Archetype '{0}' marks more than one Comp<T> field [Motion] or [Position]; an entity has one position",
        Category, DiagnosticSeverity.Error, true);

    // TPH1103 is retired: it refused a replicated field without [Field], but every instance field of a supported type is stored — [Field] only
    // renames it or pins its id — so it fired on ordinary components.

    internal static readonly DiagnosticDescriptor NoDefaultCodec = new(
        "TPH1104", "No default codec",
        "'{0}.{1}' is a {2}, which has no default codec (no implicit narrowing, 01 § 2): name one, e.g. [{3}(CodecKind.Quant, Min = …, Max = …, Bits = 24)].",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor BadFraction = new(
        "TPH1105", "Invalid [Fraction]",
        "[Fraction] on '{0}.{1}': {2}",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor Misplaced = new(
        "TPH1106", "Misplaced replication attribute",
        "[{0}] on '{1}' does nothing: {2}",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor KindOnWrongType = new(
        "TPH1107", "A codec the field cannot carry",
        "'{0}.{1}' is a {2}, which CodecKind.{3} cannot carry",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor PublicAndPrivate = new(
        "TPH1108", "Both public and owner-only",
        "'{0}.{1}' is both [Replicate] and [Owner]: [Replicate] sends it to every client, so [Owner] hides nothing. Drop one — a public "
        + "[Fraction] beside a private [Owner] is the way to show a coarse value to all and the exact one to its owner.",
        Category, DiagnosticSeverity.Error, true);

    internal static readonly DiagnosticDescriptor Unsupported = new(
        "TPH1109", "Unsupported replicated type",
        "'{0}' {1}",
        Category, DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var archetypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            ReplicatedFqn,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, ct) => TransformArchetype(ctx, ct));
        context.RegisterSourceOutput(archetypes, static (spc, output) => output.Report(spc));

        var messages = context.SyntaxProvider.ForAttributeWithMetadataName(
            MessageFqn,
            static (node, _) => node is StructDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.ValueText: "struct" },
            static (ctx, ct) => TransformMessage(ctx, ct));
        context.RegisterSourceOutput(messages, static (spc, output) => output.Report(spc));

        // Placement: an attribute where nothing reads it is a silent no-op, which is worse than an error.
        foreach (var fqn in new[] { MotionFqn, PositionFqn }.Concat(ComponentAttributes).Concat(MessageAttributes))
        {
            var placed = context.SyntaxProvider.ForAttributeWithMetadataName(
                fqn,
                static (node, _) => node is VariableDeclaratorSyntax,
                static (ctx, _) => CheckPlacement(ctx));
            context.RegisterSourceOutput(placed, static (spc, output) => output.Report(spc));
        }
    }

    // ── archetypes ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Output TransformArchetype(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var output = new Output();
        var decl = (ClassDeclarationSyntax)ctx.TargetNode;
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        if (!decl.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            output.Diagnostics.Add(Diagnostic.Create(NotPartial, decl.Identifier.GetLocation(), symbol.Name, "Replicated", "IReplicatedArchetype"));
            return output;
        }

        if (ctx.SemanticModel.Compilation.GetTypeByMetadataName("Typhon.Engine.IReplicatedArchetype") == null)
        {
            output.Diagnostics.Add(Diagnostic.Create(Unsupported, decl.Identifier.GetLocation(), symbol.Name,
                "is [Replicated] in an assembly that does not reference Typhon.Engine: an archetype's replication is declared where the engine is"));
            return output;
        }

        if (!CheckShape(symbol, decl, output))
        {
            return output;
        }

        var isStatic = ctx.Attributes[0].NamedArguments.Any(a => a.Key == "Static" && a.Value.Value is true);
        var comps = new List<IFieldSymbol>();
        CollectComps(symbol, comps, ct);

        var body = new List<string>();
        var owner = new List<string>();
        var positions = 0;
        foreach (var comp in comps)
        {
            ct.ThrowIfCancellationRequested();
            var compRef = Global(comp.ContainingType) + "." + Id(comp.Name);
            var compLocation = comp.Locations.FirstOrDefault(l => l.IsInSource) ?? decl.Identifier.GetLocation();
            var component = ((INamedTypeSymbol)comp.Type).TypeArguments[0];
            var componentFqn = Global(component);

            foreach (var attr in BoundAttributes(comp))
            {
                var name = attr.AttributeClass.ToDisplayString();
                if (name == MotionFqn)
                {
                    positions++;
                    var motion = new StringBuilder();
                    Append(motion, attr, "ToleranceM", ".Tolerance");
                    Append(motion, attr, "TeleportMps", ".Teleport");
                    Append(motion, attr, "MaxAgeS", ".MaxAge");
                    body.Add(motion.Length == 0 ? $"archetype.Motion({compRef});" : $"archetype.Motion({compRef}, m => m{motion});");
                }
                else if (name == PositionFqn)
                {
                    positions++;
                    body.Add($"archetype.Position({compRef});");
                }
            }

            foreach (var member in component.GetMembers())
            {
                if (member is not IFieldSymbol { IsStatic: false } field)
                {
                    continue;
                }

                var at = LocationOf(field, compLocation);
                var isPublic = false;
                var isOwner = false;
                foreach (var attr in BoundAttributes(field))
                {
                    string statement = null;
                    switch (attr.AttributeClass.ToDisplayString())
                    {
                        case ReplicateFqn:
                            isPublic = true;
                            statement = FieldCall("Field", compRef, componentFqn, component, field, attr, output, "Replicate", withGroup: true, at);
                            break;
                        case OnEnterFqn:
                            statement = FieldCall("OnEnter", compRef, componentFqn, component, field, attr, output, "OnEnter", withGroup: false, at);
                            break;
                        case OwnerFqn:
                            isOwner = true;
                            var ownerCall = FieldCall("Field", compRef, componentFqn, component, field, attr, output, "Owner", withGroup: true, at);
                            if (ownerCall != null)
                            {
                                owner.Add("owner" + ownerCall);
                            }

                            break;
                        case FractionFqn:
                            statement = FractionCall(compRef, componentFqn, component, field, attr, output, at);
                            break;
                        case HeadingFqn:
                            statement = HeadingCall(compRef, componentFqn, field, attr);
                            break;
                        default:
                            continue;
                    }

                    if (statement != null)
                    {
                        body.Add("archetype" + statement + ";");
                    }
                }

                if (isPublic && isOwner)
                {
                    output.Diagnostics.Add(Diagnostic.Create(PublicAndPrivate, at, component.Name, field.Name));
                }

            }
        }

        if (positions > 1)
        {
            output.Diagnostics.Add(Diagnostic.Create(TwoPositions, decl.Identifier.GetLocation(), symbol.Name));
        }

        if (owner.Count > 0)
        {
            body.Add("archetype.Owner(owner =>");
            body.Add("{");
            foreach (var line in owner)
            {
                body.Add("    " + line + ";");
            }

            body.Add("});");
        }

        if (output.Diagnostics.Count > 0)
        {
            return output;
        }

        var members = new List<string>
        {
            "static bool global::Typhon.Engine.IReplicatedArchetype.ReplicatedStatic => " + (isStatic ? "true" : "false") + ";",
            "",
            "static void global::Typhon.Engine.IReplicatedArchetype.DeclareReplication(global::Typhon.Engine.ArchetypeProjectionBuilder archetype)",
            "{",
        };
        members.AddRange(body.Select(b => "    " + b));
        members.Add("}");

        output.HintName = HintName(symbol, "Replication");
        output.Source = EmitPartial(symbol, "class", "global::Typhon.Engine.IReplicatedArchetype", members);
        return output;
    }

    /// <summary>Comp&lt;T&gt; static fields, the parent archetype's first — the order the accessor generator uses.</summary>
    private static void CollectComps(INamedTypeSymbol archetype, List<IFieldSymbol> comps, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (archetype.BaseType is { IsGenericType: true, TypeArguments.Length: 2 } baseType && baseType.TypeArguments[1] is INamedTypeSymbol parent)
        {
            CollectComps(parent, comps, ct);
        }

        foreach (var member in archetype.GetMembers())
        {
            if (member is IFieldSymbol { IsStatic: true } field && IsComp(field.Type))
            {
                comps.Add(field);
            }
        }
    }

    private static bool IsComp(ITypeSymbol type)
        => type is INamedTypeSymbol { Name: "Comp", TypeArguments.Length: 1 } named && named.ContainingNamespace?.ToDisplayString() == "Typhon.Engine";

    private static string FieldCall(string method, string compRef, string componentFqn, ITypeSymbol component, IFieldSymbol field, AttributeData attr,
        Output output, string attributeName, bool withGroup, Location at)
    {
        var codec = CodecExpression(component, field, attr, output, attributeName, at);
        if (codec == null)
        {
            return null;
        }

        var call = new StringBuilder();
        call.Append('.').Append(method).Append('(').Append(compRef).Append(", (").Append(componentFqn).Append(" x) => x.").Append(Id(field.Name))
            .Append(", ").Append(codec);
        AppendNamed(call, "name", Named(attr, "Name"));
        if (withGroup)
        {
            AppendNamed(call, "group", Named(attr, "Group"));
        }

        return call.Append(')').ToString();
    }

    private static string FractionCall(string compRef, string componentFqn, ITypeSymbol component, IFieldSymbol field, AttributeData attr, Output output,
        Location at)
    {
        var maxName = attr.ConstructorArguments.Length == 1 ? attr.ConstructorArguments[0].Value as string : null;
        var max = component.GetMembers(maxName ?? string.Empty).OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic);
        string problem = null;
        if (max == null)
        {
            problem = $"'{component.Name}' has no field '{maxName}' to be the maximum";
        }
        else if (!SymbolEqualityComparer.Default.Equals(max.Type, field.Type) || !IsNumeric(field.Type))
        {
            problem = $"the value and '{maxName}' must be the same numeric type";
        }
        else if (Named(attr, "Name") is not string { Length: > 0 })
        {
            problem = "set Name — neither the value's name nor the maximum's describes the ratio";
        }

        if (problem != null)
        {
            output.Diagnostics.Add(Diagnostic.Create(BadFraction, at, component.Name, field.Name, problem));
            return null;
        }

        // The attribute's own defaults, repeated: a property left unset is absent from the attribute data, and a metadata attribute's initializer is not
        // readable from here. FractionAttribute.Bits and HeadingAttribute's are the source these must match.
        var bits = Named(attr, "Bits") is int b ? b : 8;
        var call = new StringBuilder();
        call.Append(".Fraction(").Append(compRef).Append(", (").Append(componentFqn).Append(" x) => x.").Append(Id(field.Name))
            .Append(", (").Append(componentFqn).Append(" x) => x.").Append(Id(maxName))
            .Append(", bits: ").Append(bits.ToString(CultureInfo.InvariantCulture));
        AppendNamed(call, "name", Named(attr, "Name"));
        AppendNamed(call, "group", Named(attr, "Group"));
        return call.Append(')').ToString();
    }

    private static string HeadingCall(string compRef, string componentFqn, IFieldSymbol field, AttributeData attr)
    {
        var bits = Named(attr, "Bits") is int b ? b : 16;
        var tolerance = Named(attr, "ToleranceDeg") is double d ? d : 2d;
        var call = new StringBuilder();
        call.Append(".Heading(").Append(compRef).Append(", (").Append(componentFqn).Append(" x) => x.").Append(Id(field.Name))
            .Append(", ").Append(bits.ToString(CultureInfo.InvariantCulture)).Append(", ").Append(Literal(tolerance));
        AppendNamed(call, "name", Named(attr, "Name"));
        return call.Append(')').ToString();
    }

    // ── messages ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Output TransformMessage(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var output = new Output();
        var decl = (TypeDeclarationSyntax)ctx.TargetNode;
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        if (!decl.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            output.Diagnostics.Add(Diagnostic.Create(NotPartial, decl.Identifier.GetLocation(), symbol.Name, "ReplicatedMessage", "IReplicatedMessage"));
            return output;
        }

        if (!CheckShape(symbol, decl, output))
        {
            return output;
        }

        var entries = new List<string>();
        foreach (var member in symbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (member is not IFieldSymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public } field)
            {
                continue;
            }

            var at = LocationOf(field, decl.Identifier.GetLocation());
            foreach (var attr in BoundAttributes(field))
            {
                if (!MessageAttributes.Contains(attr.AttributeClass.ToDisplayString()))
                {
                    continue;
                }

                var codec = Declaration(attr, symbol, field, output, at);
                if (codec.Kind == null || !CheckKind(symbol, field, codec.Kind, output, at))
                {
                    continue;
                }

                entries.Add("new global::Typhon.Protocol.MessageFieldDeclaration(" + SymbolDisplay.FormatLiteral(field.Name, true)
                    + ", global::Typhon.Protocol.CodecKind." + codec.Kind
                    + ", " + codec.Bits.ToString(CultureInfo.InvariantCulture)
                    + ", " + Literal(codec.Min) + ", " + Literal(codec.Max) + ", " + Literal(codec.Scale)
                    + ", " + codec.MaxBytes.ToString(CultureInfo.InvariantCulture)
                    + ", " + (Named(attr, "Name") is string { Length: > 0 } wire ? SymbolDisplay.FormatLiteral(wire, true) : "null")
                    + ", " + (codec.Saturate ? "true" : "false") + ")");
            }
        }

        if (output.Diagnostics.Count > 0)
        {
            return output;
        }

        var members = new List<string>
        {
            "global::Typhon.Protocol.MessageFieldDeclaration[] global::Typhon.Protocol.IReplicatedMessage.ReplicatedFields() =>",
            "[",
        };
        members.AddRange(entries.Select(e => "    " + e + ","));
        members.Add("];");

        output.HintName = HintName(symbol, "ReplicatedMessage");
        output.Source = EmitPartial(symbol, symbol.IsRecord ? "record struct" : "struct", "global::Typhon.Protocol.IReplicatedMessage", members);
        return output;
    }

    // ── placement and shape ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Output CheckPlacement(GeneratorAttributeSyntaxContext ctx)
    {
        var output = new Output();
        if (ctx.TargetSymbol is not IFieldSymbol field || ctx.Attributes.Length == 0 || ctx.Attributes[0].AttributeClass == null)
        {
            return output;
        }

        var attributeClass = ctx.Attributes[0].AttributeClass;
        var fqn = attributeClass.ToDisplayString();
        var owner = field.ContainingType;
        var isMessage = owner.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == MessageFqn);
        string problem = null;
        if (fqn is MotionFqn or PositionFqn)
        {
            // A parent archetype's Comp<T> is read through its [Replicated] children, so the declaring type need not be [Replicated] itself.
            if (!field.IsStatic || !IsComp(field.Type))
            {
                problem = "it belongs on a Comp<T> field of an archetype";
            }
        }
        else if (MessageAttributes.Contains(fqn))
        {
            if (!isMessage)
            {
                problem = "it describes a command or event field and belongs on a public field of a [ReplicatedMessage] struct; a component field takes "
                          + "[Replicate], [OnEnter] or [Owner], whose codec is the same kind and values";
            }
            else if (field.IsStatic || field.DeclaredAccessibility != Accessibility.Public)
            {
                problem = "only a public instance field travels";
            }
        }
        else if (isMessage)
        {
            problem = "a [ReplicatedMessage] field takes [Codec], [Quant] or [EntityRef]; the component attributes describe an archetype's projection";
        }
        else if (field.IsStatic)
        {
            problem = "only an instance field of a component is stored";
        }

        if (problem != null)
        {
            output.Diagnostics.Add(Diagnostic.Create(Misplaced, ctx.TargetNode.GetLocation(), attributeClass.Name.Replace("Attribute", string.Empty),
                owner.Name + "." + field.Name, problem));
        }

        return output;
    }

    /// <summary>What the emitted partial can express: no generic type, and every containing type partial too.</summary>
    private static bool CheckShape(INamedTypeSymbol symbol, SyntaxNode decl, Output output)
    {
        for (var type = symbol; type != null; type = type.ContainingType)
        {
            if (type.IsGenericType)
            {
                output.Diagnostics.Add(Diagnostic.Create(Unsupported, decl.GetLocation(), symbol.Name,
                    "is generic or nested in a generic type, which replication by attributes does not support: declare it with the builder"));
                return false;
            }

            if (!SymbolEqualityComparer.Default.Equals(type, symbol)
                && type.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax t && !t.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                output.Diagnostics.Add(Diagnostic.Create(Unsupported, decl.GetLocation(), symbol.Name,
                    $"is nested in '{type.Name}', which is not partial: the generated implementation is declared inside it, so it must be"));
                return false;
            }
        }

        return true;
    }

    // ── codecs ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private struct CodecValues
    {
        public string Kind;
        public int Bits;
        public double Min;
        public double Max;
        public double Scale;
        public int MaxBytes;
        public bool Saturate;
    }

    /// <summary>
    /// A codec attribute's values: its kind from the constructor (or the shorthand's), the rest from its named arguments. <see cref="CodecValues.Kind"/>
    /// is <see langword="null"/> after reporting an undefined <c>CodecKind</c>.
    /// </summary>
    private static CodecValues Declaration(AttributeData attr, ITypeSymbol owner, IFieldSymbol field, Output output, Location at)
    {
        var values = new CodecValues { Kind = "Unknown" };
        var args = attr.ConstructorArguments;
        switch (attr.AttributeClass.Name)
        {
            case "QuantAttribute":
                values.Kind = "Quant";
                values.Min = ToDouble(args[0].Value);
                values.Max = ToDouble(args[1].Value);
                values.Bits = args[2].Value is int quantBits ? quantBits : 0;
                break;
            case "EntityRefAttribute":
                values.Kind = "EntityRef";
                break;
            default:
                if (args.Length == 1 && args[0].Type is INamedTypeSymbol kindType)
                {
                    values.Kind = EnumMemberName(kindType, args[0].Value);
                    if (values.Kind == null)
                    {
                        output.Diagnostics.Add(Diagnostic.Create(Unsupported, at, owner.Name + "." + field.Name,
                            $"declares CodecKind value {args[0].Value}, which is not a defined codec"));
                        return values;
                    }
                }

                break;
        }

        if (Named(attr, "Bits") is int bits)
        {
            values.Bits = bits;
        }

        if (Named(attr, "Min") is { } min)
        {
            values.Min = ToDouble(min);
        }

        if (Named(attr, "Max") is { } max)
        {
            values.Max = ToDouble(max);
        }

        if (Named(attr, "Scale") is { } scale)
        {
            values.Scale = ToDouble(scale);
        }

        if (Named(attr, "MaxBytes") is int maxBytes)
        {
            values.MaxBytes = maxBytes;
        }

        values.Saturate = Named(attr, "Saturate") is true;
        return values;
    }

    /// <summary><c>Codec.Declared&lt;TField&gt;(kind, …)</c> for a component field, or <see langword="null"/> after reporting why it cannot be.</summary>
    private static string CodecExpression(ITypeSymbol owner, IFieldSymbol field, AttributeData attr, Output output, string attributeName, Location at)
    {
        var values = Declaration(attr, owner, field, output, at);
        if (values.Kind == null)
        {
            return null;
        }

        if (values.Kind == "Unknown" && !HasDefaultCodec(field.Type))
        {
            output.Diagnostics.Add(Diagnostic.Create(NoDefaultCodec, at, owner.Name, field.Name, field.Type.ToDisplayString(), attributeName));
            return null;
        }

        if (!CheckKind(owner, field, values.Kind, output, at))
        {
            return null;
        }

        var call = new StringBuilder();
        call.Append("global::Typhon.Engine.Codec.Declared<").Append(Global(field.Type)).Append(">(global::Typhon.Protocol.CodecKind.").Append(values.Kind);
        if (values.Bits != 0)
        {
            call.Append(", bits: ").Append(values.Bits.ToString(CultureInfo.InvariantCulture));
        }

        if (values.Min != 0)
        {
            call.Append(", min: ").Append(Literal(values.Min));
        }

        if (values.Max != 0)
        {
            call.Append(", max: ").Append(Literal(values.Max));
        }

        if (values.Scale != 0)
        {
            call.Append(", scale: ").Append(Literal(values.Scale));
        }

        if (values.MaxBytes != 0)
        {
            call.Append(", maxBytes: ").Append(values.MaxBytes.ToString(CultureInfo.InvariantCulture));
        }

        if (values.Saturate)
        {
            call.Append(", saturate: true");
        }

        return call.Append(')').ToString();
    }

    /// <summary>
    /// The one type check worth doing at compile time (§ 5.5): a numeric codec on a field that is not a number — a <c>Quant</c> on a <c>bool</c>. Every
    /// other constraint (widths, ranges) is the engine's factory's, which words it with the values in hand.
    /// </summary>
    private static bool CheckKind(ITypeSymbol owner, IFieldSymbol field, string kind, Output output, Location at)
    {
        if (NumericKinds.Contains(kind) && !IsNumeric(field.Type) && field.Type.TypeKind != TypeKind.Enum)
        {
            output.Diagnostics.Add(Diagnostic.Create(KindOnWrongType, at, owner.Name, field.Name, field.Type.ToDisplayString(), kind));
            return false;
        }

        return true;
    }

    /// <summary>Mirrors the engine's <c>MessageContract.DefaultCodec</c>: the types with a codec of their own.</summary>
    private static bool HasDefaultCodec(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Single:
                return true;
        }

        return type.Name == "EntityId" && type.ContainingNamespace?.ToDisplayString() == "Typhon.Engine";
    }

    private static bool IsNumeric(ITypeSymbol type) => type.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16
        or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
        or SpecialType.System_Single or SpecialType.System_Double;

    // ── emission helpers ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string EmitPartial(INamedTypeSymbol symbol, string keyword, string iface, List<string> members)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable CS1591 // an explicit interface implementation needs no documentation of its own");
        sb.AppendLine();

        var ns = symbol.ContainingNamespace;
        var indent = string.Empty;
        if (ns is { IsGlobalNamespace: false })
        {
            sb.Append("namespace ").AppendLine(ns.ToDisplayString());
            sb.AppendLine("{");
            indent = "    ";
        }

        var parents = new List<INamedTypeSymbol>();
        for (var parent = symbol.ContainingType; parent != null; parent = parent.ContainingType)
        {
            parents.Insert(0, parent);
        }

        foreach (var parent in parents)
        {
            sb.Append(indent).Append("partial ").Append(parent.IsRecord ? "record " : string.Empty)
                .Append(parent.TypeKind == TypeKind.Struct ? "struct " : "class ").AppendLine(Id(parent.Name));
            sb.Append(indent).AppendLine("{");
            indent += "    ";
        }

        sb.Append(indent).Append("partial ").Append(keyword).Append(' ').Append(Id(symbol.Name)).Append(" : ").AppendLine(iface);
        sb.Append(indent).AppendLine("{");
        foreach (var member in members)
        {
            sb.Append(member.Length == 0 ? string.Empty : indent + "    ").AppendLine(member);
        }

        sb.Append(indent).AppendLine("}");
        for (var i = parents.Count; i > 0; i--)
        {
            indent = indent.Substring(4);
            sb.Append(indent).AppendLine("}");
        }

        if (ns is { IsGlobalNamespace: false })
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private static string HintName(INamedTypeSymbol symbol, string suffix)
        => symbol.ToDisplayString().Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_').Replace('@', '_') + "." + suffix + ".g.cs";

    private static string Global(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>An identifier as it must be written: a keyword gets its <c>@</c>.</summary>
    private static string Id(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    /// <summary>
    /// The attributes that bound: one being typed — <c>[Quant(0, 1)]</c> — has no constructor and empty arguments, and reading it would throw, which drops
    /// every output of this generator and turns every <c>subs.Archetype&lt;T&gt;()</c> red.
    /// </summary>
    private static IEnumerable<AttributeData> BoundAttributes(ISymbol symbol)
        => symbol.GetAttributes().Where(a => a.AttributeClass != null && a.AttributeConstructor != null);

    private static object Named(AttributeData attr, string name)
    {
        foreach (var pair in attr.NamedArguments)
        {
            if (pair.Key == name)
            {
                return pair.Value.Value;
            }
        }

        return null;
    }

    private static void Append(StringBuilder motion, AttributeData attr, string property, string method)
    {
        if (Named(attr, property) is double value && !double.IsNaN(value))
        {
            motion.Append(method).Append('(').Append(Literal(value)).Append(')');
        }
    }

    private static void AppendNamed(StringBuilder call, string parameter, object value)
    {
        if (value is string { Length: > 0 } text)
        {
            call.Append(", ").Append(parameter).Append(": ").Append(SymbolDisplay.FormatLiteral(text, true));
        }
    }

    /// <summary>
    /// A double as a C# literal that reads back to the same bits whichever runtime hosts the generator — "R" is not guaranteed to round-trip on the .NET
    /// Framework an IDE may host it on, and two hosts emitting two literals would give two catalog hashes.
    /// </summary>
    private static string Literal(double value)
    {
        if (double.IsNaN(value))
        {
            return "double.NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "double.PositiveInfinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "double.NegativeInfinity";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) != value)
        {
            text = value.ToString("G17", CultureInfo.InvariantCulture);
        }

        return text + "d";
    }

    private static double ToDouble(object value) => value == null ? 0d : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static string EnumMemberName(INamedTypeSymbol enumType, object value)
    {
        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol { HasConstantValue: true } field && Equals(field.ConstantValue, value))
            {
                return field.Name;
            }
        }

        return null;
    }

    /// <summary>The field's own location, or — for a component compiled in another assembly — the <c>Comp&lt;T&gt;</c> field that brought it in.</summary>
    private static Location LocationOf(IFieldSymbol field, Location fallback) => field.Locations.FirstOrDefault(l => l.IsInSource) ?? fallback;

    /// <summary>One transform's result: a source, or the diagnostics that replaced it.</summary>
    private sealed class Output
    {
        public string HintName;
        public string Source;
        public readonly List<Diagnostic> Diagnostics = [];

        public void Report(SourceProductionContext spc)
        {
            foreach (var diagnostic in Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic);
            }

            if (Source != null)
            {
                spc.AddSource(HintName, Source);
            }
        }
    }
}
