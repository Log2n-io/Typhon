using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// Unified analyzer for critical disposable field management in types.
/// Performs a single pass over each named type and emits up to three diagnostics:
///
/// <list type="bullet">
///   <item><b>TYPHON005</b> — Type containing critical disposable fields must implement IDisposable</item>
///   <item><b>TYPHON006</b> — Dispose() must dispose ALL critical fields</item>
///   <item><b>TYPHON007</b> — Early returns in Dispose() must not skip critical field disposal</item>
/// </list>
///
/// The checks form a natural chain: if a type doesn't implement IDisposable (005),
/// there's no Dispose() to check for completeness (006) or early-return safety (007).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class CriticalFieldDisposalAnalyzer : DiagnosticAnalyzer
{
    // === TYPHON005: Container doesn't implement IDisposable ===

    public const string ContainerDiagnosticId = "TYPHON005";

    private static readonly DiagnosticDescriptor ContainerRule = new DiagnosticDescriptor(
        ContainerDiagnosticId,
        "Type with disposable field has no Dispose()",
        "Type '{0}' contains field '{1}' of disposable type '{2}' but has no Dispose(){3}",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Types that contain fields of critical types ([MustDispose], or owning one by value) must have a Dispose() to release them: " +
            "IDisposable, or for a ref struct the pattern method.");

    // === TYPHON006: Dispose() incomplete ===

    public const string CompletenessDiagnosticId = "TYPHON006";

    private static readonly DiagnosticDescriptor CompletenessRule = new DiagnosticDescriptor(
        CompletenessDiagnosticId,
        "Dispose() does not dispose all critical fields",
        "Dispose() method in '{0}' does not dispose field '{1}' of type '{2}'{3}",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Dispose() methods must dispose ALL fields of critical IDisposable types (ChunkAccessor, Transaction).");

    // === TYPHON007: Early return skips disposal ===

    public const string EarlyReturnDiagnosticId = "TYPHON007";

    private static readonly DiagnosticDescriptor EarlyReturnRule = new DiagnosticDescriptor(
        EarlyReturnDiagnosticId,
        "Early return in Dispose() skips disposal of critical field",
        "Early return in Dispose() of '{0}' may skip disposal of field '{1}'{2}",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Early return statements in Dispose() methods must not skip disposal of critical fields.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(ContainerRule, CompletenessRule, EarlyReturnRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var catalog = new OwnershipCatalog(start.Compilation);
            start.RegisterSymbolAction(symbol => AnalyzeNamedType(symbol, catalog), SymbolKind.NamedType);
        });
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, OwnershipCatalog catalog)
    {
        var namedType = (INamedTypeSymbol)context.Symbol;

        if (namedType.TypeKind != TypeKind.Class && namedType.TypeKind != TypeKind.Struct)
            return;

        // Shared first step: find critical fields once
        var criticalFields = DisposableAnalyzerHelpers.FindCriticalFields(namedType, catalog);
        if (criticalFields.Count == 0)
            return;

        // Branch: does the type have a Dispose (IDisposable, or a ref struct's pattern method)?
        if (!catalog.HasDispose(namedType))
        {
            // TYPHON005 path
            CheckContainerImplementsDisposable(context, namedType, criticalFields, catalog);
            return;
        }

        // Type implements IDisposable — check the Dispose() body
        var disposeMethod = FindDisposeMethod(namedType);
        if (disposeMethod == null)
            return;

        var disposeBody = ExpandWithOwnCalls(namedType, GetMethodBody(disposeMethod), 2);
        if (string.IsNullOrEmpty(disposeBody))
            return;

        // TYPHON006 path
        CheckDisposeCompleteness(context, namedType, disposeMethod, disposeBody, criticalFields);

        // TYPHON007 path
        CheckDisposeEarlyReturns(context, namedType, disposeMethod, criticalFields);
    }

    // ──────────────────────────────────────────────
    //  TYPHON005: Container must implement IDisposable
    // ──────────────────────────────────────────────

    private static void CheckContainerImplementsDisposable(
        SymbolAnalysisContext context,
        INamedTypeSymbol namedType,
        List<(IFieldSymbol Field, string TypeFullName, string Consequence)> criticalFields,
        OwnershipCatalog catalog)
    {
        // A ref struct is not exempt: it cannot implement IDisposable, but 'using' takes its pattern Dispose(), and without one nothing can release what
        // it owns — the shape of the ArchetypeAccessor leak (#1538), had it lacked the method instead of its callers lacking the call.

        // Skip inline arrays — compiler-generated, disposal managed by containing struct
        if (IsInlineArray(namedType))
            return;

        // Skip types nested inside an IDisposable container — parent handles disposal
        if (IsNestedInsideDisposableType(namedType, catalog))
            return;

        // Skip types that have an explicit disposal method (e.g., DisposeAccessors())
        if (HasMethodDisposingFields(namedType, criticalFields))
            return;

        foreach (var (field, _, consequence) in criticalFields)
        {
            var location = field.Locations.FirstOrDefault() ?? namedType.Locations.FirstOrDefault();
            if (location == null)
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                ContainerRule, location,
                namedType.Name, field.Name, field.Type.Name, consequence));
        }
    }

    // ──────────────────────────────────────────────
    //  TYPHON006: Dispose() must dispose all critical fields
    // ──────────────────────────────────────────────

    private static void CheckDisposeCompleteness(
        SymbolAnalysisContext context,
        INamedTypeSymbol namedType,
        IMethodSymbol disposeMethod,
        string disposeBody,
        List<(IFieldSymbol Field, string TypeFullName, string Consequence)> criticalFields)
    {
        foreach (var (field, _, consequence) in criticalFields)
        {
            if (!IsFieldDisposedInBody(field, disposeBody))
            {
                var location = disposeMethod.Locations.FirstOrDefault() ?? namedType.Locations.FirstOrDefault();
                if (location == null)
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    CompletenessRule, location,
                    namedType.Name, field.Name, field.Type.Name, consequence));
            }
        }
    }

    // ──────────────────────────────────────────────
    //  TYPHON007: Early returns must not skip disposal
    // ──────────────────────────────────────────────

    private static void CheckDisposeEarlyReturns(
        SymbolAnalysisContext context,
        INamedTypeSymbol namedType,
        IMethodSymbol disposeMethod,
        List<(IFieldSymbol Field, string TypeFullName, string Consequence)> criticalFields)
    {
        MethodDeclarationSyntax methodSyntax = null;
        foreach (var syntaxRef in disposeMethod.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is MethodDeclarationSyntax mds)
            {
                methodSyntax = mds;
                break;
            }
        }

        var body = methodSyntax?.Body;
        if (body == null)
            return;

        var returnStatements = body.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
        if (returnStatements.Count == 0)
            return;

        foreach (var returnStatement in returnStatements)
        {
            if (IsLastStatementInMethod(returnStatement, body))
                continue;

            // The idempotency guard — `if (_disposed) return;` — returns because the disposal already ran
            if (IsDisposedGuard(returnStatement))
                continue;

            var disposedBeforeReturn = GetFieldsDisposedBefore(returnStatement, body, criticalFields);

            foreach (var (field, _, consequence) in criticalFields)
            {
                if (!disposedBeforeReturn.Contains(field.Name))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        EarlyReturnRule, returnStatement.GetLocation(),
                        namedType.Name, field.Name, consequence));
                }
            }
        }
    }

    // ──────────────────────────────────────────────
    //  Shared helpers
    // ──────────────────────────────────────────────

    private static IMethodSymbol FindDisposeMethod(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is IMethodSymbol method &&
                method.Name == "Dispose" &&
                method.Parameters.Length == 0 &&
                !method.IsAbstract)
            {
                return method;
            }
        }

        // Explicit interface implementation
        foreach (var member in type.GetMembers())
        {
            if (member is IMethodSymbol method &&
                method.MethodKind == MethodKind.ExplicitInterfaceImplementation &&
                method.Name.EndsWith(".Dispose") &&
                method.Parameters.Length == 0)
            {
                return method;
            }
        }

        return null;
    }

    private static string GetMethodBody(IMethodSymbol method)
    {
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is MethodDeclarationSyntax methodSyntax)
            {
                return methodSyntax.Body?.ToString() ?? methodSyntax.ExpressionBody?.ToString() ?? "";
            }
        }

        return "";
    }

    /// <summary>
    /// <paramref name="body"/> followed by the bodies of the methods of <paramref name="type"/> it calls, <paramref name="depth"/> levels deep: a
    /// <c>Dispose() => Close()</c> disposes what <c>Close</c> does.
    /// </summary>
    private static string ExpandWithOwnCalls(INamedTypeSymbol type, string body, int depth)
    {
        if (depth == 0 || string.IsNullOrEmpty(body))
            return body;

        var expanded = new System.Text.StringBuilder(body);
        foreach (var member in type.GetMembers())
        {
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, Name: not "Dispose" } method &&
                System.Text.RegularExpressions.Regex.IsMatch(body, @"(?<![\w.])" + method.Name + @"\s*\("))
            {
                expanded.Append('\n').Append(ExpandWithOwnCalls(type, GetMethodBody(method), depth - 1));
            }
        }

        return expanded.ToString();
    }

    // --- TYPHON005 helpers ---

    private static bool IsInlineArray(INamedTypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name;
            if (attrName == "InlineArrayAttribute" || attrName == "InlineArray")
                return true;
        }

        return false;
    }

    private static bool IsNestedInsideDisposableType(INamedTypeSymbol type, OwnershipCatalog catalog)
    {
        var containingType = type.ContainingType;
        while (containingType != null)
        {
            if (catalog.HasDispose(containingType))
                return true;
            containingType = containingType.ContainingType;
        }

        return false;
    }

    private static bool HasMethodDisposingFields(
        INamedTypeSymbol type,
        List<(IFieldSymbol Field, string TypeFullName, string Consequence)> criticalFields)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method)
                continue;

            if (method.MethodKind != MethodKind.Ordinary)
                continue;

            var methodName = method.Name;
            if (!methodName.Contains("Dispose") &&
                !methodName.Contains("Cleanup") &&
                !methodName.Contains("Release") &&
                !methodName.Contains("Close"))
            {
                continue;
            }

            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax() is not MethodDeclarationSyntax methodSyntax)
                    continue;

                var body = methodSyntax.Body?.ToString() ?? methodSyntax.ExpressionBody?.ToString() ?? "";
                var disposedCount = 0;
                foreach (var (field, _, _) in criticalFields)
                {
                    if (body.Contains($"{field.Name}.Dispose()"))
                        disposedCount++;
                }

                if (disposedCount == criticalFields.Count)
                    return true;
            }
        }

        return false;
    }

    // --- TYPHON006 helpers ---

    /// <summary>
    /// Checks if a field is disposed in the method body via various patterns:
    /// direct call, null-conditional, via-local assignment, foreach iteration, or Dictionary.Values.
    /// </summary>
    private static bool IsFieldDisposedInBody(IFieldSymbol field, string methodBody)
    {
        var fieldName = field.Name;

        // Direct: _field.Dispose()
        if (methodBody.Contains($"{fieldName}.Dispose()"))
            return true;

        // Null-conditional: _field?.Dispose()
        if (methodBody.Contains($"{fieldName}?.Dispose()"))
            return true;

        // Via this: this._field.Dispose()
        if (methodBody.Contains($"this.{fieldName}.Dispose()"))
            return true;

        // Assigned to local then disposed (conservative — assume disposal happens)
        if (methodBody.Contains($"= {fieldName};") || methodBody.Contains($"= {fieldName},"))
            return true;

        // Iterated via foreach
        if (methodBody.Contains($"in {fieldName}") && methodBody.Contains(".Dispose()"))
            return true;

        // Dictionary.Values pattern
        if (methodBody.Contains($"{fieldName}.Values") && methodBody.Contains(".Dispose()"))
            return true;

        return false;
    }

    // --- TYPHON007 helpers ---

    private static bool IsLastStatementInMethod(ReturnStatementSyntax returnStatement, BlockSyntax methodBody)
    {
        var lastStatement = methodBody.Statements.LastOrDefault();
        if (lastStatement == null)
            return false;

        if (lastStatement == returnStatement)
            return true;

        // Check if return is inside an if/else at the end that covers all paths
        var returnParent = returnStatement.Parent;
        while (returnParent != null && returnParent != methodBody)
        {
            if (returnParent == lastStatement)
                return true;
            returnParent = returnParent.Parent;
        }

        return false;
    }

    private static bool IsDisposedGuard(ReturnStatementSyntax returnStatement)
    {
        var parent = returnStatement.Parent is BlockSyntax { Statements.Count: 1 } block ? block.Parent : returnStatement.Parent;
        return parent is IfStatementSyntax ifStatement &&
               ifStatement.Condition.ToString().IndexOf("dispos", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static HashSet<string> GetFieldsDisposedBefore(
        ReturnStatementSyntax returnStatement,
        BlockSyntax methodBody,
        List<(IFieldSymbol Field, string TypeFullName, string Consequence)> criticalFields)
    {
        var disposedFields = new HashSet<string>();

        var containingBlock = returnStatement.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        if (containingBlock == null)
            return disposedFields;

        foreach (var (field, _, _) in criticalFields)
        {
            if (IsFieldDisposedBeforeReturn(field.Name, returnStatement, containingBlock, methodBody))
            {
                disposedFields.Add(field.Name);
            }
        }

        return disposedFields;
    }

    private static bool IsFieldDisposedBeforeReturn(
        string fieldName,
        ReturnStatementSyntax returnStatement,
        BlockSyntax containingBlock,
        BlockSyntax methodBody)
    {
        // Check statements before the return in the same block
        foreach (var statement in containingBlock.Statements)
        {
            if (statement.SpanStart >= returnStatement.SpanStart)
                break;

            var statementText = statement.ToString();
            if (statementText.Contains($"{fieldName}.Dispose()") ||
                statementText.Contains($"{fieldName}?.Dispose()"))
            {
                return true;
            }
        }

        // Also check parent blocks if we're nested (e.g., inside an if)
        if (containingBlock != methodBody)
        {
            var parentBlock = containingBlock.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
            if (parentBlock != null)
            {
                var containingStatement = containingBlock.Ancestors().OfType<StatementSyntax>()
                    .FirstOrDefault(s => s.Parent == parentBlock);

                if (containingStatement != null)
                {
                    foreach (var statement in parentBlock.Statements)
                    {
                        if (statement.SpanStart >= containingStatement.SpanStart)
                            break;

                        var statementText = statement.ToString();
                        if (statementText.Contains($"{fieldName}.Dispose()") ||
                            statementText.Contains($"{fieldName}?.Dispose()"))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }
}
