using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Typhon.Schema.Definition;

/// <summary>
/// Detects component types whose MANAGED layout — the one the engine reads them through (<c>*(T*)</c>, <c>Span&lt;T&gt;</c>, <c>ref T</c>) — may differ from
/// their MARSHALLED layout, which is all that <see cref="Marshal.OffsetOf(Type, string)"/> can report.
/// </summary>
/// <remarks>
/// <para>
/// The two agree for every ordinary blittable primitive and diverge for exactly two types: <c>bool</c> is 1 byte managed and 4 marshalled, and <c>char</c> is
/// 2 and 1 under the default <c>CharSet.Ansi</c>. Anywhere either appears, offsets read by reflection may describe a different layout from the one the bytes
/// are addressed by — and for <c>char</c> the two can total the SAME size, so no size comparison at any layer detects it.
/// </para>
/// <para>
/// Presence of such a field is a proxy for divergence, not a proof of it, so the cases where the declaration itself reconciles the two layouts are excluded:
/// <c>CharSet.Unicode</c> (a char then marshals as 2 bytes, like managed), an explicit <c>[MarshalAs]</c> (the width is the author's to define),
/// <c>LayoutKind.Explicit</c> (offsets are declared outright, so both layouts report what the author wrote), and a <c>fixed</c> buffer (element count times
/// element width in either layout). Everything left over is refused.
/// </para>
/// <para>
/// Lives in the schema contract assembly because the engine's definition builder and the CLI's assembly schema loader must apply the SAME rule. A second,
/// shallower copy of this policy in one of them is how the hole reopens (#819, rule SCHEMA-07).
/// </para>
/// </remarks>
[PublicAPI]
public static class LayoutDivergence
{
    /// <summary>
    /// Depth cap for the walk. Value types cannot contain themselves, so the graph is finite; this bounds a pathologically deep one rather than guarding a
    /// cycle. Reaching it reports divergence: a layout this code could not finish inspecting is not one to vouch for, and the whole premise here is refusing
    /// what cannot be verified.
    /// </summary>
    private const int MaxDepth = 16;

    /// <summary>Sentinel reported as the divergence <c>kind</c> for a type too deeply nested to finish inspecting.</summary>
    public const string UnverifiableNesting = "unverifiable nesting";

    /// <summary>
    /// Searches <paramref name="type"/> for a field whose managed and marshalled representations may differ, at any nesting depth.
    /// </summary>
    /// <param name="type">The component's CLR type.</param>
    /// <param name="path">Receives a dotted path to the offending field, for diagnostics; <see langword="null"/> when none is found.</param>
    /// <param name="kind">Receives <c>"bool"</c>, <c>"char"</c> or <see cref="UnverifiableNesting"/>; <see langword="null"/> when none is found.</param>
    /// <returns><see langword="true"/> when the type's reflected offsets must not be trusted.</returns>
    /// <remarks>
    /// Walks the CLR type's instance fields rather than a schema's field list, and includes non-public ones. A nested struct the schema does not model is
    /// dropped from that list yet still occupies bytes, and therefore still displaces every field declared after it — scanning the schema's own view would
    /// look straight past it.
    /// </remarks>
    public static bool Detect(Type type, out string path, out string kind) => Detect(type, marshalsCharAsTwoBytes: false, 0, out path, out kind);

    private static bool Detect(Type type, bool marshalsCharAsTwoBytes, int depth, out string path, out string kind)
    {
        path = null;
        kind = null;

        if (type == null)
        {
            return false;
        }

        if (type == typeof(bool))
        {
            path = string.Empty;
            kind = "bool";
            return true;
        }

        if (type == typeof(char))
        {
            if (marshalsCharAsTwoBytes)
            {
                return false;
            }

            path = string.Empty;
            kind = "char";
            return true;
        }

        // Other primitives, enums (whose underlying type is a non-divergent primitive), pointers and reference types have nothing to walk into.
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum || type.IsPointer)
        {
            return false;
        }

        var layout = ReadStructLayout(type, out var charSetMarshalsAsTwoBytes);

        // Explicit layout means every offset is declared, and Marshal.OffsetOf reports exactly what was declared — the same value the managed layout uses.
        if (layout == LayoutKind.Explicit)
        {
            return false;
        }

        if (depth >= MaxDepth)
        {
            path = "…";
            kind = UnverifiableNesting;
            return true;
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            // [MarshalAs] hands the marshalled width to the author; modelling every UnmanagedType is not something to guess at, and second-guessing a
            // deliberate annotation is worse than trusting it.
            if (field.IsDefined(typeof(MarshalAsAttribute), inherit: false))
            {
                continue;
            }

            // A `fixed` buffer occupies element-count times element-width in BOTH layouts, so it neither diverges itself nor displaces what follows. Its field
            // type is a compiler-generated struct wrapping one element, which the walk would otherwise descend into and mistake for a bare char.
            if (field.IsDefined(typeof(FixedBufferAttribute), inherit: false))
            {
                continue;
            }

            if (!Detect(field.FieldType, charSetMarshalsAsTwoBytes, depth + 1, out var nested, out kind))
            {
                continue;
            }

            path = string.IsNullOrEmpty(nested) ? Unmangle(field.Name) : $"{Unmangle(field.Name)}.{nested}";
            return true;
        }

        kind = null;
        return false;
    }

    /// <summary>Reads the type's <see cref="StructLayoutAttribute"/>, defaulting to what C# emits for a struct: sequential, ANSI.</summary>
    private static LayoutKind ReadStructLayout(Type type, out bool charMarshalsAsTwoBytes)
    {
        var attribute = type.StructLayoutAttribute;
        if (attribute == null)
        {
            charMarshalsAsTwoBytes = false;
            return LayoutKind.Sequential;
        }

        // CharSet.Unicode marshals a char as 2 bytes — the managed width — and Auto resolves to Unicode on every platform .NET Core supports.
        charMarshalsAsTwoBytes = attribute.CharSet is CharSet.Unicode or CharSet.Auto;
        return attribute.Value;
    }

    /// <summary>Renders an auto-property's backing field as the property name, so a diagnostic reads <c>Position</c> rather than <c>&lt;Position&gt;k__BackingField</c>.</summary>
    private static string Unmangle(string fieldName)
    {
        if (fieldName.Length > 0 && fieldName[0] == '<')
        {
            var end = fieldName.IndexOf('>');
            if (end > 1)
            {
                return fieldName.Substring(1, end - 1);
            }
        }

        return fieldName;
    }
}
