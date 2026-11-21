using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// <b>TYPHON017</b> — a <c>Where&lt;T&gt;(Func&lt;T, bool&gt;)</c> predicate on an <c>EcsQuery</c> reads an <c>[Index]</c> field of <c>T</c> (#1586 §4).
/// </summary>
/// <remarks>
/// <para>
/// <c>Where</c> takes an opaque delegate, so the query walks every entity of its archetypes and calls the delegate on each: a predicate on an indexed field
/// silently skips the index. <c>WhereField&lt;T&gt;(Expression&lt;Func&lt;T, bool&gt;&gt;)</c> parses the same lambda and drives the scan from the index.
/// </para>
/// <para>
/// Deterministic, no heuristic: it fires only when the argument is a lambda written at the call and its body reads <c>param.Field</c> where
/// <c>Field</c> is a field of <c>T</c> carrying <c>Typhon.Schema.Definition.IndexAttribute</c>. A delegate variable or method group is opaque to the
/// analyzer too and gets no diagnostic. The code fix (<see cref="IndexedFieldInOpaqueWhereCodeFixProvider"/>) is offered only when the whole predicate is
/// a shape <c>WhereField</c> accepts — see <see cref="IsWhereFieldShape"/>.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IndexedFieldInOpaqueWhereAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON017";

    /// <summary>Diagnostic property set when the code fix can rewrite the call to <c>WhereField</c>.</summary>
    public const string FixableProperty = "WhereFieldFixable";

    private const string IndexAttributeFqn = "Typhon.Schema.Definition.IndexAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Where(Func) on an indexed field scans every entity",
        "Where<{0}>(...) reads the [Index] field '{1}' through an opaque delegate, so the query scans every entity instead of using the index. "
        + "Use WhereField<{0}>(...) with the same lambda.",
        "Performance",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "EcsQuery.Where<T>(Func<T, bool>) cannot see inside its delegate: it opens every entity the query admits and calls the predicate on each. "
            + "WhereField<T>(Expression<Func<T, bool>>) parses the predicate, seeks or range-scans the field's index, and backs incremental views. "
            + "Keep Where for conditions WhereField cannot express (two fields compared, a computed value, a non-indexed field).",
        helpLinkUri: DiagnosticHelp.For(DiagnosticId));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess || memberAccess.Name.Identifier.Text != "Where")
        {
            return;
        }

        if (invocation.ArgumentList.Arguments.Count != 1 || invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda)
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || !IsEcsQueryWhere(method, out var componentType))
        {
            return;
        }

        // A WhereField on another component in the same fluent chain makes Where the right call: WhereField refuses a second component, and the engine's
        // own guidance for that case is Where. No diagnostic, since its advice would trade a slow query for a throwing one.
        if (ChainHasWhereFieldOnOtherComponent(invocation, componentType, context.SemanticModel, context.CancellationToken))
        {
            return;
        }

        var parameter = LambdaParameter(lambda);
        if (parameter == null)
        {
            return;
        }

        var parameterSymbol = context.SemanticModel.GetDeclaredSymbol(parameter, context.CancellationToken);
        if (parameterSymbol == null)
        {
            return;
        }

        string firstIndexed = null;
        foreach (var node in lambda.Body.DescendantNodesAndSelf())
        {
            if (node is MemberAccessExpressionSyntax access && IsIndexedFieldOfParameter(access, parameterSymbol, componentType, context.SemanticModel,
                    context.CancellationToken))
            {
                firstIndexed = access.Name.Identifier.Text;
                break;
            }
        }

        if (firstIndexed == null)
        {
            return;
        }

        var properties = ImmutableDictionary<string, string>.Empty;
        if (IsWhereFieldShape(lambda, parameterSymbol, componentType, context.SemanticModel, context.CancellationToken))
        {
            properties = properties.Add(FixableProperty, "true");
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.Name.GetLocation(), properties, componentType.Name, firstIndexed));
    }

    /// <summary>
    /// <c>EcsQuery&lt;TArch&gt;.Where&lt;T&gt;(Func&lt;T, bool&gt;)</c> in namespace <c>Typhon.Engine</c>; <paramref name="componentType"/> is <c>T</c>.
    /// </summary>
    private static bool IsEcsQueryWhere(IMethodSymbol method, out INamedTypeSymbol componentType)
    {
        componentType = null;
        var containing = method.ContainingType;
        if (containing == null || containing.Name != "EcsQuery" || containing.ContainingNamespace?.ToDisplayString() != "Typhon.Engine")
        {
            return false;
        }

        if (method.TypeArguments.Length != 1 || method.Parameters.Length != 1)
        {
            return false;
        }

        if (method.Parameters[0].Type is not INamedTypeSymbol { Name: "Func", TypeArguments.Length: 2 })
        {
            return false;
        }

        componentType = method.TypeArguments[0] as INamedTypeSymbol;
        return componentType != null;
    }

    private static ParameterSyntax LambdaParameter(LambdaExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => simple.Parameter,
        ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } paren => paren.ParameterList.Parameters[0],
        _ => null
    };

    /// <summary>
    /// <c>p.Field</c> where <c>p</c> is the lambda's parameter and <c>Field</c> is an <c>[Index]</c> field of <paramref name="componentType"/>.
    /// </summary>
    internal static bool IsIndexedFieldOfParameter(MemberAccessExpressionSyntax access, IParameterSymbol parameter, INamedTypeSymbol componentType,
        SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (access.Expression is not IdentifierNameSyntax receiver
            || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(receiver, ct).Symbol, parameter))
        {
            return false;
        }

        return model.GetSymbolInfo(access, ct).Symbol is IFieldSymbol field
               && SymbolEqualityComparer.Default.Equals(field.ContainingType, componentType)
               && HasIndexAttribute(field);
    }

    private static bool HasIndexAttribute(IFieldSymbol field)
    {
        foreach (var attribute in field.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == IndexAttributeFqn)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <c>WhereField</c> accepts the lambda as written: an expression body made of comparisons joined by <c>&amp;&amp;</c>, each comparing one
    /// <c>[Index]</c> field of the parameter (of a numeric type, or <c>bool</c> under <c>==</c>/<c>!=</c>) to a value that does not read the parameter.
    /// </summary>
    /// <remarks>
    /// This is the subset of <c>ExpressionParser</c>'s grammar that every terminal runs: <c>||</c> parses but a one-shot terminal refuses a multi-branch
    /// predicate, a field compared to a field or a non-indexed field is refused by <c>WhereField</c>, and a string field has no tree key. Narrower than the
    /// parser is fine — a missed fix leaves the warning for a human; a fix that produced a refused shape would trade a slow query for a throwing one.
    /// </remarks>
    internal static bool IsWhereFieldShape(LambdaExpressionSyntax lambda, IParameterSymbol parameter, INamedTypeSymbol componentType, SemanticModel model,
        System.Threading.CancellationToken ct) =>
        lambda.Body is ExpressionSyntax body && IsConjunctionOfComparisons(body, parameter, componentType, model, ct);

    private static bool IsConjunctionOfComparisons(ExpressionSyntax expr, IParameterSymbol parameter, INamedTypeSymbol componentType, SemanticModel model,
        System.Threading.CancellationToken ct)
    {
        while (expr is ParenthesizedExpressionSyntax paren)
        {
            expr = paren.Expression;
        }

        if (expr is not BinaryExpressionSyntax binary)
        {
            return false;
        }

        if (binary.IsKind(SyntaxKind.LogicalAndExpression))
        {
            return IsConjunctionOfComparisons(binary.Left, parameter, componentType, model, ct)
                   && IsConjunctionOfComparisons(binary.Right, parameter, componentType, model, ct);
        }

        var equality = binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression);
        var ordered = binary.IsKind(SyntaxKind.LessThanExpression) || binary.IsKind(SyntaxKind.LessThanOrEqualExpression)
                      || binary.IsKind(SyntaxKind.GreaterThanExpression) || binary.IsKind(SyntaxKind.GreaterThanOrEqualExpression);
        if (!equality && !ordered)
        {
            return false;
        }

        var leftField = FieldOperand(binary.Left, parameter, componentType, model, ct);
        var rightField = FieldOperand(binary.Right, parameter, componentType, model, ct);
        if ((leftField == null) == (rightField == null))
        {
            return false;   // no field, or field compared to field
        }

        var field = leftField ?? rightField;
        var value = leftField != null ? binary.Right : binary.Left;
        if (!IsPlainValue(value) || ReadsParameter(value, parameter, model, ct) || !IsPlainValueType(model.GetTypeInfo(value, ct).Type)
            || ContainsDynamic(value, model, ct))
        {
            return false;
        }

        return field.Type.SpecialType switch
        {
            SpecialType.System_Boolean => equality,
            SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
                or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single
                or SpecialType.System_Double => true,
            _ => false
        };
    }

    /// <summary>
    /// The indexed field <paramref name="operand"/> reads (through parentheses), or null. An explicit cast disqualifies: the parser strips conversions,
    /// so <c>(byte)p.Level == 3</c> would become <c>p.Level == 3</c> and drop the truncation the delegate applied.
    /// </summary>
    private static IFieldSymbol FieldOperand(ExpressionSyntax operand, IParameterSymbol parameter, INamedTypeSymbol componentType, SemanticModel model,
        System.Threading.CancellationToken ct)
    {
        while (operand is ParenthesizedExpressionSyntax paren)
        {
            operand = paren.Expression;
        }

        return operand is MemberAccessExpressionSyntax access && IsIndexedFieldOfParameter(access, parameter, componentType, model, ct)
            ? (IFieldSymbol)model.GetSymbolInfo(access, ct).Symbol
            : null;
    }

    /// <summary>
    /// A value an expression tree can hold and the parser can evaluate: literals, names, member chains (<c>Limits.Max</c>, <c>int.MaxValue</c>), casts,
    /// unary +/- and arithmetic over those. Anything else — a call (optional arguments are refused in expression trees), <c>?.</c>, <c>??</c> over a
    /// conditional access, a throw, switch or index-from-end expression — may not compile once the lambda becomes an <c>Expression</c>.
    /// </summary>
    private static bool IsPlainValue(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case ParenthesizedExpressionSyntax paren:
                return IsPlainValue(paren.Expression);
            case LiteralExpressionSyntax literal:
                return literal.IsKind(SyntaxKind.NumericLiteralExpression) || literal.IsKind(SyntaxKind.TrueLiteralExpression)
                       || literal.IsKind(SyntaxKind.FalseLiteralExpression) || literal.IsKind(SyntaxKind.CharacterLiteralExpression);
            case IdentifierNameSyntax:
            case PredefinedTypeSyntax:
            case ThisExpressionSyntax:
                return true;
            case MemberAccessExpressionSyntax member:
                return member.IsKind(SyntaxKind.SimpleMemberAccessExpression) && member.Name is IdentifierNameSyntax && IsPlainValue(member.Expression);
            case CastExpressionSyntax cast:
                return IsPlainValue(cast.Expression);
            case PrefixUnaryExpressionSyntax unary:
                return (unary.IsKind(SyntaxKind.UnaryMinusExpression) || unary.IsKind(SyntaxKind.UnaryPlusExpression)) && IsPlainValue(unary.Operand);
            case BinaryExpressionSyntax arithmetic:
                return (arithmetic.IsKind(SyntaxKind.AddExpression) || arithmetic.IsKind(SyntaxKind.SubtractExpression)
                        || arithmetic.IsKind(SyntaxKind.MultiplyExpression) || arithmetic.IsKind(SyntaxKind.DivideExpression)
                        || arithmetic.IsKind(SyntaxKind.ModuloExpression))
                       && IsPlainValue(arithmetic.Left) && IsPlainValue(arithmetic.Right);
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether the fluent chain <paramref name="invocation"/> sits in — the calls it is made on and the calls made on its result — holds a
    /// <c>WhereField&lt;U&gt;</c> with <c>U</c> other than <paramref name="componentType"/>. Only the chain written as one expression is visible.
    /// </summary>
    private static bool ChainHasWhereFieldOnOtherComponent(InvocationExpressionSyntax invocation, INamedTypeSymbol componentType, SemanticModel model,
        System.Threading.CancellationToken ct)
    {
        // Climb to the outermost call of the chain: invocation -> member access -> invocation ...
        ExpressionSyntax top = invocation;
        while (top.Parent is MemberAccessExpressionSyntax access && access.Expression == top && access.Parent is InvocationExpressionSyntax outer)
        {
            top = outer;
        }

        // Walk down the receivers.
        var node = top;
        while (node is InvocationExpressionSyntax call && call.Expression is MemberAccessExpressionSyntax member)
        {
            if (member.Name.Identifier.Text == "WhereField"
                && model.GetSymbolInfo(call, ct).Symbol is IMethodSymbol { TypeArguments.Length: 1 } whereField
                && !SymbolEqualityComparer.Default.Equals(whereField.TypeArguments[0], componentType))
            {
                return true;
            }
            node = member.Expression;
        }

        return false;
    }

    /// <summary>
    /// A value of a numeric, <c>bool</c> or <c>char</c> type. Not nullable (an empty one compares as nothing, which a renamed call would carry over but a
    /// reader would not expect), not <c>dynamic</c> (refused in expression trees, CS1963).
    /// </summary>
    private static bool IsPlainValueType(ITypeSymbol type) => type != null && type.SpecialType switch
    {
        SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16
            or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal => true,
        _ => false
    };

    /// <summary>Whether any part of <paramref name="expr"/> is <c>dynamic</c>: a cast to int does not make <c>(int)d</c> legal in an expression tree.</summary>
    private static bool ContainsDynamic(ExpressionSyntax expr, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (var node in expr.DescendantNodesAndSelf())
        {
            if (node is ExpressionSyntax e && model.GetTypeInfo(e, ct).Type is { TypeKind: TypeKind.Dynamic })
            {
                return true;
            }
        }

        return false;
    }

    private static bool ReadsParameter(ExpressionSyntax expr, IParameterSymbol parameter, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (var node in expr.DescendantNodesAndSelf())
        {
            if (node is IdentifierNameSyntax id && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, ct).Symbol, parameter))
            {
                return true;
            }
        }

        return false;
    }
}
