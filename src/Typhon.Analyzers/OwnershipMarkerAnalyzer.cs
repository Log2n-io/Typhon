using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// TYPHON014 — a type the analyzer infers to own a resource, or to be unsafe to copy, must say so with a marker: ownership stated at the declaration,
/// and seen by every caller.
/// </summary>
/// <remarks>
/// <para>
/// TYPHON004 and TYPHON001/003 infer ownership from fields (<see cref="OwnershipCatalog"/>): a struct with a <c>Dispose</c> that holds a
/// <c>ChunkAccessor</c> by value owns its pages, and copying it copies the accessor. A project that references the engine compiles against its reference
/// assembly, which drops a class's private fields: an owning class is invisible there unless marked. And a type the analyzer has to work out is a type a
/// reader has to work out too — <c>ArchetypeAccessor&lt;T&gt;</c> owned three accessors without a word at its declaration, and the demo's undisposed
/// locals and by-value parameters held the checkpoint back for 13 hours (#1538).
/// </para>
/// <para>
/// So wherever the inference finds an owner in source it requires the marker: <c>[MustDispose]</c> on a type with a <c>Dispose</c> that owns a
/// must-dispose type by value, <c>[NoCopy]</c> on a struct that holds a no-copy struct by value. Private types are exempt — nothing outside their
/// assembly sees them, and inside it the inference already works.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class OwnershipMarkerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON014";

    private static readonly DiagnosticDescriptor MustDisposeRule = new DiagnosticDescriptor(
        DiagnosticId,
        "Type owning a must-dispose resource is not marked [MustDispose]",
        "'{0}' owns a {1} through '{2}' but is not marked [MustDispose]: state what the analyzer infers, so every caller is told to dispose it{3}",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Ownership inferred from fields is invisible at the declaration, and through a class to other assemblies: reference assemblies drop a " +
            "class's private fields. Mark the type [MustDispose] so TYPHON004 reaches every caller.");

    private static readonly DiagnosticDescriptor NoCopyRule = new DiagnosticDescriptor(
        DiagnosticId,
        "Struct holding a no-copy struct by value is not marked [NoCopy]",
        "'{0}' holds a {1} by value in '{2}' but is not marked [NoCopy]: copying it copies the {1}",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Copying the outer struct copies the inner one. Mark the struct [NoCopy] so the hazard is stated where it is declared.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(MustDisposeRule, NoCopyRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var catalog = new OwnershipCatalog(start.Compilation);
            start.RegisterSymbolAction(symbol => Analyze(symbol, catalog), SymbolKind.NamedType);
        });
    }

    private static void Analyze(SymbolAnalysisContext context, OwnershipCatalog catalog)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind is not (TypeKind.Struct or TypeKind.Class) || IsPrivate(type))
        {
            return;
        }

        var location = type.Locations.FirstOrDefault();
        if (location == null)
        {
            return;
        }

        if (catalog.HasDispose(type) && !OwnershipCatalog.TryGetMarker(type, "MustDisposeAttribute", out _))
        {
            var field = catalog.FieldOwningCritical(type, out var consequence);
            if (field != null)
            {
                context.ReportDiagnostic(Diagnostic.Create(MustDisposeRule, location, type.Name, field.Type.Name, field.Name, consequence));
            }
        }

        if (!OwnershipCatalog.TryGetMarker(type, "NoCopyAttribute", out _))
        {
            var field = catalog.FieldHoldingNoCopy(type);
            if (field != null)
            {
                context.ReportDiagnostic(Diagnostic.Create(NoCopyRule, location, type.Name, field.Type.Name, field.Name));
            }
        }
    }

    private static bool IsPrivate(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility is Accessibility.Private)
            {
                return true;
            }
        }

        return false;
    }
}
