using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Typhon.Engine.Internals;

internal readonly struct FieldPredicate
{
    public readonly string FieldName;
    public readonly CompareOp Operator;
    public readonly object Value;

    public FieldPredicate(string fieldName, CompareOp op, object value)
    {
        FieldName = fieldName;
        Operator = op;
        Value = value;
    }
}

internal static class ExpressionParser
{
    internal const int MaxDnfBranches = 16;

    public static FieldPredicate[] Parse<T>(Expression<Func<T, bool>> expression)
    {
        // Phase 7: Query:Parse span — covers single-clause predicate parsing. Stats filled at exit.
        var parseScope = TyphonEvent.BeginQueryParse(0, 1);
        try
        {
            var predicates = new List<FieldPredicate>();
            CollectPredicates(expression.Body, [expression.Parameters[0]], predicates, null);
            parseScope.PredicateCount = (ushort)Math.Min(predicates.Count, ushort.MaxValue);
            return predicates.ToArray();
        }
        finally
        {
            parseScope.Dispose();
        }
    }

    /// <summary>
    /// Parses a predicate expression into Disjunctive Normal Form (OR of ANDs).
    /// Returns FieldPredicate[][] where outer = OR branches, inner = AND predicates per branch.
    /// Pure AND expressions return a single-element outer array.
    /// </summary>
    /// <remarks>
    /// <b>Branch limit:</b> The normalized DNF is capped at 16 branches. Simple OR chains (A || B || ... up to 16 terms) and mixed expressions with a few OR
    /// pairs are fine. The pattern that causes exponential blowup is ANDing multiple OR pairs: <c>(A||B) &amp;&amp; (C||D) &amp;&amp; (E||F)</c>
    /// produces 2×2×2 = 8 branches. Five such pairs would exceed the limit.
    /// If you hit the limit, restructure the query to reduce the number of ANDed OR pairs, or split into separate queries.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the normalized DNF exceeds 16 branches.</exception>
    public static FieldPredicate[][] ParseDnf<T>(Expression<Func<T, bool>> expression)
    {
        // Phase 7: Query:Parse:DNF span — covers normalization. inBranches=1 (input), outBranches set after CollectDnfBranches.
        var dnfScope = TyphonEvent.BeginQueryParseDnf(1, 0);
        try
        {
            var parameters = new[] { expression.Parameters[0] };
            var ast = BuildAst(expression.Body, parameters, false);
            var dnf = ToDnf(ast);
            var branches = CollectDnfBranches(dnf, parameters);
            dnfScope.OutBranches = (ushort)Math.Min(branches.Length, ushort.MaxValue);
            if (branches.Length > MaxDnfBranches)
            {
                throw new InvalidOperationException(
                    $"Predicate normalizes to {branches.Length} DNF clauses (max {MaxDnfBranches}). " +
                    "This typically happens when multiple OR pairs are ANDed together: each additional (A||B) doubles " +
                    "the clause count. Simplify by reducing the number of ANDed OR pairs, or split into separate queries.");
            }
            return branches;
        }
        finally
        {
            dnfScope.Dispose();
        }
    }

    public static string ExtractFieldName<T, TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var body = keySelector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }
        if (body is MemberExpression member && member.Expression == keySelector.Parameters[0])
        {
            return member.Member.Name;
        }
        throw new NotSupportedException("OrderBy expression must be a simple field access (e.g., p => p.Level).");
    }

    public static (FieldPredicate[] ForT1, FieldPredicate[] ForT2) Parse<T1, T2>(Expression<Func<T1, T2, bool>> expression)
    {
        var predicates = new List<FieldPredicate>();
        var paramIndices = new List<int>();
        CollectPredicates(expression.Body, [expression.Parameters[0], expression.Parameters[1]], predicates, paramIndices);

        var forT1 = new List<FieldPredicate>();
        var forT2 = new List<FieldPredicate>();
        for (var i = 0; i < predicates.Count; i++)
        {
            if (paramIndices[i] == 0)
            {
                forT1.Add(predicates[i]);
            }
            else
            {
                forT2.Add(predicates[i]);
            }
        }

        return (forT1.ToArray(), forT2.ToArray());
    }

    private static void CollectPredicates(Expression expr, ParameterExpression[] parameters, List<FieldPredicate> predicates,
        List<int> paramIndices)
    {
        if (expr is not BinaryExpression binary)
        {
            throw new NotSupportedException($"Unsupported expression node: {expr.NodeType}");
        }

        if (binary.NodeType == ExpressionType.AndAlso)
        {
            CollectPredicates(binary.Left, parameters, predicates, paramIndices);
            CollectPredicates(binary.Right, parameters, predicates, paramIndices);
            return;
        }

        var op = MapCompareOp(binary.NodeType);
        if (op == null)
        {
            throw new NotSupportedException($"Unsupported expression type: {binary.NodeType}");
        }

        var (leftField, leftParamIndex) = TryExtractFieldWithParam(binary.Left, parameters);
        var (rightField, rightParamIndex) = TryExtractFieldWithParam(binary.Right, parameters);

        if (leftField != null && rightField != null)
        {
            throw new NotSupportedException("Field-to-field comparisons across parameters are not supported.");
        }

        if (leftField != null)
        {
            var value = EvaluateConstant(binary.Right);
            predicates.Add(new FieldPredicate(leftField, op.Value, value));
            paramIndices?.Add(leftParamIndex);
        }
        else if (rightField != null)
        {
            var value = EvaluateConstant(binary.Left);
            predicates.Add(new FieldPredicate(rightField, FlipOp(op.Value), value));
            paramIndices?.Add(rightParamIndex);
        }
        else
        {
            throw new NotSupportedException("Comparison must have exactly one field access and one constant.");
        }
    }

    private static (string fieldName, int paramIndex) TryExtractFieldWithParam(Expression expr, ParameterExpression[] parameters)
    {
        // Peel the conversions C# inserts to compare the field with a wider operand. They are what the normalisation models (QCLAUSE-01: `(double)p.I`
        // against a double constant). An explicit narrowing cast on the field is not: `(int)p.F > 3` truncates F first, which neither the index nor the
        // evaluator can express — it is refused below instead of silently compared as `F > 3`.
        var hasNarrowingCast = false;
        while (expr is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            hasNarrowingCast |= unary.Method != null || !IsImplicitConversion(unary.Operand.Type, unary.Type);
            expr = unary.Operand;
        }

        if (expr is MemberExpression member)
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                if (member.Expression == parameters[i])
                {
                    if (hasNarrowingCast)
                    {
                        throw new NotSupportedException(
                            $"A cast on the field '{member.Member.Name}' changes what the comparison selects, and a field predicate compares the field itself. "
                            + "Compare the field against a value of its own type, or use Where<T>(Func<T, bool>) for the cast.");
                    }
                    return (member.Member.Name, i);
                }
            }
        }

        return (null, -1);
    }

    /// <summary>
    /// Whether C# converts <paramref name="from"/> to <paramref name="to"/> implicitly: a numeric widening, an enum to its underlying type, a lift to
    /// nullable.
    /// </summary>
    private static bool IsImplicitConversion(Type from, Type to)
    {
        if (from == to)
        {
            return true;
        }

        var toUnderlying = Nullable.GetUnderlyingType(to);
        if (toUnderlying != null)
        {
            return IsImplicitConversion(from, toUnderlying);
        }

        if (from.IsEnum)
        {
            return IsImplicitConversion(Enum.GetUnderlyingType(from), to);
        }

        return Type.GetTypeCode(from) switch
        {
            TypeCode.SByte => Type.GetTypeCode(to) is TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single or TypeCode.Double
                or TypeCode.Decimal,
            TypeCode.Byte => Type.GetTypeCode(to) is TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
                or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
            TypeCode.Int16 => Type.GetTypeCode(to) is TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
            TypeCode.UInt16 or TypeCode.Char => Type.GetTypeCode(to) is TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
                or TypeCode.Single or TypeCode.Double or TypeCode.Decimal or TypeCode.UInt16,
            TypeCode.Int32 => Type.GetTypeCode(to) is TypeCode.Int64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
            TypeCode.UInt32 => Type.GetTypeCode(to) is TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
            TypeCode.Int64 or TypeCode.UInt64 => Type.GetTypeCode(to) is TypeCode.Single or TypeCode.Double or TypeCode.Decimal,
            TypeCode.Single => Type.GetTypeCode(to) is TypeCode.Double,
            _ => false
        };
    }

    private static object EvaluateConstant(Expression expr)
    {
        // Fast path: literal constant (e.g., d.Score >= 5)
        if (expr is ConstantExpression constant)
        {
            return constant.Value;
        }

        // Fast path for closure-captured variables: field access on a display class instance.
        // Pattern: MemberAccess(ConstantExpression) — e.g., closure.threshold where threshold is a local variable.
        // This avoids Expression.Lambda().Compile().DynamicInvoke() which costs ~100+ µs per call.
        // The Convert nodes are peeled to reach the captured field, then re-applied to its value with C# semantics: the comparison runs on the converted
        // value, and dropping a narrowing cast changes what it selects (`l.I == (int)d` with d = 3.7 compares against 3; `l.F == (float)d` against the
        // float nearest d, not d). A conversion this cannot replay — user-defined, checked, nullable — takes the compiling fallback.
        var target = expr;
        while (target is UnaryExpression { NodeType: ExpressionType.Convert, Method: null } conv)
        {
            target = conv.Operand;
        }
        // A captured local (a field of the closure's display class) or a static member (`Limits.Max`, Expression == null).
        if (target is MemberExpression memberExpr && memberExpr.Expression is null or ConstantExpression)
        {
            var instance = (memberExpr.Expression as ConstantExpression)?.Value;
            var member = memberExpr.Member;
            object value = null;
            if (member is System.Reflection.FieldInfo fieldInfo)
            {
                value = fieldInfo.GetValue(instance);
            }
            else if (member is System.Reflection.PropertyInfo propInfo)
            {
                value = propInfo.GetValue(instance);
            }
            if (value != null && TryReplayConversions(expr, target, ref value))
            {
                return value;
            }
        }

        // Fallback: compile and invoke (handles complex nested expressions)
        return Expression.Lambda(expr).Compile().DynamicInvoke();
    }

    /// <summary>Applies the Convert chain between <paramref name="node"/> and <paramref name="leaf"/> to <paramref name="value"/>, innermost first.</summary>
    private static bool TryReplayConversions(Expression node, Expression leaf, ref object value)
    {
        if (node == leaf)
        {
            return true;
        }

        var conv = (UnaryExpression)node;
        return TryReplayConversions(conv.Operand, leaf, ref value) && TryConvertNumeric(value, conv.Type, out value);
    }

    /// <summary>
    /// The unchecked C# conversion of a boxed primitive to <paramref name="target"/> — what a compiled <c>Convert</c> node computes. False for anything
    /// that is not a primitive numeric conversion.
    /// </summary>
    internal static bool TryConvertNumeric(object value, Type target, out object result)
    {
        result = value;
        if (value.GetType() == target)
        {
            return true;
        }

        // An enum converts through its underlying type, as the IL does.
        if (value.GetType().IsEnum)
        {
            value = Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), System.Globalization.CultureInfo.InvariantCulture);
            if (value.GetType() == target)
            {
                result = value;
                return true;
            }
        }
        if (target.IsEnum)
        {
            if (!TryConvertNumeric(value, Enum.GetUnderlyingType(target), out var underlying))
            {
                return false;
            }
            result = Enum.ToObject(target, underlying);
            return true;
        }

        var tc = Type.GetTypeCode(target);
        switch (value)
        {
            case double or float:
            {
                var d = value is double dv ? dv : (float)value;
                result = tc switch
                {
                    TypeCode.SByte => (sbyte)d,
                    TypeCode.Byte => (byte)d,
                    TypeCode.Int16 => (short)d,
                    TypeCode.UInt16 => (ushort)d,
                    TypeCode.Char => (char)d,
                    TypeCode.Int32 => (int)d,
                    TypeCode.UInt32 => (uint)d,
                    TypeCode.Int64 => (long)d,
                    TypeCode.UInt64 => (ulong)d,
                    TypeCode.Single => (float)d,
                    TypeCode.Double => d,
                    _ => null
                };
                break;
            }
            case ulong u:
                result = tc switch
                {
                    TypeCode.SByte => unchecked((sbyte)u),
                    TypeCode.Byte => unchecked((byte)u),
                    TypeCode.Int16 => unchecked((short)u),
                    TypeCode.UInt16 => unchecked((ushort)u),
                    TypeCode.Char => unchecked((char)u),
                    TypeCode.Int32 => unchecked((int)u),
                    TypeCode.UInt32 => unchecked((uint)u),
                    TypeCode.Int64 => unchecked((long)u),
                    TypeCode.Single => (float)u,
                    TypeCode.Double => (double)u,
                    _ => null
                };
                break;
            case sbyte or byte or short or ushort or char or int or uint or long:
            {
                var l = value is char c ? c : Convert.ToInt64(value);
                result = tc switch
                {
                    TypeCode.SByte => unchecked((sbyte)l),
                    TypeCode.Byte => unchecked((byte)l),
                    TypeCode.Int16 => unchecked((short)l),
                    TypeCode.UInt16 => unchecked((ushort)l),
                    TypeCode.Char => unchecked((char)l),
                    TypeCode.Int32 => unchecked((int)l),
                    TypeCode.UInt32 => unchecked((uint)l),
                    TypeCode.Int64 => l,
                    TypeCode.UInt64 => unchecked((ulong)l),
                    TypeCode.Single => (float)l,
                    TypeCode.Double => (double)l,
                    _ => null
                };
                break;
            }
            default:
                result = null;
                break;
        }

        return result != null;
    }

    private static CompareOp? MapCompareOp(ExpressionType type) =>
        type switch
        {
            ExpressionType.Equal => CompareOp.Equal,
            ExpressionType.NotEqual => CompareOp.NotEqual,
            ExpressionType.GreaterThan => CompareOp.GreaterThan,
            ExpressionType.LessThan => CompareOp.LessThan,
            ExpressionType.GreaterThanOrEqual => CompareOp.GreaterThanOrEqual,
            ExpressionType.LessThanOrEqual => CompareOp.LessThanOrEqual,
            _ => null
        };

    private static CompareOp FlipOp(CompareOp op) =>
        op switch
        {
            CompareOp.GreaterThan => CompareOp.LessThan,
            CompareOp.LessThan => CompareOp.GreaterThan,
            CompareOp.GreaterThanOrEqual => CompareOp.LessThanOrEqual,
            CompareOp.LessThanOrEqual => CompareOp.GreaterThanOrEqual,
            _ => op // Equal and NotEqual are symmetric
        };

    private static CompareOp InvertOp(CompareOp op) =>
        op switch
        {
            CompareOp.Equal => CompareOp.NotEqual,
            CompareOp.NotEqual => CompareOp.Equal,
            CompareOp.GreaterThan => CompareOp.LessThanOrEqual,
            CompareOp.LessThanOrEqual => CompareOp.GreaterThan,
            CompareOp.LessThan => CompareOp.GreaterThanOrEqual,
            CompareOp.GreaterThanOrEqual => CompareOp.LessThan,
            _ => throw new NotSupportedException($"Cannot invert operator {op}")
        };

    #region DNF AST

    /// <summary>Intermediate AST node for DNF normalization.</summary>
    private abstract class DnfNode { }

    private sealed class LeafNode : DnfNode
    {
        public readonly Expression Expr;
        public readonly bool Negated;

        public LeafNode(Expression expr, bool negated = false)
        {
            Expr = expr;
            Negated = negated;
        }
    }

    private sealed class AndNode : DnfNode
    {
        public readonly List<DnfNode> Children;
        public AndNode(List<DnfNode> children) { Children = children; }
    }

    private sealed class OrNode : DnfNode
    {
        public readonly List<DnfNode> Children;
        public OrNode(List<DnfNode> children) { Children = children; }
    }

    /// <summary>
    /// Builds a logical AST from an expression tree, handling AND, OR, and NOT.
    /// NOT is pushed down to leaves (De Morgan's law applied immediately).
    /// </summary>
    private static DnfNode BuildAst(Expression expr, ParameterExpression[] parameters, bool negated)
    {
        // Handle NOT: !(expr)
        if (expr is UnaryExpression { NodeType: ExpressionType.Not } notExpr)
        {
            return BuildAst(notExpr.Operand, parameters, !negated);
        }

        if (expr is BinaryExpression binary)
        {
            if (binary.NodeType == ExpressionType.AndAlso)
            {
                if (!negated)
                {
                    // AND(A, B)
                    var left = BuildAst(binary.Left, parameters, false);
                    var right = BuildAst(binary.Right, parameters, false);
                    return new AndNode(FlattenChildren<AndNode>(left, right));
                }
                // De Morgan: !(A && B) → !A || !B
                var leftNeg = BuildAst(binary.Left, parameters, true);
                var rightNeg = BuildAst(binary.Right, parameters, true);
                return new OrNode(FlattenChildren<OrNode>(leftNeg, rightNeg));
            }

            if (binary.NodeType == ExpressionType.OrElse)
            {
                if (!negated)
                {
                    // OR(A, B)
                    var left = BuildAst(binary.Left, parameters, false);
                    var right = BuildAst(binary.Right, parameters, false);
                    return new OrNode(FlattenChildren<OrNode>(left, right));
                }
                // De Morgan: !(A || B) → !A && !B
                var leftNeg = BuildAst(binary.Left, parameters, true);
                var rightNeg = BuildAst(binary.Right, parameters, true);
                return new AndNode(FlattenChildren<AndNode>(leftNeg, rightNeg));
            }
        }

        // Leaf comparison (e.g., p.B > 50)
        return new LeafNode(expr, negated);
    }

    /// <summary>Flatten nested nodes of the same type (AND inside AND, OR inside OR).</summary>
    private static List<DnfNode> FlattenChildren<TNode>(DnfNode left, DnfNode right) where TNode : DnfNode
    {
        var result = new List<DnfNode>();
        if (left is TNode sameLeft)
        {
            result.AddRange(typeof(TNode) == typeof(AndNode) ? ((AndNode)(DnfNode)sameLeft).Children : ((OrNode)(DnfNode)sameLeft).Children);
        }
        else
        {
            result.Add(left);
        }

        if (right is TNode sameRight)
        {
            result.AddRange(typeof(TNode) == typeof(AndNode) ? ((AndNode)(DnfNode)sameRight).Children : ((OrNode)(DnfNode)sameRight).Children);
        }
        else
        {
            result.Add(right);
        }
        return result;
    }

    /// <summary>
    /// Converts an AST to Disjunctive Normal Form (OR of ANDs) by distributing AND over OR.
    /// </summary>
    private static DnfNode ToDnf(DnfNode node)
    {
        switch (node)
        {
            case LeafNode:
                return node;

            case OrNode or:
            {
                // Recursively normalize children, flatten nested ORs
                var normalized = new List<DnfNode>();
                for (var i = 0; i < or.Children.Count; i++)
                {
                    var child = ToDnf(or.Children[i]);
                    if (child is OrNode childOr)
                    {
                        normalized.AddRange(childOr.Children);
                    }
                    else
                    {
                        normalized.Add(child);
                    }
                }
                return normalized.Count == 1 ? normalized[0] : new OrNode(normalized);
            }

            case AndNode and:
            {
                // Recursively normalize children
                var children = new List<DnfNode>(and.Children.Count);
                for (var i = 0; i < and.Children.Count; i++)
                {
                    children.Add(ToDnf(and.Children[i]));
                }

                // Distribute AND over OR: AND(OR(A,B), C) → OR(AND(A,C), AND(B,C))
                // Find the first OR child
                var orIndex = -1;
                for (var i = 0; i < children.Count; i++)
                {
                    if (children[i] is OrNode)
                    {
                        orIndex = i;
                        break;
                    }
                }

                if (orIndex < 0)
                {
                    // No OR children — this AND is already in DNF
                    return new AndNode(children);
                }

                // Distribute: collect all non-OR children as the "rest"
                var orChild = (OrNode)children[orIndex];
                var rest = new List<DnfNode>(children.Count - 1);
                for (var i = 0; i < children.Count; i++)
                {
                    if (i != orIndex)
                    {
                        rest.Add(children[i]);
                    }
                }

                // For each OR branch, create AND(branch, rest...)
                var distributed = new List<DnfNode>(orChild.Children.Count);
                for (var i = 0; i < orChild.Children.Count; i++)
                {
                    var branchChildren = new List<DnfNode>(rest.Count + 1) { orChild.Children[i] };
                    branchChildren.AddRange(rest);
                    distributed.Add(new AndNode(branchChildren));
                }

                // Recurse to handle remaining OR children
                return ToDnf(new OrNode(distributed));
            }

            default:
                throw new NotSupportedException($"Unknown DnfNode type: {node.GetType()}");
        }
    }

    /// <summary>
    /// Extracts FieldPredicate[][] from a DNF AST.
    /// Each OR branch becomes an inner FieldPredicate[] array.
    /// </summary>
    private static FieldPredicate[][] CollectDnfBranches(DnfNode dnf, ParameterExpression[] parameters)
    {
        switch (dnf)
        {
            case LeafNode leaf:
                // Single predicate → 1 branch with 1 predicate
                return [ExtractLeafPredicates(leaf, parameters)];

            case AndNode and:
                // Single AND branch
                return [ExtractAndPredicates(and, parameters)];

            case OrNode or:
            {
                var branches = new FieldPredicate[or.Children.Count][];
                for (var i = 0; i < or.Children.Count; i++)
                {
                    var child = or.Children[i];
                    branches[i] = child switch
                    {
                        LeafNode leaf => ExtractLeafPredicates(leaf, parameters),
                        AndNode and => ExtractAndPredicates(and, parameters),
                        _ => throw new NotSupportedException("DNF normalization produced unexpected node structure.")
                    };
                }
                return branches;
            }

            default:
                throw new NotSupportedException($"Unexpected DNF root: {dnf.GetType()}");
        }
    }

    private static FieldPredicate[] ExtractLeafPredicates(LeafNode leaf, ParameterExpression[] parameters)
    {
        var predicate = ParseLeafExpression(leaf.Expr, parameters, leaf.Negated);
        return [predicate];
    }

    private static FieldPredicate[] ExtractAndPredicates(AndNode and, ParameterExpression[] parameters)
    {
        var predicates = new FieldPredicate[and.Children.Count];
        for (var i = 0; i < and.Children.Count; i++)
        {
            if (and.Children[i] is not LeafNode leaf)
            {
                throw new NotSupportedException("DNF normalization produced nested compound node inside AND branch.");
            }
            predicates[i] = ParseLeafExpression(leaf.Expr, parameters, leaf.Negated);
        }
        return predicates;
    }

    /// <summary>Parses a single comparison expression into a FieldPredicate, applying negation if needed.</summary>
    private static FieldPredicate ParseLeafExpression(Expression expr, ParameterExpression[] parameters, bool negated)
    {
        if (expr is not BinaryExpression binary)
        {
            throw new NotSupportedException($"Unsupported expression node in leaf: {expr.NodeType}");
        }

        var op = MapCompareOp(binary.NodeType);
        if (op == null)
        {
            throw new NotSupportedException($"Unsupported expression type: {binary.NodeType}");
        }

        var resolvedOp = negated ? InvertOp(op.Value) : op.Value;

        (string leftField, _) = TryExtractFieldWithParam(binary.Left, parameters);
        (string rightField, _) = TryExtractFieldWithParam(binary.Right, parameters);

        if (leftField != null && rightField != null)
        {
            throw new NotSupportedException("Field-to-field comparisons across parameters are not supported.");
        }

        if (leftField != null)
        {
            var value = EvaluateConstant(binary.Right);
            return new FieldPredicate(leftField, resolvedOp, value);
        }
        if (rightField != null)
        {
            var value = EvaluateConstant(binary.Left);
            return new FieldPredicate(rightField, negated ? InvertOp(FlipOp(op.Value)) : FlipOp(op.Value), value);
        }

        throw new NotSupportedException("Comparison must have exactly one field access and one constant.");
    }

    #endregion
}
