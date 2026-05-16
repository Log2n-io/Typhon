using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// Encourages callers to use <c>ClusterRef.WriteSpatial</c> instead of <c>GetSpan</c>/<c>Get</c> for components that contain a <c>[SpatialIndex]</c>-marked field.
/// The write barrier flags migration / AABB grow / AABB shrink at the write site so the engine fence loop can iterate only the clusters that actually changed —
/// without it, the fence has to fall back to a full scan over every active cluster (the regression that motivated the write-time bookkeeping refactor;
/// see <c>claude/design/spatial/write-time-spatial.md</c>).
///
/// <para>
/// Pattern detected (warning):
/// <code>
///   var bounds = cluster.GetSpan(Ant.Bounds);   // Ant.Bounds.T is WorldBounds, which has [SpatialIndex] on Bounds
///   bounds[idx].Bounds.MinX = newX;             // ← bypasses the barrier
/// </code>
/// Preferred:
/// <code>
///   cluster.WriteSpatial(Ant.Bounds, idx, new WorldBounds { Bounds = new AABB2F { MinX = newX, ... } });
/// </code>
/// </para>
///
/// <para>
/// V1 is intentionally conservative — it warns on ANY <c>GetSpan&lt;T&gt;</c>/<c>Get&lt;T&gt;</c> call where <c>T</c> contains a spatial-indexed field,
/// even read-only access. False-positive call sites that genuinely only READ should switch to <c>GetReadOnlySpan</c>/<c>GetReadOnly</c> to silence the warning.
/// Severity stays at <c>Warning</c> until all in-tree callers migrate; then it can be bumped to Error.
/// </para>
///
/// <list type="bullet">
///   <item><b>TYPHON009</b> — Mutable access to a spatial component via GetSpan/Get bypasses the WriteSpatial barrier.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class SpatialMutationWithoutBarrierAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON009";

    private const string SpatialIndexAttributeFqn = "Typhon.Schema.Definition.SpatialIndexAttribute";
    private const string ClusterRefTypeName = "ClusterRef";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        "Spatial component accessed via raw span — use WriteSpatial barrier instead",
        "Mutable access to spatial component '{0}' via {1}<T> bypasses the WriteSpatial barrier. " +
        "Use 'cluster.WriteSpatial({2}, slotIndex, newValue)' so the engine can flag migration / AABB updates inline. " +
        "If the access is read-only, use GetReadOnlySpan/GetReadOnly instead to silence this warning.",
        "Performance",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "ClusterRef.GetSpan<T> and Get<T> return mutable references into raw cluster memory and do NOT trigger " +
            "the engine's write-time spatial bookkeeping. Writing a spatial-indexed field through such a reference " +
            "leaves the per-cluster migration/shrink bitmaps untouched, forcing the fence-time spatial pass to fall " +
            "back to a full-scan over every active cluster. The WriteSpatial barrier replaces these write paths " +
            "with an inline detector that costs ~5ns per call and feeds the sparse fence loop.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Match `something.GetSpan(...)` or `something.Get(...)` only.
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }
        string methodName = memberAccess.Name.Identifier.Text;
        if (methodName != "GetSpan" && methodName != "Get")
        {
            return;
        }

        // Bind the invocation to confirm the receiver is ClusterRef<T> (the type we're guarding).
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        if (symbolInfo.Symbol is not IMethodSymbol method)
        {
            return;
        }
        var receiverType = method.ReceiverType;
        if (receiverType == null || receiverType.Name != ClusterRefTypeName)
        {
            return;
        }

        // Inspect the generic argument's element type. For `GetSpan<T>(Comp<T> comp)`, the method's
        // single type parameter is T. We resolve the inferred T from TypeArguments.
        if (method.TypeArguments.Length != 1)
        {
            return;
        }
        if (method.TypeArguments[0] is not INamedTypeSymbol componentType)
        {
            return;
        }

        // Does any field of the component type have [SpatialIndex]?
        if (!HasSpatialIndexField(componentType))
        {
            return;
        }

        // Build a friendly call-site replacement hint. We use the user-visible component name +
        // the invocation argument text (e.g., "Ant.Bounds") if present, otherwise just the type.
        string compArgText = invocation.ArgumentList.Arguments.Count > 0
            ? invocation.ArgumentList.Arguments[0].ToString()
            : componentType.Name;

        var diag = Diagnostic.Create(
            Rule,
            memberAccess.Name.GetLocation(),
            componentType.Name,
            methodName,
            compArgText);
        context.ReportDiagnostic(diag);
    }

    /// <summary>Recursion depth cap for the nested-struct scan — guards against pathological deeply-nested component graphs.</summary>
    private const int MaxNestedFieldDepth = 8;

    private static bool HasSpatialIndexField(INamedTypeSymbol type)
    {
        // Reuses a per-call visited set to break struct-field cycles (a struct can't contain itself by value in C#, but a generic instantiation graph can still
        // loop) and to avoid re-scanning a shared embedded struct multiple times.
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        return HasSpatialIndexField(type, visited, depth: 0);
    }

    private static bool HasSpatialIndexField(INamedTypeSymbol type, HashSet<INamedTypeSymbol> visited, int depth)
    {
        if (type == null || depth > MaxNestedFieldDepth || !visited.Add(type))
        {
            return false;
        }

        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol field || field.IsStatic) continue;

            // Direct hit: the field itself carries [SpatialIndex].
            foreach (var attr in field.GetAttributes())
            {
                var attrClass = attr.AttributeClass;
                if (attrClass == null) continue;
                if (attrClass.ToDisplayString() == SpatialIndexAttributeFqn) return true;
            }

            // Recurse into struct-typed fields: a component can embed a struct that carries the spatial-indexed field. Skip non-struct fields (classes,
            // primitives bottom out, enums, pointers) — only value-type aggregates can host a [SpatialIndex] member relevant to a blittable component.
            if (field.Type is INamedTypeSymbol fieldType
                && fieldType.IsValueType
                && fieldType.TypeKind == TypeKind.Struct
                && HasSpatialIndexField(fieldType, visited, depth + 1))
            {
                return true;
            }
        }
        return false;
    }
}
