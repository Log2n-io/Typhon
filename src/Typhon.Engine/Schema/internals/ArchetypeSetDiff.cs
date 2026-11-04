using System;
using System.Text;

namespace Typhon.Engine.Internals;

/// <summary>
/// The slot-by-slot correspondence between an archetype's persisted component list and its runtime one, matched by component schema NAME, never by
/// index (SCHEMA-11). Pure: no engine dependency.
/// </summary>
/// <remarks>
/// <para>
/// A set change, a reorder and a parent change are all one thing here: a name map. A same-count swap ({A,B} → {A,C}) is a removal plus an addition, not an
/// identity, which is precisely what comparing counts used to miss.
/// </para>
/// <para>
/// The caller resolves persisted names through the rename journal (SCHEMA-04) before calling <see cref="Compute"/>, so a renamed component is the same
/// name on both sides. An archetype holds at most 16 components (one bit per slot in the enabled masks).
/// </para>
/// </remarks>
internal sealed class ArchetypeSetDiff
{
    /// <summary>A slot with no counterpart: an added runtime slot in <see cref="NewToOld"/>, a removed persisted slot in <see cref="OldToNew"/>.</summary>
    public const byte NoSlot = 0xFF;

    /// <summary>The most slots an archetype can hold (the width of an enabled-bits mask).</summary>
    public const int MaxSlots = 16;

    private ArchetypeSetDiff(string[] persisted, string[] runtime, byte[] newToOld, byte[] oldToNew, ushort addedMask, ushort removedOldMask)
    {
        PersistedNames = persisted;
        RuntimeNames = runtime;
        NewToOld = newToOld;
        OldToNew = oldToNew;
        AddedMask = addedMask;
        RemovedOldMask = removedOldMask;

        var identical = persisted.Length == runtime.Length;
        for (var s = 0; identical && s < newToOld.Length; s++)
        {
            identical = newToOld[s] == s;
        }
        IsIdentical = identical;
        IsReorderOnly = !identical && addedMask == 0 && removedOldMask == 0;
    }

    /// <summary>Rename-resolved persisted names, in persisted slot order.</summary>
    public string[] PersistedNames { get; }

    /// <summary>Runtime names, in runtime slot order.</summary>
    public string[] RuntimeNames { get; }

    /// <summary>For each runtime slot, its persisted slot, or <see cref="NoSlot"/> when the component is added.</summary>
    public byte[] NewToOld { get; }

    /// <summary>For each persisted slot, its runtime slot, or <see cref="NoSlot"/> when the component is removed.</summary>
    public byte[] OldToNew { get; }

    /// <summary>Runtime slots whose component the persisted list does not hold.</summary>
    public ushort AddedMask { get; }

    /// <summary>Persisted slots whose component the runtime list does not hold.</summary>
    public ushort RemovedOldMask { get; }

    /// <summary>Same names in the same order.</summary>
    public bool IsIdentical { get; }

    /// <summary>Same set of names, in a different order.</summary>
    public bool IsReorderOnly { get; }

    /// <summary>Whether the runtime list drops a persisted component.</summary>
    public bool HasRemovals => RemovedOldMask != 0;

    /// <summary>
    /// Re-expresses a per-slot mask in persisted numbering as runtime numbering: a surviving component's bit moves to its runtime slot, a removed
    /// component's bit is dropped, and an added slot's bit is clear.
    /// </summary>
    public ushort RemapEnabledBits(ushort oldMask)
    {
        ushort result = 0;
        for (var s = 0; s < NewToOld.Length; s++)
        {
            var o = NewToOld[s];
            if (o != NoSlot && (oldMask & (1 << o)) != 0)
            {
                result |= (ushort)(1 << s);
            }
        }
        return result;
    }

    /// <summary>
    /// A one-line account of the change, for messages and logs: <c>+Gear at slot 2; -Old from slot 1; Vitals 2-&gt;3</c>, or <c>no change</c>.
    /// </summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        for (var s = 0; s < NewToOld.Length; s++)
        {
            if (NewToOld[s] == NoSlot)
            {
                Append(sb, $"+{RuntimeNames[s]} at slot {s}");
            }
        }
        for (var o = 0; o < OldToNew.Length; o++)
        {
            if (OldToNew[o] == NoSlot)
            {
                Append(sb, $"-{PersistedNames[o]} from slot {o}");
            }
        }
        for (var s = 0; s < NewToOld.Length; s++)
        {
            var o = NewToOld[s];
            if (o != NoSlot && o != s)
            {
                Append(sb, $"{RuntimeNames[s]} {o}->{s}");
            }
        }
        return sb.Length == 0 ? "no change" : sb.ToString();

        static void Append(StringBuilder sb, string part)
        {
            if (sb.Length > 0)
            {
                sb.Append("; ");
            }
            sb.Append(part);
        }
    }

    /// <summary>The index of the first name that occurs earlier in <paramref name="names"/> too, or -1 when every name is distinct. Ordinal.</summary>
    public static int FindDuplicate(ReadOnlySpan<string> names)
    {
        for (var i = 1; i < names.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (string.Equals(names[i], names[j], StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>Matches <paramref name="persisted"/> against <paramref name="runtime"/> by name (ordinal).</summary>
    /// <param name="persisted">Rename-resolved persisted names, in persisted slot order.</param>
    /// <param name="runtime">Runtime names, in runtime slot order.</param>
    /// <exception cref="ArgumentException">Either list holds more than <see cref="MaxSlots"/> names, or a name twice.</exception>
    public static ArchetypeSetDiff Compute(ReadOnlySpan<string> persisted, ReadOnlySpan<string> runtime)
    {
        if (persisted.Length > MaxSlots)
        {
            throw new ArgumentException($"An archetype holds at most {MaxSlots} components; the persisted list has {persisted.Length}.", nameof(persisted));
        }
        if (runtime.Length > MaxSlots)
        {
            throw new ArgumentException($"An archetype holds at most {MaxSlots} components; the runtime list has {runtime.Length}.", nameof(runtime));
        }

        var dup = FindDuplicate(persisted);
        if (dup >= 0)
        {
            throw new ArgumentException($"The persisted component list holds '{persisted[dup]}' twice.", nameof(persisted));
        }
        dup = FindDuplicate(runtime);
        if (dup >= 0)
        {
            throw new ArgumentException($"The runtime component list holds '{runtime[dup]}' twice.", nameof(runtime));
        }

        var oldToNew = new byte[persisted.Length];
        oldToNew.AsSpan().Fill(NoSlot);
        var newToOld = new byte[runtime.Length];
        ushort addedMask = 0;

        for (var s = 0; s < runtime.Length; s++)
        {
            newToOld[s] = NoSlot;
            for (var o = 0; o < persisted.Length; o++)
            {
                if (string.Equals(runtime[s], persisted[o], StringComparison.Ordinal))
                {
                    newToOld[s] = (byte)o;
                    oldToNew[o] = (byte)s;
                    break;
                }
            }
            if (newToOld[s] == NoSlot)
            {
                addedMask |= (ushort)(1 << s);
            }
        }

        ushort removedOldMask = 0;
        for (var o = 0; o < persisted.Length; o++)
        {
            if (oldToNew[o] == NoSlot)
            {
                removedOldMask |= (ushort)(1 << o);
            }
        }

        return new ArchetypeSetDiff(persisted.ToArray(), runtime.ToArray(), newToOld, oldToNew, addedMask, removedOldMask);
    }
}
