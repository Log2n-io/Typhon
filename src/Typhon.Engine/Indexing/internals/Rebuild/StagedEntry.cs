using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// One index entry of a sorted rebuild (#1588, design <c>Indexing/sorted-index-rebuild.md</c> §D3): the key's raw bytes as the cluster holds them, and the
/// cluster location of the entity holding it.
/// </summary>
/// <remarks>
/// <c>Pack = 4</c>: 8 bytes for a key of up to 4 bytes, 12 for an 8-byte key, 68 for String64. The raw bits are kept rather than the radix key because the
/// radix key is not invertible: NaN payloads and the sign of zero map together, and the tree stores the bits the data holds.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct StagedEntry<TKey> where TKey : unmanaged
{
    public TKey Key;
    public int Location;

    public StagedEntry(TKey key, int location)
    {
        Key = key;
        Location = location;
    }
}

/// <summary>
/// The order of a sorted rebuild over <typeparamref name="TKey"/>: the tree's own key order (<see cref="BTree{TKey,TStore}.RadixKeyOf"/>, or an unsigned
/// byte compare for String64), then the cluster location. Every member folds per instantiation, as <c>RadixKeyOf</c> does.
/// </summary>
/// <remarks>
/// The location tie-break makes the order total, and so the unique winner deterministic: of entries with equal keys, the lowest location comes first
/// (RB-07, §D5).
/// </remarks>
internal static class StagedOrder<TKey> where TKey : unmanaged
{
    /// <summary>True for every key with a radix order (integers, <c>char</c>, both floats); false for String64, which compares as bytes.</summary>
    public static bool HasRadixKey
        => typeof(TKey) == typeof(int) || typeof(TKey) == typeof(long) || typeof(TKey) == typeof(short) || typeof(TKey) == typeof(sbyte)
            || typeof(TKey) == typeof(uint) || typeof(TKey) == typeof(ulong) || typeof(TKey) == typeof(ushort) || typeof(TKey) == typeof(byte)
            || typeof(TKey) == typeof(float) || typeof(TKey) == typeof(double) || typeof(TKey) == typeof(char);

    /// <summary>
    /// True for every key this order is exact for: the radix keys and String64. Any other would compare as all-equal (<c>RadixKeyOf</c> returns 0 for a
    /// type it does not know), and a unique tree would keep one entry and count the rest as conflicts; the rebuild refuses it instead.
    /// </summary>
    public static bool IsSupported => HasRadixKey || typeof(TKey) == typeof(String64);

    /// <summary>The bytes one entry takes.</summary>
    public static int Stride => Unsafe.SizeOf<StagedEntry<TKey>>();

    /// <summary>The radix key of <paramref name="entry"/>'s key; 0 for String64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong RadixKey(in StagedEntry<TKey> entry) => BTree<TKey, PersistentStore>.RadixKeyOf(entry.Key);

    /// <summary>Compares the keys only, in the tree's order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareKeys(in StagedEntry<TKey> a, in StagedEntry<TKey> b)
    {
        if (HasRadixKey)
        {
            return RadixKey(in a).CompareTo(RadixKey(in b));
        }

        return KeyBytes(in a).SequenceCompareTo(KeyBytes(in b));
    }

    /// <summary>The total order: keys, then locations.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compare(in StagedEntry<TKey> a, in StagedEntry<TKey> b)
    {
        var c = CompareKeys(in a, in b);
        return c != 0 ? c : a.Location.CompareTo(b.Location);
    }

    /// <summary>True when <paramref name="entries"/> is already in <see cref="Compare"/> order, which then needs no sort (a ledger's sequence
    /// number).</summary>
    public static bool IsOrdered(ReadOnlySpan<StagedEntry<TKey>> entries)
    {
        for (var i = 1; i < entries.Length; i++)
        {
            if (Compare(in entries[i - 1], in entries[i]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Sorts <paramref name="entries"/> into <see cref="Compare"/> order. Returns false, touching nothing, when they already were.
    /// </summary>
    /// <param name="entries">The entries, appended in ascending location order by the stage.</param>
    /// <param name="scratch">The radix sort's ping-pong partner, at least as long as <paramref name="entries"/>; unused for String64.</param>
    /// <param name="counts">The radix sort's histogram, at least <see cref="RadixSort.Buckets"/> ints; unused for String64.</param>
    /// <remarks>
    /// A numeric key takes the stable radix sort on the key alone: the stage appends in location order, so stability yields the location tie-break with no
    /// second key. Callers whose input is not in location order must not rely on that, and none do. String64 takes an introsort on the total order.
    /// </remarks>
    public static bool Sort(Span<StagedEntry<TKey>> entries, Span<StagedEntry<TKey>> scratch, Span<int> counts)
    {
        if (IsOrdered(entries))
        {
            return false;
        }

        if (HasRadixKey)
        {
            RadixSort.Sort<StagedEntry<TKey>, EntryRadixKey>(entries, scratch, counts);
        }
        else
        {
            entries.Sort(default(EntryComparer));
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> KeyBytes(in StagedEntry<TKey> entry)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in entry.Key), 1));

    private readonly struct EntryRadixKey : IRadixKey<StagedEntry<TKey>>
    {
        public static ulong Key(in StagedEntry<TKey> item) => RadixKey(in item);
    }

    private readonly struct EntryComparer : IComparer<StagedEntry<TKey>>
    {
        public int Compare(StagedEntry<TKey> x, StagedEntry<TKey> y) => StagedOrder<TKey>.Compare(in x, in y);
    }
}
