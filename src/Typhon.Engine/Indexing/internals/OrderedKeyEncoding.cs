using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Converts a typed B+Tree key to the universal order-preserving <see cref="long"/> encoding the K-way merge compares on.
/// </summary>
/// <remarks>
/// <para>
/// The encoding must be a strictly monotonic map from the key's own ordering onto <see cref="long"/>, because that is the only property the merge relies on:
/// it never decodes a key, it only compares two of them. Unsigned types get their high bit flipped so they do not straddle zero; floating point goes through
/// the same sign-flip <see cref="ZoneMapArray"/> uses, so a zone map and a merge order the same values identically.
/// </para>
/// <para>
/// Lifted out of <c>ArchetypeSortedStream</c> when the streaming page cursor started producing already-encoded keys: the conversion now happens inside the
/// B+Tree layer, while the copy out of a leaf is already in flight, rather than a second pass over the same entries.
/// </para>
/// </remarks>
internal static class OrderedKeyEncoding
{
    /// <summary>Encodes <paramref name="key"/> so that <c>a &lt; b</c> in the key's own order implies <c>Encode(a) &lt; Encode(b)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Encode<TKey>(TKey key, KeyType keyType) where TKey : unmanaged
    {
        switch (keyType)
        {
            case KeyType.Float:
                return ZoneMapArray.FloatToOrderedLong(Unsafe.As<TKey, float>(ref key));
            case KeyType.Double:
                return ZoneMapArray.DoubleToOrderedLong(Unsafe.As<TKey, double>(ref key));
            case KeyType.UShort:
                return Unsafe.As<TKey, ushort>(ref key) ^ (1L << 15);
            case KeyType.UInt:
                return Unsafe.As<TKey, uint>(ref key) ^ (1L << 31);
            case KeyType.ULong:
                return Unsafe.As<TKey, long>(ref key) ^ long.MinValue;
            case KeyType.Byte:
                return Unsafe.As<TKey, byte>(ref key);
            default:
                // Signed integer types (sbyte, short, int, long): direct widening preserves order
                return keyType switch
                {
                    KeyType.SByte => Unsafe.As<TKey, sbyte>(ref key),
                    KeyType.Short => Unsafe.As<TKey, short>(ref key),
                    KeyType.Int => Unsafe.As<TKey, int>(ref key),
                    KeyType.Long => Unsafe.As<TKey, long>(ref key),
                    _ => 0
                };
        }
    }

    /// <summary>
    /// Reinterprets a key's raw bits out of the <see cref="long"/> slot they were parked in. This is NOT <see cref="Encode{TKey}"/>'s inverse and must never be
    /// confused with it.
    /// </summary>
    /// <remarks>
    /// A parked cursor has to remember two keys — where the range ends and which key it last emitted — across a call boundary that has no <c>TKey</c> in scope.
    /// Storing the ORDERED encoding would force an exact inverse for every key type, one more bijection to get right and to keep right. Storing the raw bits
    /// needs no inverse at all: every supported key type is 8 bytes or fewer, so the bits round-trip by definition. The ordered encoding stays what it is
    /// for — comparison — and never has to be undone.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TKey FromRawBits<TKey>(long bits) where TKey : unmanaged => Unsafe.As<long, TKey>(ref bits);

    /// <summary>Parks a key's raw bits in a <see cref="long"/>. Pairs with <see cref="FromRawBits{TKey}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToRawBits<TKey>(TKey key) where TKey : unmanaged
    {
        long bits = 0;
        Unsafe.As<long, TKey>(ref bits) = key;
        return bits;
    }
}
