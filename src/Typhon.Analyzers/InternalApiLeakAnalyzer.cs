using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// Enforces the namespace boundary established in <c>claude/research/PublicVsInternalApiClassification.md</c>:
/// types in the public namespace <c>Typhon.Engine</c> must not expose types from
/// <c>Typhon.Engine.Internals</c> on their public/protected surface.
/// <para>
/// Pre-migration the analyzer is a no-op (no type lives in <c>Typhon.Engine.Internals</c> yet).
/// Once the big-bang migration lands, accidental drift — e.g. a public method whose return type
/// or parameter slips an internal type out — fails the build.
/// </para>
/// <list type="bullet">
///   <item><b>TYPHON008</b> — Public-namespace type exposes internal-namespace type on its public surface.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class InternalApiLeakAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON008";

    private const string PublicNamespace = "Typhon.Engine";
    private const string InternalNamespacePrefix = "Typhon.Engine.Internals";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        "Public API leaks internal type",
        "Public type '{0}' exposes internal type '{1}' via {2}. Either promote the internal type, or hide '{0}' behind a public-only abstraction.",
        "Design",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Types in namespace 'Typhon.Engine' form the public consumer surface. " +
            "Types in 'Typhon.Engine.Internals' are implementation details exposed only to friend assemblies via InternalsVisibleTo. " +
            "A public type may use internal types in its implementation, but it must NOT mention them on its public/protected surface — " +
            "doing so re-exports the internal type to consumers, defeating the namespace split.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        // Only types externally visible (public, or protected nested under a public outer) need checking.
        if (!IsExternallyVisible(type))
        {
            return;
        }

        // Only types in the public namespace are subject to the rule.
        if (!IsInPublicNamespace(type))
        {
            return;
        }

        // Generated code from source generators (TraceEventGenerator, ArchetypeAccessorGenerator, etc.) is excluded.
        if (IsGeneratedSymbol(type))
        {
            return;
        }

        // Base type and interfaces — re-export anything they reach.
        if (type.BaseType is { } baseType && !IsObjectOrValueType(baseType))
        {
            ReportLeaks(context, type, baseType, "base type");
        }

        foreach (var iface in type.Interfaces)
        {
            ReportLeaks(context, type, iface, "implemented interface");
        }

        foreach (var member in type.GetMembers())
        {
            if (!IsExternallyVisible(member))
            {
                continue;
            }

            if (IsGeneratedSymbol(member))
            {
                continue;
            }

            switch (member)
            {
                case IFieldSymbol field:
                    // Compiler-generated backing fields would be filtered by IsGeneratedSymbol; const enum members are noise.
                    if (field.IsImplicitlyDeclared || field.ContainingType.TypeKind == TypeKind.Enum)
                    {
                        continue;
                    }
                    ReportLeaks(context, type, field.Type, $"field '{field.Name}'");
                    break;

                case IPropertySymbol property:
                    ReportLeaks(context, type, property.Type, $"property '{property.Name}'");
                    foreach (var p in property.Parameters)
                    {
                        ReportLeaks(context, type, p.Type, $"indexer parameter '{p.Name}' on '{property.Name}'");
                    }
                    break;

                case IEventSymbol evt:
                    ReportLeaks(context, type, evt.Type, $"event '{evt.Name}'");
                    break;

                case IMethodSymbol method:
                    if (ShouldSkipMethod(method))
                    {
                        continue;
                    }
                    if (!method.ReturnsVoid)
                    {
                        ReportLeaks(context, type, method.ReturnType, $"return type of '{method.Name}'");
                    }
                    foreach (var p in method.Parameters)
                    {
                        ReportLeaks(context, type, p.Type, $"parameter '{p.Name}' of '{method.Name}'");
                    }
                    foreach (var tp in method.TypeParameters)
                    {
                        foreach (var constraint in tp.ConstraintTypes)
                        {
                            ReportLeaks(context, type, constraint, $"generic constraint on '{method.Name}<{tp.Name}>'");
                        }
                    }
                    break;
            }
        }

        foreach (var tp in type.TypeParameters)
        {
            foreach (var constraint in tp.ConstraintTypes)
            {
                ReportLeaks(context, type, constraint, $"generic constraint on '{type.Name}<{tp.Name}>'");
            }
        }
    }

    private static bool ShouldSkipMethod(IMethodSymbol method)
    {
        // Property/event accessors are visited via their owning member.
        if (method.AssociatedSymbol is IPropertySymbol or IEventSymbol)
        {
            return true;
        }

        // Static constructors are not part of the public surface.
        if (method.MethodKind == MethodKind.StaticConstructor)
        {
            return true;
        }

        // Compiler-generated record helpers (Equals/GetHashCode etc.) are noise.
        return method.IsImplicitlyDeclared;
    }

    private static void ReportLeaks(SymbolAnalysisContext context, INamedTypeSymbol publicType, ITypeSymbol referencedType, string surface)
    {
        foreach (var leaked in EnumerateInternalNamedTypes(referencedType))
        {
            var location = publicType.Locations.Length > 0 ? publicType.Locations[0] : Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                location,
                publicType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                leaked.ToDisplayString(),
                surface));
        }
    }

    /// <summary>
    /// Yields every leaked internal-namespace named type reachable from <paramref name="type"/>: the type itself if it qualifies,
    /// then recursively type arguments, array element types, and pointer element types.
    /// <para>
    /// A type qualifies as "leaked" only if it is BOTH (a) declared inside <c>Typhon.Engine.Internals</c> (or a sub-namespace) AND
    /// (b) has internal accessibility. The dual condition lets the analyzer stay dormant during a namespace-only migration
    /// (when types are temporarily moved into the internal namespace but still public-accessibility) and only fires once the
    /// accessibility pass also completes — at which point it becomes the standing guard against drift.
    /// </para>
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> EnumerateInternalNamedTypes(ITypeSymbol type)
    {
        if (type == null)
        {
            yield break;
        }

        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var inner in EnumerateInternalNamedTypes(array.ElementType))
                {
                    yield return inner;
                }
                break;

            case IPointerTypeSymbol pointer:
                foreach (var inner in EnumerateInternalNamedTypes(pointer.PointedAtType))
                {
                    yield return inner;
                }
                break;

            case INamedTypeSymbol named:
                if (IsInInternalNamespace(named) && IsAssemblyInternal(named))
                {
                    yield return named;
                }
                foreach (var arg in named.TypeArguments)
                {
                    foreach (var inner in EnumerateInternalNamedTypes(arg))
                    {
                        yield return inner;
                    }
                }
                break;
        }
    }

    private static bool IsAssemblyInternal(INamedTypeSymbol type)
    {
        // Walk outward through nested types. The effective accessibility is the most restrictive of the chain.
        // A type is "assembly-internal" if anywhere in the chain we see Internal, ProtectedAndInternal, or Private.
        // Public/Protected/ProtectedOrInternal are visible to consumers (Protected is reachable via subclass), so
        // they don't qualify as a leak target.
        for (var current = type; current != null; current = current.ContainingType)
        {
            switch (current.DeclaredAccessibility)
            {
                case Accessibility.Internal:
                case Accessibility.ProtectedAndInternal:
                case Accessibility.Private:
                    return true;
            }
        }

        return false;
    }

    private static bool IsExternallyVisible(ISymbol symbol)
    {
        for (var current = symbol; current != null; current = current.ContainingType)
        {
            switch (current.DeclaredAccessibility)
            {
                case Accessibility.Public:
                case Accessibility.Protected:
                case Accessibility.ProtectedOrInternal:
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool IsInPublicNamespace(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        return ns == PublicNamespace;
    }

    private static bool IsInInternalNamespace(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (ns == null)
        {
            return false;
        }

        return ns == InternalNamespacePrefix
            || ns.StartsWith(InternalNamespacePrefix + ".", System.StringComparison.Ordinal);
    }

    private static bool IsObjectOrValueType(INamedTypeSymbol type)
    {
        var name = type.ToDisplayString();
        return name == "object" || name == "System.Object" || name == "System.ValueType" || name == "System.Enum";
    }

    private static bool IsGeneratedSymbol(ISymbol symbol)
    {
        // Filter both compiler-generated members and source-generator-emitted code (the latter typically lacks a
        // syntax tree path the user maintains; we still want to skip it because the producer of the leak is the
        // generator, not the developer).
        if (symbol.IsImplicitlyDeclared)
        {
            return true;
        }

        foreach (var attr in symbol.GetAttributes())
        {
            var name = attr.AttributeClass?.Name;
            if (name == "GeneratedCodeAttribute" || name == "CompilerGeneratedAttribute")
            {
                return true;
            }
        }

        foreach (var location in symbol.Locations)
        {
            var path = location.SourceTree?.FilePath;
            if (path == null)
            {
                continue;
            }
            if (path.IndexOf(".g.cs", System.StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf(".generated.cs", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
