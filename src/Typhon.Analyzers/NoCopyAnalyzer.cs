using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Typhon.Analyzers;

/// <summary>
/// Unified analyzer that protects types marked with <c>[NoCopy]</c> from value copies.
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

        // TYPHON001: parameter ref enforcement
        context.RegisterSyntaxNodeAction(
            AnalyzeMethodDeclaration,
            SyntaxKind.MethodDeclaration,
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.DelegateDeclaration,
            SyntaxKind.LocalFunctionStatement);

        // TYPHON003: copy detection
        context.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment);
        context.RegisterOperationAction(AnalyzeVariableDeclarator, OperationKind.VariableDeclarator);
        context.RegisterOperationAction(AnalyzeReturn, OperationKind.Return);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TYPHON001 — parameter analysis
    // ═══════════════════════════════════════════════════════════════════════

    private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext context)
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
            if (typeSymbol == null || !IsNoCopyType(typeSymbol))
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
                    FormatReason(typeSymbol)));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TYPHON003 — copy detection
    // ═══════════════════════════════════════════════════════════════════════

    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        if (!IsNoCopyType(assignment.Target.Type))
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
            FormatReason(assignment.Target.Type)));
    }

    private static void AnalyzeVariableDeclarator(OperationAnalysisContext context)
    {
        var declarator = (IVariableDeclaratorOperation)context.Operation;
        var localSymbol = declarator.Symbol;
        if (localSymbol == null || !IsNoCopyType(localSymbol.Type))
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
            FormatReason(localSymbol.Type)));
    }

    private static void AnalyzeReturn(OperationAnalysisContext context)
    {
        var returnOp = (IReturnOperation)context.Operation;
        if (returnOp.ReturnedValue == null || !IsNoCopyType(returnOp.ReturnedValue.Type))
        {
            return;
        }

        // Ref-returning methods/properties don't copy
        if (context.ContainingSymbol is IMethodSymbol { ReturnsByRef: true })
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
            FormatReason(returnOp.ReturnedValue.Type)));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Shared helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Where <c>[NoCopy]</c> and <c>[AllowCopy]</c> are declared. The match used to name <c>Typhon.Engine</c>, which neither attribute has lived in since
    /// #331, so the analyzer never fired and a by-value <c>ChunkAccessor</c> compiled without a word (#1226).
    /// </summary>
    internal const string AttributeNamespace = "Typhon.Engine.Internals";

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
    /// Checks whether the given type is marked with <c>[NoCopy]</c>.
    /// </summary>
    private static bool IsNoCopyType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol == null)
        {
            return false;
        }

        foreach (var attr in typeSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name is "NoCopyAttribute" or "NoCopy" &&
                attr.AttributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts the <c>Reason</c> property from the <c>[NoCopy]</c> attribute, if present.
    /// Returns a formatted suffix like " — reason text" or an empty string.
    /// </summary>
    private static string FormatReason(ITypeSymbol typeSymbol)
    {
        if (typeSymbol == null)
        {
            return "";
        }

        foreach (var attr in typeSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name is "NoCopyAttribute" or "NoCopy" &&
                attr.AttributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace)
            {
                foreach (var namedArg in attr.NamedArguments)
                {
                    if (namedArg.Key == "Reason" && namedArg.Value.Value is string reason && reason.Length > 0)
                    {
                        return " — " + reason;
                    }
                }
            }
        }

        return "";
    }

    /// <summary>
    /// Returns <c>true</c> if the operation is an allowed value for [NoCopy] types:
    /// <c>default</c>, <c>new</c> expressions, or invocations/properties marked with <c>[AllowCopy]</c>.
    /// </summary>
    private static bool IsAllowedValue(IOperation operation)
    {
        var unwrapped = UnwrapConversions(operation);

        if (unwrapped is IDefaultValueOperation or IObjectCreationOperation)
        {
            return true;
        }

        // Method calls returning [NoCopy] types require [AllowCopy] — the analyzer cannot
        // distinguish factories (fresh value) from getters (copy of existing state).
        if (unwrapped is IInvocationOperation invocation)
        {
            // A ref-returning method hands out an EXISTING instance (a rented, cached accessor), so taking it into a by-value local copies it whatever its
            // marking — the PS-19 leak. Every caller that takes it by ref returned earlier (a ref local or ref assignment is no copy).
            return HasAllowCopyAttribute(invocation.TargetMethod) && invocation.TargetMethod.RefKind == RefKind.None;
        }

        // Property reads marked [AllowCopy] — sentinel properties like None that inline to direct construction
        if (unwrapped is IPropertyReferenceOperation propRef && HasAllowCopyAttribute(propRef.Property))
        {
            return true;
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
