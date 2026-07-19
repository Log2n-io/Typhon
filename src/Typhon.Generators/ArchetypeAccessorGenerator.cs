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
public partial class ArchetypeAccessorGenerator : IIncrementalGenerator
{
    private const string ArchetypeAttributeFqn = "Typhon.Schema.Definition.ArchetypeAttribute";
    private const string ComponentAttributeFqn = "Typhon.Schema.Definition.ComponentAttribute";
    private const string SchemaNs = "Typhon.Schema.Definition";

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

        // Per-assembly reflection-free component registration (feature #514, phase 5). Every [Component] in the compilation is collected and registered from a
        // single generated [ModuleInitializer] that runs once at assembly load — populating the engine's Type→ComponentSchemaSpec registry (and each
        // ComponentCollection<T> AOT-safe factory) BEFORE any DatabaseEngine reads it. This replaces the former IComponentSchemaProvider-on-struct dispatch, so
        // [Component] structs no longer need to be `partial`. A component the generated top-level registrar cannot reference (private/protected nesting) is
        // skipped and falls back to runtime reflection.
        var componentPipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: ComponentAttributeFqn,
            predicate: static (node, _) => node is StructDeclarationSyntax,
            transform: static (ctx, ct) => TransformComponent(ctx, ct)
        ).Where(static m => m != null).Collect();

        // (assembly name, whether Typhon.Engine is referenced by this compilation). The engine-typed ComponentCollection<T> factory can only be emitted when the
        // engine is reachable (directly or transitively); a schema-only assembly (references just Typhon.Schema.Definition) registers component specs via the
        // contract-level GeneratedSchemaRegistry and lets collection backing-stores fall back to the runtime reflective path.
        var asmInfo = context.CompilationProvider.Select(static (c, _) =>
            (Name: c.AssemblyName, HasEngine: c.GetTypeByMetadataName("Typhon.Engine.DatabaseEngine") != null));

        context.RegisterSourceOutput(componentPipeline.Combine(asmInfo), static (spc, pair) =>
        {
            var (models, info) = pair;
            if (models.IsDefaultOrEmpty)
            {
                return;
            }

            spc.AddSource("__TyphonRegistry.g.cs", EmitRegistrar(models, info.Name, info.HasEngine));
        });

        // Cascade-delete graph validation as a BUILD-TIME diagnostic (feature #514, phase 6). Mirrors the runtime ValidateCascadeDfs: a cycle or diamond in the
        // cascade graph visible WITHIN this compilation becomes a compile error (TPH1001/TPH1002) instead of a first-Open runtime throw. The runtime keeps its
        // build-once validation for the open-world/cross-assembly path where the full graph isn't visible to one compilation, so this is an additive early check.
        var cascadePipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: ArchetypeAttributeFqn,
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, ct) => TransformCascade(ctx, ct)
        ).Where(static m => m != null).Collect();

        context.RegisterSourceOutput(cascadePipeline, static (spc, models) => ValidateCascades(spc, models));
    }

    // ── Cascade-delete build-time diagnostics (#514 phase 6) ──
    private static readonly DiagnosticDescriptor CascadeCycleDescriptor = new(
        id: "TPH1001",
        title: "Cascade-delete cycle",
        messageFormat: "Cascade-delete cycle detected involving archetype '{0}'. Cycles in cascade graphs are forbidden.",
        category: "Typhon.Cascade",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CascadeDiamondDescriptor = new(
        id: "TPH1002",
        title: "Cascade-delete diamond",
        messageFormat: "Cascade-delete diamond detected: archetype '{0}' is reachable via multiple cascade paths. Diamond cascade graphs are forbidden.",
        category: "Typhon.Cascade",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

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
            // Lowercase-first + "s" can manufacture a C# keyword from a field name (e.g. field "A" → "as"); @-escape so the generated parameter stays valid.
            paramNames[f] = EscapeIdentifier(char.ToLowerInvariant(field.FieldName[0]) + field.FieldName.Substring(1) + "s");
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

    // ═══════════════════════════════════════════════════════════════════════
    // Component schema provider (feature #514, phase 4)
    // ═══════════════════════════════════════════════════════════════════════

    private static ComponentGenModel TransformComponent(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;

        // Registration now happens from a per-assembly [ModuleInitializer] (feature #514 phase 5), NOT via an interface on the struct — so `partial` is no
        // longer required. The one constraint: the generated top-level registrar class must be able to *reference* the component type, so a struct nested in a
        // private/protected scope can't be registered from there — skip it (the engine falls back to runtime reflection for it).
        if (!IsReachableFromModuleInit(symbol))
        {
            return null;
        }

        // [Component(name, revision, StorageMode=, DefaultDiscipline=)]
        var componentAttr = ctx.Attributes[0];
        if (componentAttr.ConstructorArguments.Length < 2)
        {
            return null;
        }

        if (!(componentAttr.ConstructorArguments[0].Value is string name))
        {
            return null;
        }

        int revision = componentAttr.ConstructorArguments[1].Value is int rev ? rev : 1;

        string storageModeCast = null;
        string disciplineCast = null;
        foreach (var na in componentAttr.NamedArguments)
        {
            if (na.Key == "StorageMode")
            {
                storageModeCast = EnumCast("StorageMode", na.Value);
            }
            else if (na.Key == "DefaultDiscipline")
            {
                disciplineCast = EnumCast("DurabilityDiscipline", na.Value);
            }
        }

        string structFqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var fields = new List<ComponentFieldGenModel>();
        var collectionElementFqns = new List<string>();   // element types T of ComponentCollection<T> fields → AOT-safe factory registration
        foreach (var member in symbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            // Mirror reflection's t.GetFields(): public, non-static instance fields only (const fields are static).
            if (!(member is IFieldSymbol field) || field.IsStatic || field.IsConst || field.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            string memberName = field.Name;
            string schemaName = memberName;
            string previousName = null;
            int? explicitFieldId = null;
            bool hasIndex = false;
            bool indexAllowMultiple = false;
            bool isForeignKey = false;
            string fkTargetFqn = null;
            bool hasSpatial = false;
            string spatialMargin = null;
            string spatialCellSize = null;
            string spatialModeCast = null;
            string spatialCategory = null;

            foreach (var ad in field.GetAttributes())
            {
                if (ad.AttributeClass == null || ad.AttributeClass.ContainingNamespace?.ToDisplayString() != SchemaNs)
                {
                    continue;
                }

                switch (ad.AttributeClass.Name)
                {
                    case "FieldAttribute":
                        foreach (var na in ad.NamedArguments)
                        {
                            if (na.Key == "Name" && na.Value.Value is string fn)
                            {
                                schemaName = fn;
                            }
                            else if (na.Key == "PreviousName" && na.Value.Value is string pn)
                            {
                                previousName = pn;
                            }
                            else if (na.Key == "FieldId" && na.Value.Value is int fid)
                            {
                                explicitFieldId = fid;
                            }
                        }
                        break;

                    case "IndexAttribute":
                        hasIndex = true;
                        foreach (var na in ad.NamedArguments)
                        {
                            if (na.Key == "AllowMultiple" && na.Value.Value is bool am)
                            {
                                indexAllowMultiple = am;
                            }
                        }
                        break;

                    case "ForeignKeyAttribute":
                        isForeignKey = true;
                        if (ad.ConstructorArguments.Length >= 1 && ad.ConstructorArguments[0].Value is INamedTypeSymbol fkt)
                        {
                            fkTargetFqn = fkt.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        }
                        break;

                    case "SpatialIndexAttribute":
                        hasSpatial = true;
                        if (ad.ConstructorArguments.Length >= 1)
                        {
                            spatialMargin = FloatLit(ad.ConstructorArguments[0].Value);
                        }
                        if (ad.ConstructorArguments.Length >= 2)
                        {
                            spatialCellSize = FloatLit(ad.ConstructorArguments[1].Value);
                        }
                        foreach (var na in ad.NamedArguments)
                        {
                            if (na.Key == "Mode")
                            {
                                spatialModeCast = EnumCast("SpatialMode", na.Value);
                            }
                            else if (na.Key == "Category")
                            {
                                spatialCategory = UIntLit(na.Value.Value);
                            }
                        }
                        break;
                }
            }

            // ComponentCollection<T> field → record its element type so the generated provider can register an AOT-safe backing-store factory (B2, #409).
            if (field.Type is INamedTypeSymbol collType
                && collType.Name == "ComponentCollection"
                && collType.TypeArguments.Length == 1
                && collType.ContainingNamespace?.ToDisplayString() == SchemaNs)
            {
                collectionElementFqns.Add(collType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }

            fields.Add(new ComponentFieldGenModel(
                memberName: memberName,
                schemaName: schemaName,
                fieldTypeFqn: field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                previousName: previousName,
                explicitFieldId: explicitFieldId,
                hasIndex: hasIndex,
                indexAllowMultiple: indexAllowMultiple,
                isForeignKey: isForeignKey,
                foreignKeyTargetFqn: fkTargetFqn,
                hasSpatialIndex: hasSpatial,
                spatialMargin: spatialMargin,
                spatialCellSize: spatialCellSize,
                spatialModeCast: spatialModeCast,
                spatialCategory: spatialCategory));
        }

        if (fields.Count == 0)
        {
            return null;
        }

        var containingNs = symbol.ContainingNamespace;
        string ns = (containingNs == null || containingNs.IsGlobalNamespace) ? "" : containingNs.ToDisplayString();

        var nestingParents = new List<string>();
        var containingType = symbol.ContainingType;
        while (containingType != null)
        {
            ct.ThrowIfCancellationRequested();
            string keyword = containingType.IsRecord ? "record" : (containingType.TypeKind == TypeKind.Struct ? "struct" : "class");
            nestingParents.Insert(0, $"{AccessibilityKeyword(containingType.DeclaredAccessibility)} partial {keyword} {containingType.Name}");
            containingType = containingType.ContainingType;
        }

        string hintName = structFqn
            .Replace("global::", "")
            .Replace('.', '_')
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace(',', '_')
            .Replace(" ", "");

        return new ComponentGenModel(
            ns: ns,
            structName: symbol.Name,
            structFqn: structFqn,
            accessibility: AccessibilityKeyword(symbol.DeclaredAccessibility),
            schemaName: name,
            revision: revision,
            storageModeCast: storageModeCast,
            disciplineCast: disciplineCast,
            fields: fields.ToArray(),
            nestingParents: nestingParents.ToArray(),
            hintName: hintName,
            collectionElementFqns: collectionElementFqns.ToArray());
    }

    private static string AccessibilityKeyword(Accessibility a) => a switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Private => "private",
        _ => "internal"
    };

    // Emit an enum value as a cast from its underlying integer — robust, no member-name lookup, deterministic output.
    private static string EnumCast(string enumName, TypedConstant tc)
    {
        long iv = tc.Value == null ? 0 : Convert.ToInt64(tc.Value, System.Globalization.CultureInfo.InvariantCulture);
        return $"(global::{SchemaNs}.{enumName}){iv.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string FloatLit(object v)
    {
        float f = v == null ? 0f : Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture);
        return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";
    }

    private static string UIntLit(object v)
    {
        uint u = v == null ? 0u : Convert.ToUInt32(v, System.Globalization.CultureInfo.InvariantCulture);
        return u.ToString(System.Globalization.CultureInfo.InvariantCulture) + "u";
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default",
        "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
        "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
        "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while"
    };

    // @-escape a generated identifier when it collides with a C# reserved keyword (@keyword is valid and denotes the same identifier).
    private static string EscapeIdentifier(string name) => CSharpKeywords.Contains(name) ? "@" + name : name;

    // Whether the generated top-level registrar class (Typhon.Generated.__TyphonRegistry_*) can reference this type: it and every containing type must be
    // public or internal. A struct nested in a private/protected scope is unreachable from a sibling top-level class, so we skip it (reflection fallback).
    private static bool IsReachableFromModuleInit(INamedTypeSymbol symbol)
    {
        for (INamedTypeSymbol t = symbol; t != null; t = t.ContainingType)
        {
            var a = t.DeclaredAccessibility;
            if (a != Accessibility.Public && a != Accessibility.Internal && a != Accessibility.NotApplicable)
            {
                return false;
            }
        }
        return true;
    }

    // Turn an assembly name into a valid C# identifier suffix for the per-assembly registrar class (keeps distinct assemblies' registrars distinctly named).
    private static string SanitizeIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "Assembly";
        }
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                chars[i] = '_';
            }
        }
        var result = new string(chars);
        if (!char.IsLetter(result[0]) && result[0] != '_')
        {
            result = "_" + result;
        }
        return result;
    }

    // Per-assembly reflection-free component registrar (feature #514, phase 5). One [ModuleInitializer] registers every [Component] in the assembly: its
    // ComponentSchemaSpec (built as pure data, offsets via a one-time Marshal.OffsetOf) into the schema-contract-level GeneratedSchemaRegistry, plus — when this
    // compilation references the engine — each ComponentCollection<T> AOT-safe factory. Runs once at assembly load, before any DatabaseEngine reads the registry
    // — which dissolves the flaky lazy-init race and lets [Component] structs drop the `partial` requirement (the schema no longer lives on the struct).
    //
    // The spec registration targets Typhon.Schema.Definition (which every schema assembly references) rather than the engine, so schema-only assemblies register
    // too. The collection factory is engine-typed, so it is emitted only when the engine is reachable; otherwise the backing store uses the runtime reflective
    // fallback for that element type.
    private static string EmitRegistrar(System.Collections.Immutable.ImmutableArray<ComponentGenModel> models, string assemblyName, bool hasEngine)
    {
        // Deterministic output: sort by fully-qualified struct name so the emitted registrar is byte-stable regardless of the collection order.
        var sorted = models.ToArray();
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.StructFqn, b.StructFqn));

        var sb = new StringBuilder(4096);

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("#pragma warning disable CA2255 // ModuleInitializer in a schema/app assembly is intended (feature #514)");
        sb.AppendLine("#pragma warning disable CS8019 // Unnecessary using directive");
        sb.AppendLine();
        sb.AppendLine("namespace Typhon.Generated");
        sb.AppendLine("{");
        sb.Append("    internal static class __TyphonRegistry_").AppendLine(SanitizeIdentifier(assemblyName));
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>Source-generated reflection-free component registration (feature #514). Runs once at assembly load.</summary>");
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("        {");

        foreach (var model in sorted)
        {
            // ComponentCollection<T> AOT-safe factories (B2, #409): T is a compile-time generic argument — no MakeGenericType/Activator. Idempotent. Only
            // emittable where the engine is referenced; a schema-only assembly falls back to the engine's runtime reflective path for these element types.
            if (hasEngine)
            {
                foreach (var elemFqn in model.CollectionElementFqns)
                {
                    sb.Append("            global::Typhon.Engine.DatabaseEngine.RegisterComponentCollectionFactory<").Append(elemFqn).AppendLine(">();");
                }
            }

            sb.Append("            global::Typhon.Schema.Definition.GeneratedSchemaRegistry.RegisterComponent(typeof(").Append(model.StructFqn).AppendLine("),");
            AppendComponentSpec(sb, model, "                ");
            sb.AppendLine("            );");
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    // Appends a `new global::Typhon.Schema.Definition.ComponentSchemaSpec(...)` expression (no trailing separator) at the given indent. Shared shape with the
    // engine's reflection path so a generated and a reflected component produce byte-identical definitions.
    private static void AppendComponentSpec(StringBuilder sb, ComponentGenModel model, string indent)
    {
        string fi = indent + "    ";       // spec-argument indent
        string ei = fi + "    ";           // field-element indent

        sb.Append(indent).Append("new global::").Append(SchemaNs).AppendLine(".ComponentSchemaSpec(");
        sb.Append(fi).Append(Quote(model.SchemaName)).AppendLine(",");
        sb.Append(fi).Append(model.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine(",");
        sb.Append(fi).Append("new global::").Append(SchemaNs).AppendLine(".ComponentFieldSpec[]");
        sb.Append(fi).AppendLine("{");

        foreach (var f in model.Fields)
        {
            sb.Append(ei).Append("new global::").Append(SchemaNs).Append(".ComponentFieldSpec(")
              .Append(Quote(f.SchemaName)).Append(", typeof(").Append(f.FieldTypeFqn).Append("), ")
              .Append("global::System.Runtime.InteropServices.Marshal.OffsetOf<").Append(model.StructFqn).Append(">(")
              .Append(Quote(f.MemberName)).Append(").ToInt32()");

            if (f.PreviousName != null)
            {
                sb.Append(", previousName: ").Append(Quote(f.PreviousName));
            }
            if (f.ExplicitFieldId.HasValue)
            {
                sb.Append(", explicitFieldId: ").Append(f.ExplicitFieldId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (f.HasIndex)
            {
                sb.Append(", hasIndex: true");
                if (f.IndexAllowMultiple)
                {
                    sb.Append(", indexAllowMultiple: true");
                }
            }
            if (f.IsForeignKey)
            {
                sb.Append(", isForeignKey: true");
                if (f.ForeignKeyTargetFqn != null)
                {
                    sb.Append(", foreignKeyTargetType: typeof(").Append(f.ForeignKeyTargetFqn).Append(")");
                }
            }
            if (f.HasSpatialIndex)
            {
                sb.Append(", hasSpatialIndex: true");
                if (f.SpatialMargin != null)
                {
                    sb.Append(", spatialMargin: ").Append(f.SpatialMargin);
                }
                if (f.SpatialCellSize != null)
                {
                    sb.Append(", spatialCellSize: ").Append(f.SpatialCellSize);
                }
                if (f.SpatialModeCast != null)
                {
                    sb.Append(", spatialMode: ").Append(f.SpatialModeCast);
                }
                if (f.SpatialCategory != null)
                {
                    sb.Append(", spatialCategory: ").Append(f.SpatialCategory);
                }
            }

            sb.AppendLine("),");
        }

        sb.Append(fi).Append("}");
        if (model.StorageModeCast != null)
        {
            sb.AppendLine().Append(fi).Append(", storageMode: ").Append(model.StorageModeCast);
        }
        if (model.DisciplineCast != null)
        {
            sb.AppendLine().Append(fi).Append(", defaultDiscipline: ").Append(model.DisciplineCast);
        }
        sb.AppendLine();
        sb.Append(indent).Append(")");
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Cascade-delete build-time validation (#514 phase 6) — mirrors runtime ArchetypeRegistry.ValidateCascadeDfs
// ═══════════════════════════════════════════════════════════════════════

public partial class ArchetypeAccessorGenerator
{
    private static CascadeArchModel TransformCascade(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var parents = new List<string>();
        CollectCascadeParents(symbol, parents, ct);
        // Emit every archetype as a node (even with no cascade FK) so ValidateCascades sees the full graph; empty ParentNames = no incoming cascade edges.
        return new CascadeArchModel(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), parents.ToArray());
    }

    // Collect the parent (cascade-source) archetypes for this archetype: for every own/inherited Comp<T> component that has an EntityLink<Parent> field marked
    // [Index(OnParentDelete != None)], the edge is Parent → thisArchetype (deleting Parent cascades to this child). Mirrors runtime BuildCascadeGraph.
    private static void CollectCascadeParents(INamedTypeSymbol archetypeType, List<string> parents, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var baseType = archetypeType.BaseType;
        if (baseType != null && baseType.IsGenericType && baseType.TypeArguments.Length == 2 && baseType.TypeArguments[1] is INamedTypeSymbol parentArch)
        {
            CollectCascadeParents(parentArch, parents, ct);
        }

        foreach (var member in archetypeType.GetMembers())
        {
            if (!(member is IFieldSymbol field) || !field.IsStatic || !field.IsReadOnly)
            {
                continue;
            }
            if (field.Type is INamedTypeSymbol ct2 && ct2.Name == "Comp" && ct2.TypeArguments.Length == 1 && ct2.TypeArguments[0] is INamedTypeSymbol compType)
            {
                ScanComponentForCascadeFk(compType, parents);
            }
        }
    }

    private static void ScanComponentForCascadeFk(INamedTypeSymbol compType, List<string> parents)
    {
        foreach (var member in compType.GetMembers())
        {
            if (!(member is IFieldSymbol field) || field.IsStatic)
            {
                continue;
            }
            if (!(field.Type is INamedTypeSymbol ft) || ft.Name != "EntityLink" || ft.TypeArguments.Length != 1)
            {
                continue;
            }

            bool cascade = false;
            foreach (var ad in field.GetAttributes())
            {
                if (ad.AttributeClass?.Name != "IndexAttribute")
                {
                    continue;
                }
                foreach (var na in ad.NamedArguments)
                {
                    if (na.Key == "OnParentDelete" && na.Value.Value is int action && action != 0)
                    {
                        cascade = true;
                    }
                }
            }

            if (cascade && ft.TypeArguments[0] is INamedTypeSymbol target)
            {
                parents.Add(target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }
    }

    private static void ValidateCascades(SourceProductionContext spc, System.Collections.Immutable.ImmutableArray<CascadeArchModel> models)
    {
        // Build parent→children adjacency from the collected (child, parents[]) edges; dedup edges so redundant FKs don't read as diamonds (only distinct paths do).
        var adjacency = new Dictionary<string, List<string>>();
        var nodes = new HashSet<string>();
        foreach (var m in models)
        {
            if (m == null)
            {
                continue;
            }
            nodes.Add(m.ChildName);
            foreach (var p in m.ParentNames)
            {
                nodes.Add(p);
                if (!adjacency.TryGetValue(p, out var list))
                {
                    list = new List<string>();
                    adjacency[p] = list;
                }
                if (!list.Contains(m.ChildName))
                {
                    list.Add(m.ChildName);
                }
            }
        }

        if (adjacency.Count == 0)
        {
            return; // no cascade edges to validate
        }

        foreach (var root in nodes)
        {
            var visited = new HashSet<string>();
            var inStack = new HashSet<string>();
            if (CascadeDfs(spc, root, adjacency, visited, inStack))
            {
                return; // report the first issue only, matching the runtime throw-on-first behavior
            }
        }
    }

    private static bool CascadeDfs(SourceProductionContext spc, string node, Dictionary<string, List<string>> adjacency, HashSet<string> visited, HashSet<string> inStack)
    {
        if (inStack.Contains(node))
        {
            spc.ReportDiagnostic(Diagnostic.Create(CascadeCycleDescriptor, Location.None, ShortName(node)));
            return true;
        }
        if (!visited.Add(node))
        {
            spc.ReportDiagnostic(Diagnostic.Create(CascadeDiamondDescriptor, Location.None, ShortName(node)));
            return true;
        }

        if (adjacency.TryGetValue(node, out var children))
        {
            inStack.Add(node);
            foreach (var child in children)
            {
                if (CascadeDfs(spc, child, adjacency, visited, inStack))
                {
                    return true;
                }
            }
            inStack.Remove(node);
        }

        return false;
    }

    private static string ShortName(string fqn)
    {
        int i = fqn.LastIndexOf('.');
        return i >= 0 ? fqn.Substring(i + 1) : fqn;
    }
}

internal sealed class CascadeArchModel : IEquatable<CascadeArchModel>
{
    public string ChildName { get; }
    public string[] ParentNames { get; }

    public CascadeArchModel(string childName, string[] parentNames)
    {
        ChildName = childName;
        ParentNames = parentNames;
    }

    public bool Equals(CascadeArchModel other)
    {
        if (other is null || ChildName != other.ChildName || ParentNames.Length != other.ParentNames.Length)
        {
            return false;
        }
        for (int i = 0; i < ParentNames.Length; i++)
        {
            if (ParentNames[i] != other.ParentNames[i])
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object obj) => obj is CascadeArchModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (ChildName?.GetHashCode() ?? 0);
            hash = hash * 31 + ParentNames.Length;
            return hash;
        }
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

internal sealed class ComponentFieldGenModel : IEquatable<ComponentFieldGenModel>
{
    public string MemberName { get; }
    public string SchemaName { get; }
    public string FieldTypeFqn { get; }
    public string PreviousName { get; }
    public int? ExplicitFieldId { get; }
    public bool HasIndex { get; }
    public bool IndexAllowMultiple { get; }
    public bool IsForeignKey { get; }
    public string ForeignKeyTargetFqn { get; }
    public bool HasSpatialIndex { get; }
    public string SpatialMargin { get; }
    public string SpatialCellSize { get; }
    public string SpatialModeCast { get; }
    public string SpatialCategory { get; }

    public ComponentFieldGenModel(string memberName, string schemaName, string fieldTypeFqn, string previousName, int? explicitFieldId,
        bool hasIndex, bool indexAllowMultiple, bool isForeignKey, string foreignKeyTargetFqn, bool hasSpatialIndex,
        string spatialMargin, string spatialCellSize, string spatialModeCast, string spatialCategory)
    {
        MemberName = memberName;
        SchemaName = schemaName;
        FieldTypeFqn = fieldTypeFqn;
        PreviousName = previousName;
        ExplicitFieldId = explicitFieldId;
        HasIndex = hasIndex;
        IndexAllowMultiple = indexAllowMultiple;
        IsForeignKey = isForeignKey;
        ForeignKeyTargetFqn = foreignKeyTargetFqn;
        HasSpatialIndex = hasSpatialIndex;
        SpatialMargin = spatialMargin;
        SpatialCellSize = spatialCellSize;
        SpatialModeCast = spatialModeCast;
        SpatialCategory = spatialCategory;
    }

    public bool Equals(ComponentFieldGenModel other)
    {
        if (other is null)
        {
            return false;
        }

        return MemberName == other.MemberName
            && SchemaName == other.SchemaName
            && FieldTypeFqn == other.FieldTypeFqn
            && PreviousName == other.PreviousName
            && ExplicitFieldId == other.ExplicitFieldId
            && HasIndex == other.HasIndex
            && IndexAllowMultiple == other.IndexAllowMultiple
            && IsForeignKey == other.IsForeignKey
            && ForeignKeyTargetFqn == other.ForeignKeyTargetFqn
            && HasSpatialIndex == other.HasSpatialIndex
            && SpatialMargin == other.SpatialMargin
            && SpatialCellSize == other.SpatialCellSize
            && SpatialModeCast == other.SpatialModeCast
            && SpatialCategory == other.SpatialCategory;
    }

    public override bool Equals(object obj) => obj is ComponentFieldGenModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (MemberName?.GetHashCode() ?? 0);
            hash = hash * 31 + (SchemaName?.GetHashCode() ?? 0);
            hash = hash * 31 + (FieldTypeFqn?.GetHashCode() ?? 0);
            hash = hash * 31 + (PreviousName?.GetHashCode() ?? 0);
            hash = hash * 31 + (ExplicitFieldId?.GetHashCode() ?? 0);
            return hash;
        }
    }
}

internal sealed class ComponentGenModel : IEquatable<ComponentGenModel>
{
    public string Namespace { get; }
    public string StructName { get; }
    public string StructFqn { get; }
    public string Accessibility { get; }
    public string SchemaName { get; }
    public int Revision { get; }
    public string StorageModeCast { get; }
    public string DisciplineCast { get; }
    public ComponentFieldGenModel[] Fields { get; }
    public string[] NestingParents { get; }
    public string HintName { get; }
    public string[] CollectionElementFqns { get; }

    public ComponentGenModel(string ns, string structName, string structFqn, string accessibility, string schemaName, int revision,
        string storageModeCast, string disciplineCast, ComponentFieldGenModel[] fields, string[] nestingParents, string hintName,
        string[] collectionElementFqns)
    {
        Namespace = ns;
        StructName = structName;
        StructFqn = structFqn;
        Accessibility = accessibility;
        SchemaName = schemaName;
        Revision = revision;
        StorageModeCast = storageModeCast;
        DisciplineCast = disciplineCast;
        Fields = fields;
        NestingParents = nestingParents;
        HintName = hintName;
        CollectionElementFqns = collectionElementFqns;
    }

    public bool Equals(ComponentGenModel other)
    {
        if (other is null)
        {
            return false;
        }

        if (Namespace != other.Namespace
            || StructName != other.StructName
            || StructFqn != other.StructFqn
            || Accessibility != other.Accessibility
            || SchemaName != other.SchemaName
            || Revision != other.Revision
            || StorageModeCast != other.StorageModeCast
            || DisciplineCast != other.DisciplineCast
            || HintName != other.HintName
            || Fields.Length != other.Fields.Length
            || NestingParents.Length != other.NestingParents.Length
            || CollectionElementFqns.Length != other.CollectionElementFqns.Length)
        {
            return false;
        }

        for (int i = 0; i < CollectionElementFqns.Length; i++)
        {
            if (CollectionElementFqns[i] != other.CollectionElementFqns[i])
            {
                return false;
            }
        }

        for (int i = 0; i < Fields.Length; i++)
        {
            if (!Fields[i].Equals(other.Fields[i]))
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

    public override bool Equals(object obj) => obj is ComponentGenModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (StructFqn?.GetHashCode() ?? 0);
            hash = hash * 31 + (SchemaName?.GetHashCode() ?? 0);
            hash = hash * 31 + Revision;
            hash = hash * 31 + Fields.Length;
            return hash;
        }
    }
}
