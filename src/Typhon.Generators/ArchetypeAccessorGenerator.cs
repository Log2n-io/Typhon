using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Typhon.Generators;

/// <summary>
/// Incremental source generator that emits <c>Refs</c> / <c>MutRefs</c> ref structs and
/// <c>ReadAll</c> / <c>ReadWriteAll</c> static methods for each <c>[Archetype]</c> class.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class ArchetypeAccessorGenerator : IIncrementalGenerator
{
    private const string ArchetypeAttributeFqn = "Typhon.Schema.Definition.ArchetypeAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var pipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: ArchetypeAttributeFqn,
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, ct) => TransformArchetype(ctx, ct)
        );

        context.RegisterSourceOutput(pipeline, static (spc, model) =>
        {
            if (model == null)
            {
                return;
            }

            var source = Emit(model);
            spc.AddSource($"{model.ClassName}.g.cs", source);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Transform: syntax + semantic model → ArchetypeModel
    // ═══════════════════════════════════════════════════════════════════════

    private static ArchetypeModel TransformArchetype(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.TargetNode;
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;

        // Must be partial — skip silently if not (user can add partial)
        bool isPartial = false;
        foreach (var modifier in classDecl.Modifiers)
        {
            if (modifier.Text == "partial")
            {
                isPartial = true;
                break;
            }
        }

        if (!isPartial)
        {
            return null;
        }

        // Collect all Comp<T> fields: parent-first, then own
        var allFields = new List<CompFieldModel>();
        int inheritedCount = CollectParentFields(symbol, allFields, ct);
        CollectOwnFields(symbol, allFields, ct);

        if (allFields.Count == 0)
        {
            return null;
        }

        // Determine accessibility
        string accessibility = symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Private => "private",
            _ => "internal"
        };

        // Build nesting chain (if archetype is nested inside other types)
        var nestingParents = new List<string>();
        var containingType = symbol.ContainingType;
        while (containingType != null)
        {
            ct.ThrowIfCancellationRequested();
            string keyword = containingType.IsRecord ? "record" : "class";
            string containingAccess = containingType.DeclaredAccessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Internal => "internal",
                _ => "internal"
            };
            nestingParents.Insert(0, $"{containingAccess} partial {keyword} {containingType.Name}");
            containingType = containingType.ContainingType;
        }

        // A global-namespace symbol's ContainingNamespace is non-null and its ToDisplayString() yields the literal
        // "<global namespace>", NOT "". Treat it as empty so Emit takes its top-level path (types are already emitted
        // with global::-qualified references). Otherwise we'd emit `namespace <global namespace>` — unparseable (#505).
        var containingNs = symbol.ContainingNamespace;
        return new ArchetypeModel(
            ns: (containingNs == null || containingNs.IsGlobalNamespace) ? "" : containingNs.ToDisplayString(),
            className: symbol.Name,
            accessibility: accessibility,
            allCompFields: allFields.ToArray(),
            inheritedCount: inheritedCount,
            nestingParents: nestingParents.ToArray()
        );
    }

    /// <summary>Recursively collect parent archetype Comp fields. Returns total inherited field count.</summary>
    private static int CollectParentFields(INamedTypeSymbol archetypeType, List<CompFieldModel> result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var baseType = archetypeType.BaseType;
        if (baseType == null || !baseType.IsGenericType)
        {
            return 0;
        }

        // Archetype<TSelf, TParent> has 2 type args — extract TParent
        // Archetype<TSelf> has 1 type arg — root, no parent
        if (baseType.TypeArguments.Length != 2)
        {
            return 0;
        }

        if (!(baseType.TypeArguments[1] is INamedTypeSymbol parentType))
        {
            return 0;
        }

        // Recurse for grandparent first (parent-first ordering)
        CollectParentFields(parentType, result, ct);
        CollectOwnFields(parentType, result, ct);

        return result.Count;
    }

    /// <summary>Collect Comp&lt;T&gt; static readonly fields declared directly on this type.</summary>
    private static void CollectOwnFields(INamedTypeSymbol type, List<CompFieldModel> result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var member in type.GetMembers())
        {
            if (!(member is IFieldSymbol field))
            {
                continue;
            }

            if (!field.IsStatic || !field.IsReadOnly)
            {
                continue;
            }

            if (!(field.Type is INamedTypeSymbol fieldType))
            {
                continue;
            }

            if (!fieldType.IsGenericType || fieldType.Name != "Comp" || fieldType.TypeArguments.Length != 1)
            {
                continue;
            }

            var compType = fieldType.TypeArguments[0];

            result.Add(new CompFieldModel(
                fieldName: field.Name,
                componentTypeFullName: compType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                declaringClassFullName: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Emit: ArchetypeModel → source code
    // ═══════════════════════════════════════════════════════════════════════

    private static string Emit(ArchetypeModel model)
    {
        var sb = new StringBuilder(2048);

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable CS8019 // Unnecessary using directive");
        sb.AppendLine();

        bool hasNamespace = !string.IsNullOrEmpty(model.Namespace);
        if (hasNamespace)
        {
            sb.Append("namespace ").AppendLine(model.Namespace);
            sb.AppendLine("{");
        }

        string indent = hasNamespace ? "    " : "";

        // Open nesting parents
        foreach (var parent in model.NestingParents)
        {
            sb.Append(indent).AppendLine(parent);
            sb.Append(indent).AppendLine("{");
            indent += "    ";
        }

        // Open the archetype partial class
        sb.Append(indent).Append(model.Accessibility).Append(" partial class ").AppendLine(model.ClassName);
        sb.Append(indent).AppendLine("{");

        string memberIndent = indent + "    ";
        string fieldIndent = memberIndent + "    ";

        // ── Refs (read-only) ──
        sb.Append(memberIndent).Append("/// <summary>Read-only zero-copy component refs for ")
          .Append(model.ClassName).Append(" (").Append(model.AllCompFields.Length).AppendLine(" components).</summary>");
        sb.Append(memberIndent).AppendLine("public ref struct Refs");
        sb.Append(memberIndent).AppendLine("{");
        foreach (var field in model.AllCompFields)
        {
            sb.Append(fieldIndent).Append("public ref readonly ").Append(field.ComponentTypeFullName)
              .Append(" ").Append(field.FieldName).AppendLine(";");
        }
        sb.Append(memberIndent).AppendLine("}");
        sb.AppendLine();

        // ── MutRefs (mutable) ──
        sb.Append(memberIndent).Append("/// <summary>Mutable zero-copy component refs for ")
          .Append(model.ClassName).Append(" (").Append(model.AllCompFields.Length).AppendLine(" components).</summary>");
        sb.Append(memberIndent).AppendLine("public ref struct MutRefs");
        sb.Append(memberIndent).AppendLine("{");
        foreach (var field in model.AllCompFields)
        {
            sb.Append(fieldIndent).Append("public ref ").Append(field.ComponentTypeFullName)
              .Append(" ").Append(field.FieldName).AppendLine(";");
        }
        sb.Append(memberIndent).AppendLine("}");
        sb.AppendLine();

        // ── ReadAll ──
        sb.Append(memberIndent).AppendLine("/// <summary>Open entity read-only and return all component refs. Zero-copy.</summary>");
        sb.Append(memberIndent).AppendLine(
            "public static Refs ReadAll(global::Typhon.Engine.Transaction tx, global::Typhon.Engine.EntityId id)");
        sb.Append(memberIndent).AppendLine("{");
        sb.Append(fieldIndent).AppendLine("var entity = tx.Open(id);");
        sb.Append(fieldIndent).AppendLine("var r = new Refs();");
        foreach (var field in model.AllCompFields)
        {
            sb.Append(fieldIndent).Append("r.").Append(field.FieldName).Append(" = ref entity.Read(")
              .Append(field.DeclaringClassFullName).Append(".").Append(field.FieldName).AppendLine(");");
        }
        sb.Append(fieldIndent).AppendLine("return r;");
        sb.Append(memberIndent).AppendLine("}");
        sb.AppendLine();

        // ── ReadWriteAll ──
        sb.Append(memberIndent).AppendLine("/// <summary>Open entity for mutation and return all mutable component refs. Zero-copy.</summary>");
        sb.Append(memberIndent).AppendLine(
            "public static MutRefs ReadWriteAll(global::Typhon.Engine.Transaction tx, global::Typhon.Engine.EntityId id)");
        sb.Append(memberIndent).AppendLine("{");
        sb.Append(fieldIndent).AppendLine("var entity = tx.OpenMut(id);");
        sb.Append(fieldIndent).AppendLine("var r = new MutRefs();");
        foreach (var field in model.AllCompFields)
        {
            sb.Append(fieldIndent).Append("r.").Append(field.FieldName).Append(" = ref entity.Write(")
              .Append(field.DeclaringClassFullName).Append(".").Append(field.FieldName).AppendLine(");");
        }
        sb.Append(fieldIndent).AppendLine("return r;");
        sb.Append(memberIndent).AppendLine("}");
        sb.AppendLine();

        // ── SpawnBatch (SOA) ──
        sb.Append(memberIndent).AppendLine(
            "/// <summary>Spawn a batch of entities with per-entity component data. Source-generated SOA overload.</summary>");
        sb.Append(memberIndent).Append("public static global::Typhon.Engine.EntityId[] SpawnBatch(");
        sb.AppendLine();
        sb.Append(fieldIndent).Append("global::Typhon.Engine.Transaction tx");
        var paramNames = new string[model.AllCompFields.Length];
        for (int f = 0; f < model.AllCompFields.Length; f++)
        {
            var field = model.AllCompFields[f];
            paramNames[f] = char.ToLowerInvariant(field.FieldName[0]) + field.FieldName.Substring(1) + "s";
            sb.AppendLine(",");
            sb.Append(fieldIndent).Append("global::System.ReadOnlySpan<").Append(field.ComponentTypeFullName)
              .Append("> ").Append(paramNames[f]);
        }
        sb.AppendLine(")");
        sb.Append(memberIndent).AppendLine("{");

        // Count from first parameter
        sb.Append(fieldIndent).Append("int count = ").Append(paramNames[0]).AppendLine(".Length;");

        // Assert all spans same length
        for (int f = 1; f < model.AllCompFields.Length; f++)
        {
            sb.Append(fieldIndent).Append("global::System.Diagnostics.Debug.Assert(").Append(paramNames[f])
              .AppendLine(".Length == count, \"All component spans must have the same length\");");
        }

        // Allocate
        sb.Append(fieldIndent).AppendLine("var ids = new global::Typhon.Engine.EntityId[count];");
        sb.Append(fieldIndent).Append("int baseIndex = tx.SpawnBatchAllocate<")
          .Append(model.ClassName).AppendLine(">(count, ids);");

        // Write components — one call per component type, loop runs inside with zero dict lookups
        for (int f = 0; f < model.AllCompFields.Length; f++)
        {
            var field = model.AllCompFields[f];
            sb.Append(fieldIndent).Append("tx.SpawnBatchWriteAll(baseIndex, count, ")
              .Append(field.DeclaringClassFullName).Append(".").Append(field.FieldName)
              .Append(", ").Append(paramNames[f]).AppendLine(");");
        }

        sb.Append(fieldIndent).AppendLine("return ids;");
        sb.Append(memberIndent).AppendLine("}");

        // Close archetype class
        sb.Append(indent).AppendLine("}");

        // Close nesting parents
        for (int i = model.NestingParents.Length - 1; i >= 0; i--)
        {
            indent = indent.Substring(0, indent.Length - 4);
            sb.Append(indent).AppendLine("}");
        }

        if (hasNamespace)
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Models — immutable, equatable for incremental caching
// ═══════════════════════════════════════════════════════════════════════

internal sealed class CompFieldModel : IEquatable<CompFieldModel>
{
    public string FieldName { get; }
    public string ComponentTypeFullName { get; }
    public string DeclaringClassFullName { get; }

    public CompFieldModel(string fieldName, string componentTypeFullName, string declaringClassFullName)
    {
        FieldName = fieldName;
        ComponentTypeFullName = componentTypeFullName;
        DeclaringClassFullName = declaringClassFullName;
    }

    public bool Equals(CompFieldModel other)
    {
        if (other is null)
        {
            return false;
        }

        return FieldName == other.FieldName
            && ComponentTypeFullName == other.ComponentTypeFullName
            && DeclaringClassFullName == other.DeclaringClassFullName;
    }

    public override bool Equals(object obj) => obj is CompFieldModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (FieldName?.GetHashCode() ?? 0);
            hash = hash * 31 + (ComponentTypeFullName?.GetHashCode() ?? 0);
            hash = hash * 31 + (DeclaringClassFullName?.GetHashCode() ?? 0);
            return hash;
        }
    }
}

internal sealed class ArchetypeModel : IEquatable<ArchetypeModel>
{
    public string Namespace { get; }
    public string ClassName { get; }
    public string Accessibility { get; }
    public CompFieldModel[] AllCompFields { get; }
    public int InheritedCount { get; }
    public string[] NestingParents { get; }

    public ArchetypeModel(
        string ns,
        string className,
        string accessibility,
        CompFieldModel[] allCompFields,
        int inheritedCount,
        string[] nestingParents)
    {
        Namespace = ns;
        ClassName = className;
        Accessibility = accessibility;
        AllCompFields = allCompFields;
        InheritedCount = inheritedCount;
        NestingParents = nestingParents;
    }

    public bool Equals(ArchetypeModel other)
    {
        if (other is null)
        {
            return false;
        }

        if (Namespace != other.Namespace
            || ClassName != other.ClassName
            || Accessibility != other.Accessibility
            || InheritedCount != other.InheritedCount
            || AllCompFields.Length != other.AllCompFields.Length
            || NestingParents.Length != other.NestingParents.Length)
        {
            return false;
        }

        for (int i = 0; i < AllCompFields.Length; i++)
        {
            if (!AllCompFields[i].Equals(other.AllCompFields[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < NestingParents.Length; i++)
        {
            if (NestingParents[i] != other.NestingParents[i])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object obj) => obj is ArchetypeModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (Namespace?.GetHashCode() ?? 0);
            hash = hash * 31 + (ClassName?.GetHashCode() ?? 0);
            hash = hash * 31 + AllCompFields.Length;
            return hash;
        }
    }
}
