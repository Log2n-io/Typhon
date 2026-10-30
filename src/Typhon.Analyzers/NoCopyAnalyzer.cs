using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Typhon.Analyzers;

/// <summary>
/// Unified analyzer that protects no-copy types from value copies: marked <c>[NoCopy]</c>, or holding one by value at any depth — copying the outer
/// copies the inner (<see cref="OwnershipCatalog"/>).
/// <list type="bullet">
///   <item><b>TYPHON001</b> — Parameters must use the <c>ref</c> modifier (not <c>in</c> or by value).</item>
///   <item><b>TYPHON003</b> — Assignments, variable declarations, and returns must not create copies.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class NoCopyAnalyzer : DiagnosticAnalyzer
{
    // ═══════════════════════════════════════════════════════════════════════
    // TYPHON001 — ref parameter enforcement
    // ═══════════════════════════════════════════════════════════════════════

    public const string RefDiagnosticId = "TYPHON001";

    private static readonly DiagnosticDescriptor RefRule = new DiagnosticDescriptor(
        RefDiagnosticId,
        "[NoCopy] type must be passed by ref",
        "Parameter '{0}' of type '{1}' must be passed by ref (not 'in' or by value){2}",
        "Performance",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Types marked with [NoCopy] are large structs with mutating methods. " +
            "They must be passed by 'ref' only. The 'in' modifier causes defensive copies when calling " +
            "non-readonly methods, defeating the performance design. " +
            "Passing by value creates expensive stack copies.");

    // ═══════════════════════════════════════════════════════════════════════
    // TYPHON003 — no-copy enforcement
    // ═══════════════════════════════════════════════════════════════════════

    public const string CopyDiagnosticId = "TYPHON003";

    private static readonly DiagnosticDescriptor CopyRule = new DiagnosticDescriptor(
        CopyDiagnosticId,
        "[NoCopy] type must not be copied",
        "Copying '{0}' of type '{1}' creates an expensive copy; use 'ref' to avoid copies{2}",
        "Performance",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Types marked with [NoCopy] are large structs designed for zero-allocation performance. " +
            "Copying them creates expensive stack copies and duplicates internal state. " +
            "All usage should pass by 'ref' to avoid copies.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(RefRule, CopyRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var catalog = new OwnershipCatalog(start.Compilation);

            // TYPHON001: parameter ref enforcement
            start.RegisterSyntaxNodeAction(
                node => AnalyzeMethodDeclaration(node, catalog),
                SyntaxKind.MethodDeclaration,
                SyntaxKind.ConstructorDeclaration,
                SyntaxKind.DelegateDeclaration,
                SyntaxKind.LocalFunctionStatement);

            // TYPHON003: copy detection
            start.RegisterOperationAction(op => AnalyzeAssignment(op, catalog), OperationKind.SimpleAssignment);
            start.RegisterOperationAction(op => AnalyzeVariableDeclarator(op, catalog), OperationKind.VariableDeclarator);
            start.RegisterOperationAction(op => AnalyzeReturn(op, catalog), OperationKind.Return);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TYPHON001 — parameter analysis
    // ═══════════════════════════════════════════════════════════════════════

    private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext context, OwnershipCatalog catalog)
    {
        var parameterList = context.Node switch
        {
            MethodDeclarationSyntax method => method.ParameterList,
            ConstructorDeclarationSyntax ctor => ctor.ParameterList,
            DelegateDeclarationSyntax del => del.ParameterList,
            LocalFunctionStatementSyntax local => local.ParameterList,
            _ => null
        };

        if (parameterList == null)
        {
            return;
        }

        foreach (var parameter in parameterList.Parameters)
        {
            var typeSymbol = context.SemanticModel.GetTypeInfo(parameter.Type!).Type;
            if (typeSymbol == null || !catalog.IsNoCopy(typeSymbol))
            {
                continue;
            }

            // Only 'ref' modifier is acceptable
            var hasRefModifier = false;
            foreach (var modifier in parameter.Modifiers)
            {
                if (modifier.IsKind(SyntaxKind.RefKeyword))
                {
                    hasRefModifier = true;
                    break;
                }
            }

            if (!hasRefModifier)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RefRule,
                    parameter.GetLocation(),
                    parameter.Identifier.Text,
                    typeSymbol.Name,
                    FormatReason(catalog, typeSymbol)));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TYPHON003 — copy detection
    // ═══════════════════════════════════════════════════════════════════════

    private static void AnalyzeAssignment(OperationAnalysisContext context, OwnershipCatalog catalog)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        if (!catalog.IsNoCopy(assignment.Target.Type))
        {
            return;
        }

        // Ref reassignment — no copy
        if (assignment.IsRef)
        {
            return;
        }

        // Allowed: invocations, default, new expressions
        if (IsAllowedValue(assignment.Value))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            CopyRule,
            assignment.Syntax.GetLocation(),
            GetExpressionName(assignment.Value),
            assignment.Target.Type.Name,
            FormatReason(catalog, assignment.Target.Type)));
    }

    private static void AnalyzeVariableDeclarator(OperationAnalysisContext context, OwnershipCatalog catalog)
    {
        var declarator = (IVariableDeclaratorOperation)context.Operation;
        var localSymbol = declarator.Symbol;
        if (localSymbol == null || !catalog.IsNoCopy(localSymbol.Type))
        {
            return;
        }

        // Ref local — no copy
        if (localSymbol.RefKind != RefKind.None)
        {
            return;
        }

        var initializer = declarator.Initializer;
        if (initializer?.Value == null)
        {
            return;
        }

        var initValue = initializer.Value;

        // Allowed: invocations, default, new expressions
        if (IsAllowedValue(initValue))
        {
            return;
        }

        // Build expression name with ref field hint
        var expressionName = GetExpressionName(initValue);
        if (IsRefFieldAccess(initValue))
        {
            expressionName += " (ref field dereference — use 'ref var' instead of 'var')";
        }

        context.ReportDiagnostic(Diagnostic.Create(
            CopyRule,
            declarator.Syntax.GetLocation(),
            expressionName,
            localSymbol.Type.Name,
            FormatReason(catalog, localSymbol.Type)));
    }

    private static void AnalyzeReturn(OperationAnalysisContext context, OwnershipCatalog catalog)
    {
        var returnOp = (IReturnOperation)context.Operation;
        if (returnOp.ReturnedValue == null || !catalog.IsNoCopy(returnOp.ReturnedValue.Type))
        {
            return;
        }

        // Ref-returning methods/properties don't copy
        if (context.ContainingSymbol is IMethodSymbol { ReturnsByRef: true })
        {
            return;
        }

        // A member marked [AllowCopy] deliberately returns a copy of itself — `GetEnumerator() => this` on an enumerator a foreach consumes
        if (context.ContainingSymbol is IMethodSymbol method
            && (HasAllowCopyAttribute(method) || (method.AssociatedSymbol != null && HasAllowCopyAttribute(method.AssociatedSymbol))))
        {
            return;
        }

        // Allowed: invocations, default, new expressions
        if (IsAllowedValue(returnOp.ReturnedValue))
        {
            return;
        }

        // Allow returning a non-ref local — its initializer was already validated at the declaration site.
        // This supports the factory pattern: var x = new NoCopyType(...); /* setup */ return x;
        var unwrapped = UnwrapConversions(returnOp.ReturnedValue);
        if (unwrapped is ILocalReferenceOperation localRef && localRef.Local.RefKind == RefKind.None)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            CopyRule,
            returnOp.Syntax.GetLocation(),
            GetExpressionName(returnOp.ReturnedValue),
            returnOp.ReturnedValue.Type.Name,
            FormatReason(catalog, returnOp.ReturnedValue.Type)));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Shared helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Where <c>[NoCopy]</c> and <c>[AllowCopy]</c> are declared. The match used to name <c>Typhon.Engine</c>, which neither attribute has lived in since
    /// #331, so the analyzer never fired and a by-value <c>ChunkAccessor</c> compiled without a word (#1226).
    /// </summary>
    internal const string AttributeNamespace = OwnershipCatalog.AttributeNamespace;

    /// <summary>
    /// Checks whether the given member is marked with <c>[AllowCopy]</c>.
    /// </summary>
    private static bool HasAllowCopyAttribute(ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name is "AllowCopyAttribute" or "AllowCopy" &&
                attr.AttributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a by-value call or read of a member declared to return <paramref name="declaredType"/> hands over a value nobody else holds.
    /// </summary>
    /// <remarks>
    /// <para>Not a <c>ref</c> return: it hands out an EXISTING instance (a rented, cached accessor), so taking it into a by-value local copies it — the
    /// PS-19 leak. A caller that takes it by ref (a ref local or ref assignment) copies nothing and never gets here.</para>
    /// <para>Not a member declared to return a type parameter: <c>List&lt;T&gt;</c>'s indexer, <c>Nullable&lt;T&gt;.Value</c>, a generic
    /// <c>T Get&lt;T&gt;()</c>. Its body returns a <c>T</c>, which no rule can tell is a no-copy type, so its copy is reported here or nowhere.</para>
    /// <para>This replaces the requirement that every factory carry <c>[AllowCopy]</c>: the analyzer could not tell a factory from a getter at the call
    /// site, but it does not need to — the getter's copy is in the getter's return, which TYPHON003 reports. <c>[AllowCopy]</c> is left for a member
    /// that deliberately returns a copy of itself (<c>GetEnumerator() =&gt; this</c>).</para>
    /// </remarks>
    private static bool HandsOverAFreshValue(RefKind refKind, ITypeSymbol declaredType) =>
        refKind == RefKind.None && declaredType is not ITypeParameterSymbol;

    /// <summary>The message suffix for a no-copy type: its marker's reason, or the field through which it holds one.</summary>
    private static string FormatReason(OwnershipCatalog catalog, ITypeSymbol typeSymbol) => catalog.IsNoCopy(typeSymbol, out var reason) ? reason : "";

    /// <summary>
    /// Returns <c>true</c> if the operation is an allowed value for [NoCopy] types:
    /// <c>default</c>, <c>new</c>, a throw, or a by-value call or property read of a member declared to return the type
    /// (see <see cref="HandsOverAFreshValue"/>).
    /// </summary>
    private static bool IsAllowedValue(IOperation operation)
    {
        var unwrapped = UnwrapConversions(operation);

        // A throw produces no value: `_ => throw ...` in a switch, `?? throw ...`
        if (unwrapped is IDefaultValueOperation or IObjectCreationOperation or IThrowOperation)
        {
            return true;
        }

        // A by-value call or property read hands over a value its caller now owns. A getter that duplicates live state does so in its own body —
        // `=> _field`, `return _array[i]`, `return *p` — and that return is reported where it is written, so the call site need not ask. Two exceptions:
        if (unwrapped is IInvocationOperation invocation)
        {
            return HandsOverAFreshValue(invocation.TargetMethod.RefKind, invocation.TargetMethod.OriginalDefinition.ReturnType);
        }

        if (unwrapped is IPropertyReferenceOperation propRef)
        {
            return HandsOverAFreshValue(propRef.Property.RefKind, propRef.Property.OriginalDefinition.Type);
        }

        // Conditional: both branches must be allowed
        if (unwrapped is IConditionalOperation conditional)
        {
            return IsAllowedValue(conditional.WhenTrue) && IsAllowedValue(conditional.WhenFalse);
        }

        // `a ?? b`: both sides must be allowed — `segment?.CreateChunkAccessor() ?? default` is a factory or a default, never a copy
        if (unwrapped is ICoalesceOperation coalesce)
        {
            return IsAllowedValue(coalesce.Value) && IsAllowedValue(coalesce.WhenNull);
        }

        // `a?.M()`: what is produced is M's result, so it is allowed when M's is
        if (unwrapped is IConditionalAccessOperation conditionalAccess)
        {
            return IsAllowedValue(conditionalAccess.WhenNotNull);
        }

        // A switch expression, like `?:`: every arm must be allowed
        if (unwrapped is ISwitchExpressionOperation switchExpression)
        {
            foreach (var arm in switchExpression.Arms)
            {
                if (!IsAllowedValue(arm.Value))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static IOperation UnwrapConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    /// <summary>
    /// Checks if the operation is accessing a ref field (which causes a copy without <c>ref</c> on the LHS).
    /// </summary>
    private static bool IsRefFieldAccess(IOperation operation)
    {
        var unwrapped = UnwrapConversions(operation);
        return unwrapped is IFieldReferenceOperation fieldRef && fieldRef.Field.RefKind != RefKind.None;
    }

    private static string GetExpressionName(IOperation operation) =>
        operation switch
        {
            ILocalReferenceOperation localRef => localRef.Local.Name,
            IFieldReferenceOperation fieldRef => fieldRef.Field.Name,
            IParameterReferenceOperation paramRef => paramRef.Parameter.Name,
            IInvocationOperation invocation => invocation.TargetMethod.Name + "()",
            _ => operation.Syntax.ToString()
        };
}
