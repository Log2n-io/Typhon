using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Recognises the inline text types a message struct may carry a <c>str</c> in, for the event and command binders.
/// </summary>
/// <remarks>
/// <para>
/// Both binders ask the same question of a field's CLR type — "is this an inline UTF-8 text, and how many bytes does it
/// hold?" — and both ask it once per declared field at <c>Start</c>. The answer comes from
/// <see cref="IInlineUtf8Text"/>'s static <c>Capacity</c>, so a capacity added to the protocol is understood here with no
/// change: the alternative, a list of known types, would be a second place to edit and a silent refusal when someone
/// forgot.
/// </para>
/// <para>Cached because reflection on a static abstract member is not free and a catalog may declare the same type on
/// several messages; the map is tiny and never invalidated, since a type's capacity cannot change.</para>
/// </remarks>
internal static class MessageText
{
    private static readonly ConcurrentDictionary<Type, int?> Capacities = new();

    /// <summary>The capacity of an inline text type, or <see langword="null"/> when the type is not one.</summary>
    /// <param name="type">A message field's CLR type.</param>
    /// <returns>Its capacity in UTF-8 bytes, or <see langword="null"/>.</returns>
    public static int? CapacityOf(Type type) => Capacities.GetOrAdd(type, Resolve);

    private static int? Resolve(Type type)
    {
        // No null check: ConcurrentDictionary.GetOrAdd throws on a null key before the factory is ever reached.
        if (!typeof(IInlineUtf8Text).IsAssignableFrom(type))
        {
            return null;
        }

        // The interface's own Capacity, reached through the interface map: the type declares it explicitly, so the public
        // static property a caller sees is the type's `const`, and this is the one the contract guarantees.
        var map = type.GetInterfaceMap(typeof(IInlineUtf8Text));
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            // Paired by index, which is the contract GetInterfaceMap guarantees: InterfaceMethods[i] is implemented by
            // TargetMethods[i]. Matching on a mangled target name instead happens to work and is not promised.
            if (map.InterfaceMethods[i].Name == "get_Capacity" && map.TargetMethods[i].IsStatic)
            {
                var capacity = (int)map.TargetMethods[i].Invoke(null, null);
                RequireBinderLayout(type, capacity);
                return capacity;
            }
        }

        return null;
    }

    /// <summary>
    /// Refuses, at <c>Start</c>, an inline text type whose byte layout is not the one both binders read and write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CommandRegistry"/>'s <c>StoreText</c> and <see cref="EventHub"/>'s <c>LoadText</c> address a text field
    /// as <c>ushort</c> length at the field's offset followed by its bytes. <see cref="IInlineUtf8Text"/> promises only
    /// <c>Capacity</c> and <c>Length</c> — not that layout — while the type's own remarks invite new capacities "in a
    /// handful of lines". One declared with an <c>int</c> length, or with its buffer before its length, would compile,
    /// bind, and then read and write a neighbouring field: silent corruption, on the ingress path, found by nobody.
    /// </para>
    /// <para>
    /// Checked here rather than made virtual because the alternative — putting store and load on the interface — puts an
    /// indirect call on the hot path to guard against a type that does not exist yet. A refusal at Start costs nothing
    /// and is this engine's usual answer to a load-bearing assumption.
    /// </para>
    /// </remarks>
    private static void RequireBinderLayout(Type type, int capacity)
    {
        var size = Marshal.SizeOf(type);
        var expected = capacity + sizeof(ushort);
        if (size != expected)
        {
            throw new InvalidOperationException(
                $"Inline text type '{type.Name}' has capacity {capacity} and measures {size} bytes; the command and event " +
                $"binders address it as a ushort length followed by {capacity} bytes, which is {expected}. Give it that " +
                "layout, or teach both binders the new one.");
        }

        FieldInfo length = null;
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (field.FieldType == typeof(ushort))
            {
                length = field;
                break;
            }
        }

        if (length is null || (int)Marshal.OffsetOf(type, length.Name) != 0)
        {
            throw new InvalidOperationException(
                $"Inline text type '{type.Name}' must hold its length as a ushort at offset 0: the command and event " +
                "binders read and write it there.");
        }
    }

    /// <summary>The name to put in a refusal, so it says what to use rather than only what is wrong.</summary>
    public static string KnownTypes => nameof(Utf8Text256);

}
