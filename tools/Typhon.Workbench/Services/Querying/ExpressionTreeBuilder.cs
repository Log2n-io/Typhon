using System.Linq.Expressions;
using Typhon.Workbench.Dtos.Query;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Services.Querying;

/// <summary>
/// Synthesizes <see cref="Expression{TDelegate}"/> trees from a <see cref="PredicateNodeDto"/> AST. The engine's
/// <c>EcsQuery.WhereField&lt;T&gt;(Expression&lt;Func&lt;T, bool&gt;&gt;)</c> entry point parses the expression internally
/// to <see cref="Typhon.Engine.Internals.FieldPredicate"/>[][] DNF; rather than try to inject the parsed form directly
/// (which would bypass the pending-spawn matcher — see plan deviation D5), we re-emit Expression trees from the parsed
/// DSL and feed them through the engine's blessed entry point.
/// </summary>
/// <remarks>
/// The Expression tree is compiled at most once per Run (when the engine first materializes the predicate), not per row.
/// The ~100 µs compilation cost is negligible for a manual-UI action.
/// </remarks>
internal static class ExpressionTreeBuilder
{
    /// <summary>
    /// Build a <c>Expression&lt;Func&lt;T, bool&gt;&gt;</c> from a predicate AST rooted at a single component. The AST
    /// must reference exactly one component (the caller filters per-component before invoking — engine's
    /// <c>WhereField</c> is single-typed).
    /// </summary>
    /// <typeparam name="T">The component CLR type the predicate targets.</typeparam>
    /// <param name="node">Root of the AST. Must be non-null.</param>
    /// <returns>A lambda ready to pass to <c>EcsQuery.WhereField&lt;T&gt;()</c>.</returns>
    /// <exception cref="WorkbenchException"><c>invalid_field</c> when a referenced field doesn't exist on <typeparamref name="T"/>;
    /// <c>invalid_query_syntax</c> when the AST is malformed.</exception>
    public static Expression<Func<T, bool>> BuildPredicate<T>(PredicateNodeDto node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var param = Expression.Parameter(typeof(T), "x");
        var body = BuildBody(node, param, typeof(T));
        return Expression.Lambda<Func<T, bool>>(body, param);
    }

    /// <summary>
    /// Build a <c>Expression&lt;Func&lt;T, TKey&gt;&gt;</c> field selector — the form
    /// <c>EcsQuery.OrderByField&lt;T,TKey&gt;()</c> requires. <see cref="Typhon.Engine.Internals.ExpressionParser.ExtractFieldName"/>
    /// only accepts simple member access (<c>x =&gt; x.Field</c>); we emit exactly that.
    /// </summary>
    /// <typeparam name="T">The component CLR type.</typeparam>
    /// <typeparam name="TKey">The field's CLR type.</typeparam>
    /// <param name="fieldName">Field name to select.</param>
    public static Expression<Func<T, TKey>> BuildFieldSelector<T, TKey>(string fieldName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        var param = Expression.Parameter(typeof(T), "x");
        var member = ResolveMember(typeof(T), fieldName, param);
        // Cast if the member's declared type doesn't exactly match TKey — handles enum→underlying conversions.
        Expression body = member.Type == typeof(TKey) ? member : Expression.Convert(member, typeof(TKey));
        return Expression.Lambda<Func<T, TKey>>(body, param);
    }

    /// <summary>
    /// Resolve the CLR type of a field on a component — used by the compiler to decide which generic
    /// <c>BuildFieldSelector&lt;T,TKey&gt;</c> overload to call (we don't know <typeparamref name="TKey"/> at the call
    /// site, so we resolve it here and dispatch via <c>MakeGenericMethod</c>).
    /// </summary>
    public static Type ResolveFieldType(Type componentType, string fieldName)
    {
        var field = componentType.GetField(fieldName);
        if (field != null) return field.FieldType;
        var prop = componentType.GetProperty(fieldName);
        if (prop != null) return prop.PropertyType;
        throw new WorkbenchException(400, "invalid_field",
            $"Component '{componentType.Name}' has no field or property '{fieldName}'.");
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Internals
    // ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Expression BuildBody(PredicateNodeDto node, ParameterExpression param, Type componentType)
    {
        return node.Kind switch
        {
            "and" => CombineChildren(node, param, componentType, ExpressionType.AndAlso),
            "or" => CombineChildren(node, param, componentType, ExpressionType.OrElse),
            "cmp" => BuildCmp(node, param, componentType),
            _ => throw new WorkbenchException(400, "invalid_query_syntax",
                $"Unknown predicate node kind '{node.Kind}'. Expected 'and', 'or', or 'cmp'."),
        };
    }

    private static Expression CombineChildren(PredicateNodeDto node, ParameterExpression param, Type componentType, ExpressionType combineOp)
    {
        if (node.Children == null || node.Children.Length == 0)
        {
            throw new WorkbenchException(400, "invalid_query_syntax",
                $"Predicate node of kind '{node.Kind}' has no children.");
        }
        var acc = BuildBody(node.Children[0], param, componentType);
        for (var i = 1; i < node.Children.Length; i++)
        {
            var rhs = BuildBody(node.Children[i], param, componentType);
            acc = Expression.MakeBinary(combineOp, acc, rhs);
        }
        return acc;
    }

    private static Expression BuildCmp(PredicateNodeDto node, ParameterExpression param, Type componentType)
    {
        // No `node.Component` vs `componentType.Name` check: the user can reference a component by its registered
        // name (the [Component("...")] attribute string, often namespaced — "Typhon.Workbench.Fixture.Player"),
        // its CLR short name ("CompPlayer"), or its CLR full name — the compiler's ResolveComponentTable accepts
        // all three and maps to the same ComponentTable. By the time we reach this builder, AssertSingleComponent
        // has already enforced single-component WHERE and the compiler has dispatched to BuildPredicate<T> with
        // the correct T. Trust the resolution; just build the lambda for `componentType`.
        var member = ResolveMember(componentType, node.Field, param);
        var value = CoerceValue(node.Value, member.Type);
        var rhs = Expression.Constant(value, member.Type);

        return node.Op switch
        {
            "==" => Expression.Equal(member, rhs),
            "!=" => Expression.NotEqual(member, rhs),
            ">" => Expression.GreaterThan(member, rhs),
            "<" => Expression.LessThan(member, rhs),
            ">=" => Expression.GreaterThanOrEqual(member, rhs),
            "<=" => Expression.LessThanOrEqual(member, rhs),
            _ => throw new WorkbenchException(400, "invalid_query_syntax",
                $"Unknown comparison operator '{node.Op}'. Expected one of: ==, !=, >, <, >=, <=."),
        };
    }

    private static MemberExpression ResolveMember(Type componentType, string fieldName, ParameterExpression param)
    {
        // Try field first (component types are unmanaged structs — fields are the norm), then property as a fallback.
        var field = componentType.GetField(fieldName);
        if (field != null)
        {
            return Expression.Field(param, field);
        }
        var prop = componentType.GetProperty(fieldName);
        if (prop != null)
        {
            return Expression.Property(param, prop);
        }
        throw new WorkbenchException(400, "invalid_field",
            $"Component '{componentType.Name}' has no field or property '{fieldName}'.");
    }

    /// <summary>
    /// Coerce a parser-emitted value (long / double / string / bool) to the target field's CLR type. Handles:
    /// numeric widening / narrowing (long → int / float / double / etc.), bool passthrough, enum-by-name (parser emits
    /// the identifier as string; we <see cref="Enum.Parse"/> here), string-to-<c>String64</c>-style structs is a
    /// future-need but unsupported in Phase 1 (most String64-indexed fields are byte-equality, which works with raw
    /// string today via the engine's expression parser path; we revisit if dogfooding shows otherwise).
    /// </summary>
    private static object CoerceValue(object value, Type targetType)
    {
        if (value == null)
        {
            return null;
        }
        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }
        var nonNullable = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // Enum-as-name (parser stored the identifier string).
        if (nonNullable.IsEnum && value is string s)
        {
            try
            {
                return Enum.Parse(nonNullable, s, ignoreCase: false);
            }
            catch (ArgumentException ex)
            {
                throw new WorkbenchException(400, "invalid_query_syntax",
                    $"Cannot convert identifier '{s}' to enum '{nonNullable.Name}'. {ex.Message}");
            }
        }

        // Numeric coercion. Convert.ChangeType handles all the .NET primitive widening + narrowing rules; we accept any
        // arithmetic loss at user-input time (the engine will compare on the narrowed value).
        try
        {
            return Convert.ChangeType(value, nonNullable, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException)
        {
            throw new WorkbenchException(400, "invalid_query_syntax",
                $"Cannot convert value '{value}' (type {value.GetType().Name}) to '{targetType.Name}'.");
        }
    }
}
