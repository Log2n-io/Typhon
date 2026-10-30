using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Typhon.Analyzers;

/// <summary>
/// Shared helpers for Typhon dispose analyzers (TYPHON004 through TYPHON007, TYPHON014): which types own a resource only their <c>Dispose</c> releases,
/// and which must not be copied.
/// </summary>
/// <remarks>
/// <para>
/// A type is <b>critical</b> (must be disposed) when it is marked <c>[MustDispose]</c>, or when it has a <c>Dispose</c> — <c>IDisposable</c> or, for a
/// ref struct, the pattern method — and holds a critical type in an instance field <i>by value</i>. A reference-typed field is not followed: a class
/// held by reference may be borrowed (a query borrows its transaction), and only its owner disposes it. A struct held by value cannot be borrowed — a copy
/// is a copy — so its container owns it.
/// </para>
/// <para>
/// A struct is <b>no-copy</b> when it is marked <c>[NoCopy]</c> or holds a no-copy struct in an instance field by value, at any depth: copying the outer
/// copies the inner.
/// </para>
/// <para>
/// Both inferences read fields. A referencing project compiles against the engine's reference assembly, which keeps a struct's private fields but drops a
/// class's; TYPHON014 therefore requires the markers wherever the inference finds an owner in source, so every caller reads ownership from the
/// declaration. The match was a fixed list of names before, and named <c>Typhon.Engine.ChunkAccessor</c> long after the type moved to
/// <c>Typhon.Engine.Internals</c> (#329): TYPHON004–007 checked nothing for it until #1538.
/// </para>
/// </remarks>
sealed class OwnershipCatalog
{
    /// <summary>Where <c>[MustDispose]</c>, <c>[NoCopy]</c> and <c>[AllowCopy]</c> are declared.</summary>
    public const string AttributeNamespace = "Typhon.Engine.Internals";

    private const int MaxDepth = 8;

    private readonly ConcurrentDictionary<ITypeSymbol, string> _critical = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ITypeSymbol, string> _noCopy = new(SymbolEqualityComparer.Default);
    private readonly INamedTypeSymbol _disposable;

    public OwnershipCatalog(Compilation compilation) => _disposable = compilation.GetTypeByMetadataName("System.IDisposable");

    /// <summary>
    /// Whether <paramref name="type"/> must be disposed. <paramref name="consequence"/> is the suffix for a diagnostic message — the marker's reason, or
    /// the field through which it owns a marked resource.
    /// </summary>
    public bool IsCritical(ITypeSymbol type, out string consequence)
    {
        consequence = CriticalConsequence(type, 0);
        return consequence != null;
    }

    public bool IsCritical(ITypeSymbol type) => CriticalConsequence(type, 0) != null;

    /// <summary>Whether a struct of <paramref name="type"/> must not be copied; <paramref name="reason"/> is the message suffix.</summary>
    public bool IsNoCopy(ITypeSymbol type, out string reason)
    {
        reason = NoCopyReason(type, 0);
        return reason != null;
    }

    public bool IsNoCopy(ITypeSymbol type) => NoCopyReason(type, 0) != null;

    /// <summary>
    /// The instance field through which <paramref name="type"/> owns a critical resource (by value), or null — independent of whether the type is marked:
    /// TYPHON014's question.
    /// </summary>
    public IFieldSymbol FieldOwningCritical(INamedTypeSymbol type, out string consequence)
    {
        foreach (var field in OwnedValueFields(type))
        {
            consequence = CriticalConsequence(field.Type, 1);
            if (consequence != null)
            {
                return field;
            }
        }

        consequence = null;
        return null;
    }

    /// <summary>The instance field through which a struct holds a no-copy struct by value, or null.</summary>
    public IFieldSymbol FieldHoldingNoCopy(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct)
        {
            return null;
        }

        foreach (var field in OwnedValueFields(type))
        {
            if (NoCopyReason(field.Type, 1) != null)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="type"/> has a <c>Dispose</c>: it implements <c>IDisposable</c>, or declares the parameterless pattern method.</summary>
    public bool HasDispose(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        if (_disposable != null)
        {
            foreach (var i in named.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(i, _disposable))
                {
                    return true;
                }
            }
        }

        foreach (var member in named.GetMembers("Dispose"))
        {
            if (member is IMethodSymbol { IsStatic: false, Parameters.Length: 0, ReturnsVoid: true })
            {
                return true;
            }
        }

        return false;
    }

    private string CriticalConsequence(ITypeSymbol type, int depth)
    {
        if (type is not INamedTypeSymbol named || depth > MaxDepth || named.TypeKind is not (TypeKind.Struct or TypeKind.Class))
        {
            return null;
        }

        var key = named.OriginalDefinition;
        if (_critical.TryGetValue(key, out var cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        string result = null;
        if (TryGetMarker(key, "MustDisposeAttribute", out var reason))
        {
            result = reason;
        }
        else if (HasDispose(key))
        {
            foreach (var field in OwnedValueFields(key))
            {
                var inner = CriticalConsequence(field.Type, depth + 1);
                if (inner != null)
                {
                    result = $" - owns a {field.Type.Name} through '{field.Name}'{inner}";
                    break;
                }
            }
        }

        _critical.TryAdd(key, result ?? "");
        return result;
    }

    private string NoCopyReason(ITypeSymbol type, int depth)
    {
        if (type is not INamedTypeSymbol named || depth > MaxDepth || named.TypeKind != TypeKind.Struct)
        {
            return null;
        }

        var key = named.OriginalDefinition;
        if (_noCopy.TryGetValue(key, out var cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        // No placeholder entry while computing: analysis runs concurrently, and another thread would read it as "not no-copy". A struct cannot contain
        // itself by value; MaxDepth stops the walk on invalid source the analyzer still runs on.
        string result = null;
        if (TryGetMarker(key, "NoCopyAttribute", out var reason))
        {
            result = reason;
        }
        else
        {
            foreach (var field in OwnedValueFields(key))
            {
                var inner = NoCopyReason(field.Type, depth + 1);
                if (inner != null)
                {
                    result = $" — holds a {field.Type.Name} by value ('{field.Name}'){inner}";
                    break;
                }
            }
        }

        _noCopy.TryAdd(key, result ?? "");
        return result;
    }

    /// <summary>Instance fields held by value: not static, not <c>ref</c> (a ref field borrows), of a value type. Auto-property backing fields count.</summary>
    private static IEnumerable<IFieldSymbol> OwnedValueFields(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is IFieldSymbol { IsStatic: false, RefKind: RefKind.None, IsConst: false } field && field.Type.IsValueType)
            {
                yield return field;
            }
        }
    }

    /// <summary>Whether <paramref name="type"/> carries the Typhon marker <paramref name="attributeName"/>; <paramref name="reason"/> formats its Reason.</summary>
    public static bool TryGetMarker(ISymbol type, string attributeName, out string reason)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.Name == attributeName && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace)
            {
                reason = "";
                foreach (var namedArg in attr.NamedArguments)
                {
                    if (namedArg.Key == "Reason" && namedArg.Value.Value is string text && text.Length > 0)
                    {
                        reason = " — " + text;
                    }
                }
                return true;
            }
        }

        reason = null;
        return false;
    }
}

/// <summary>Field discovery shared by TYPHON005–007.</summary>
static class DisposableAnalyzerHelpers
{
    /// <summary>
    /// The instance fields of <paramref name="type"/> holding a critical type — by value, or a reference to a critical class (a field of type
    /// <c>Transaction</c> is its owner's to dispose unless suppressed as borrowed). Ref fields are excluded: borrowed references, the caller owns the
    /// resource.
    /// </summary>
    public static List<(IFieldSymbol Field, string TypeFullName, string Consequence)> FindCriticalFields(INamedTypeSymbol type, OwnershipCatalog catalog)
    {
        var result = new List<(IFieldSymbol, string, string)>();

        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol field || field.IsStatic || field.IsImplicitlyDeclared || field.RefKind != RefKind.None)
            {
                continue;
            }

            if (catalog.IsCritical(field.Type, out var consequence))
            {
                result.Add((field, field.Type.ToDisplayString(), consequence));
            }
        }

        return result;
    }
}
