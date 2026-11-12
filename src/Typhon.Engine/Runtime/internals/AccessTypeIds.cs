using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Dense, process-wide ids for the component types DV-01 checks (#1650): a system's declared access compiles to bitsets indexed by these ids, so each check
/// is one array load and a bit test instead of the two <c>HashSet&lt;Type&gt;</c> probes it used to be. Ids are handed out on first sight and never reused;
/// they are unrelated to the schema's component type ids, which belong to one engine.
/// </summary>
internal static class AccessTypeIds
{
    private static readonly ConcurrentDictionary<Type, int> Ids = new();
    private static readonly ConcurrentDictionary<int, Type> Types = new();
    private static int _next = -1;

    /// <summary>The id of <paramref name="type"/>, assigned on first call.</summary>
    public static int Of(Type type) => Ids.GetOrAdd(type, static t =>
    {
        var id = Interlocked.Increment(ref _next);
        Types[id] = t;
        return id;
    });

    /// <summary>The type behind <paramref name="id"/>, for messages.</summary>
    public static Type TypeOf(int id) => Types.TryGetValue(id, out var t) ? t : null;

    /// <summary>True when bit <paramref name="id"/> is set in <paramref name="bits"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Has(ulong[] bits, int id)
    {
        var word = id >> 6;
        return (uint)word < (uint)bits.Length && (bits[word] & (1UL << (id & 63))) != 0;
    }

    /// <summary>Sets bit <paramref name="id"/>, growing <paramref name="bits"/> as needed.</summary>
    public static void Set(ref ulong[] bits, int id)
    {
        var word = id >> 6;
        if (word >= bits.Length)
        {
            Array.Resize(ref bits, word + 1);
        }
        bits[word] |= 1UL << (id & 63);
    }
}

/// <summary>The <see cref="AccessTypeIds"/> id of <typeparamref name="T"/>, resolved once per closed type.</summary>
internal static class AccessTypeId<T>
{
    public static readonly int Value = AccessTypeIds.Of(typeof(T));
}

/// <summary>
/// A <see cref="SystemAccessDescriptor"/> compiled for DV-01: the readable, writable and side-writable component sets as bitsets over
/// <see cref="AccessTypeIds"/>.
/// </summary>
internal sealed class CompiledAccess
{
    /// <summary>Reads, ReadsFresh, ReadsSnapshot, AdditionalReads and Writes: a writer may read what it writes.</summary>
    public readonly ulong[] Readable;

    /// <summary>Writes.</summary>
    public readonly ulong[] Writable;

    /// <summary>SideWrites: readable and writable through a side transaction only.</summary>
    public readonly ulong[] SideWritable;

    public CompiledAccess(SystemAccessDescriptor d)
    {
        ulong[] r = [], w = [], s = [];
        AddAll(ref r, d.Reads);
        AddAll(ref r, d.ReadsFresh);
        AddAll(ref r, d.ReadsSnapshot);
        AddAll(ref r, d.AdditionalReads);
        AddAll(ref r, d.Writes);
        AddAll(ref w, d.Writes);
        AddAll(ref s, d.SideWrites);
        Readable = r;
        Writable = w;
        SideWritable = s;
    }

    private static void AddAll(ref ulong[] bits, HashSet<Type> types)
    {
        foreach (var t in types)
        {
            AccessTypeIds.Set(ref bits, AccessTypeIds.Of(t));
        }
    }
}
