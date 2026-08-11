using System;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Where the keys and values of a B+Tree node are, for the layout an indexed field's type selects.
/// </summary>
/// <remarks>
/// <para>
/// The four node structs share a 20-byte prefix and then diverge, because the high key is 2, 4, 8 or 64 bytes wide and
/// carries the value and key arrays along with it. Which one a tree uses is decided at construction from
/// <c>field.Type</c> (<c>ComponentTable.CreateIndexForFieldCore</c>), so the same mapping — and nothing cleverer —
/// recovers it offline.
/// </para>
/// <para>
/// <b>Offsets come from the structs, not from arithmetic.</b> <see cref="Marshal.OffsetOf"/> against the engine's own
/// <c>IndexNNChunk</c> types means a field moving inside one of them moves this reader with it. Recomputing the same
/// numbers by hand would produce a decoder that stays plausible while silently addressing the wrong bytes — which is
/// the failure this whole family is built to avoid, arrived at from the inside.
/// </para>
/// <para>
/// <b>Keys are stored raw, in the key type's own order.</b> <c>OrderedKeyEncoding</c> exists for the K-way merge and is
/// not what lands on disk, so comparing two stored keys needs the type's own semantics: signed for signed integers,
/// biased for unsigned, IEEE-aware for floating point. <see cref="TryEncodeKey"/> routes through the engine's encoder
/// for exactly that reason — it is a strictly monotonic map from the key's own order onto <see cref="long"/>, which is
/// all an order check needs.
/// </para>
/// </remarks>
internal readonly struct IndexNodeLayout
{
    /// <summary>Node flag marking a leaf, from <c>NodeStates</c>.</summary>
    public const int IsLeafFlag = 0x02;

    private IndexNodeLayout(int capacity, int keysOffset, int valuesOffset, int keySize, int highKeyOffset,
        KeyType keyType, bool isString)
    {
        Capacity = capacity;
        KeysOffset = keysOffset;
        ValuesOffset = valuesOffset;
        KeySize = keySize;
        HighKeyOffset = highKeyOffset;
        KeyType = keyType;
        IsString = isString;
    }

    /// <summary>Entries the node can hold.</summary>
    public int Capacity { get; }

    /// <summary>Byte offset of the key array.</summary>
    public int KeysOffset { get; }

    /// <summary>Byte offset of the value array.</summary>
    public int ValuesOffset { get; }

    /// <summary>Bytes per key.</summary>
    public int KeySize { get; }

    /// <summary>Byte offset of the node's high key.</summary>
    public int HighKeyOffset { get; }

    /// <summary>The key's comparison semantics.</summary>
    public KeyType KeyType { get; }

    /// <summary>Whether keys are fixed 64-byte strings, compared ordinally rather than numerically.</summary>
    public bool IsString { get; }

    /// <summary>Whether a layout was resolved at all.</summary>
    public bool IsUsable => Capacity > 0;

    /// <summary>
    /// The layout an indexed field of this type produces, or an unusable one for a type that cannot be indexed.
    /// </summary>
    /// <param name="fieldType">The indexed field's type.</param>
    public static IndexNodeLayout ForFieldType(FieldType fieldType)
        => fieldType switch
        {
            FieldType.Byte => Narrow(KeyType.Byte),
            FieldType.Short => Narrow(KeyType.Short),
            FieldType.UByte => Narrow(KeyType.Byte),
            FieldType.UShort => Narrow(KeyType.UShort),
            FieldType.Char => Narrow(KeyType.UShort),
            FieldType.Int => Medium(KeyType.Int),
            FieldType.UInt => Medium(KeyType.UInt),
            FieldType.Float => Medium(KeyType.Float),
            FieldType.Long => Wide(KeyType.Long),
            FieldType.ULong => Wide(KeyType.ULong),
            FieldType.Double => Wide(KeyType.Double),
            FieldType.String64 => Text(),
            _ => default
        };

    private static IndexNodeLayout Narrow(KeyType keyType) => new(
        Index16Chunk.Capacity,
        OffsetOf<Index16Chunk>(nameof(Index16Chunk.Keys)),
        OffsetOf<Index16Chunk>(nameof(Index16Chunk.Values)),
        sizeof(short),
        OffsetOf<Index16Chunk>(nameof(Index16Chunk.HighKey)),
        keyType,
        isString: false);

    private static IndexNodeLayout Medium(KeyType keyType) => new(
        Index32Chunk.Capacity,
        OffsetOf<Index32Chunk>(nameof(Index32Chunk.Keys)),
        OffsetOf<Index32Chunk>(nameof(Index32Chunk.Values)),
        sizeof(int),
        OffsetOf<Index32Chunk>(nameof(Index32Chunk.HighKey)),
        keyType,
        isString: false);

    private static IndexNodeLayout Wide(KeyType keyType) => new(
        Index64Chunk.Capacity,
        OffsetOf<Index64Chunk>(nameof(Index64Chunk.Keys)),
        OffsetOf<Index64Chunk>(nameof(Index64Chunk.Values)),
        sizeof(long),
        OffsetOf<Index64Chunk>(nameof(Index64Chunk.HighKey)),
        keyType,
        isString: false);

    private static IndexNodeLayout Text() => new(
        IndexString64Chunk.Capacity,
        OffsetOf<IndexString64Chunk>(nameof(IndexString64Chunk.Keys)),
        OffsetOf<IndexString64Chunk>(nameof(IndexString64Chunk.Values)),
        64,
        OffsetOf<IndexString64Chunk>(nameof(IndexString64Chunk.HighKey)),
        KeyType.Bool,
        isString: true);

    private static int OffsetOf<T>(string field) => Marshal.OffsetOf<T>(field).ToInt32();

    /// <summary>Whether the node's flags mark it a leaf.</summary>
    /// <param name="node">The node's bytes.</param>
    public static bool IsLeaf(ReadOnlySpan<byte> node) => (IndexDirectoryReader.StatesOf(node) & (NodeStates)IsLeafFlag) != 0;

    /// <summary>
    /// The position of logical entry <paramref name="index"/> in the node's circular buffer.
    /// </summary>
    /// <remarks>
    /// Entries are not stored from position 0. A node keeps <c>Start</c> and <c>Count</c> and wraps, so that an insert
    /// at the front costs no shifting — reading the array in physical order returns a rotated sequence, which then
    /// looks like a key-order violation on a perfectly ordered node.
    /// </remarks>
    /// <param name="node">The node's bytes.</param>
    /// <param name="index">The logical entry index.</param>
    public int PhysicalSlot(ReadOnlySpan<byte> node, int index)
    {
        var start = node[2];   // Control byte 2 — see Index64Chunk.Start
        var slot = start + index;
        return slot >= Capacity ? slot - Capacity : slot;
    }

    /// <summary>Reads the value at a logical entry index.</summary>
    /// <param name="node">The node's bytes.</param>
    /// <param name="index">The logical entry index.</param>
    public int ValueAt(ReadOnlySpan<byte> node, int index)
        => MemoryMarshal.Read<int>(node[(ValuesOffset + (PhysicalSlot(node, index) * sizeof(int)))..]);

    /// <summary>The raw key bytes at a logical entry index.</summary>
    /// <param name="node">The node's bytes.</param>
    /// <param name="index">The logical entry index.</param>
    public ReadOnlySpan<byte> KeyAt(ReadOnlySpan<byte> node, int index)
        => node.Slice(KeysOffset + (PhysicalSlot(node, index) * KeySize), KeySize);

    /// <summary>The node's high key bytes — the B-link upper bound.</summary>
    /// <param name="node">The node's bytes.</param>
    public ReadOnlySpan<byte> HighKey(ReadOnlySpan<byte> node) => node.Slice(HighKeyOffset, KeySize);

    /// <summary>
    /// Compares two stored keys in the tree's own order. Negative, zero or positive as usual.
    /// </summary>
    /// <param name="left">First key's raw bytes.</param>
    /// <param name="right">Second key's raw bytes.</param>
    public int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (IsString)
        {
            return left.SequenceCompareTo(right);
        }

        return TryEncodeKey(left).CompareTo(TryEncodeKey(right));
    }

    /// <summary>
    /// Maps a stored key onto a <see cref="long"/> that preserves the key type's own ordering.
    /// </summary>
    /// <remarks>
    /// Routed through <c>OrderedKeyEncoding</c> rather than reimplemented, so unsigned biasing and the float sign-flip
    /// come from the same code the engine orders by. Getting either wrong makes a correct index look unsorted around
    /// zero — a false report that is entirely convincing.
    /// </remarks>
    /// <param name="key">The key's raw bytes.</param>
    public long TryEncodeKey(ReadOnlySpan<byte> key)
        => KeyType switch
        {
            KeyType.Byte => OrderedKeyEncoding.Encode(key[0], KeyType.Byte),
            KeyType.Short => OrderedKeyEncoding.Encode(MemoryMarshal.Read<short>(key), KeyType.Short),
            KeyType.UShort => OrderedKeyEncoding.Encode(MemoryMarshal.Read<ushort>(key), KeyType.UShort),
            KeyType.Int => OrderedKeyEncoding.Encode(MemoryMarshal.Read<int>(key), KeyType.Int),
            KeyType.UInt => OrderedKeyEncoding.Encode(MemoryMarshal.Read<uint>(key), KeyType.UInt),
            KeyType.Float => OrderedKeyEncoding.Encode(MemoryMarshal.Read<float>(key), KeyType.Float),
            KeyType.Long => OrderedKeyEncoding.Encode(MemoryMarshal.Read<long>(key), KeyType.Long),
            KeyType.ULong => OrderedKeyEncoding.Encode(MemoryMarshal.Read<ulong>(key), KeyType.ULong),
            KeyType.Double => OrderedKeyEncoding.Encode(MemoryMarshal.Read<double>(key), KeyType.Double),
            _ => 0
        };

    /// <summary>Renders a key for a finding's detail text, in a form an operator can match against their data.</summary>
    /// <param name="key">The key's raw bytes.</param>
    public string Describe(ReadOnlySpan<byte> key)
    {
        if (IsString)
        {
            var end = key.IndexOf((byte)0);
            return $"\"{System.Text.Encoding.UTF8.GetString(end < 0 ? key : key[..end])}\"";
        }

        return KeyType switch
        {
            KeyType.Float => MemoryMarshal.Read<float>(key).ToString(System.Globalization.CultureInfo.InvariantCulture),
            KeyType.Double => MemoryMarshal.Read<double>(key).ToString(System.Globalization.CultureInfo.InvariantCulture),
            KeyType.ULong => MemoryMarshal.Read<ulong>(key).ToString(),
            KeyType.UInt => MemoryMarshal.Read<uint>(key).ToString(),
            KeyType.UShort => MemoryMarshal.Read<ushort>(key).ToString(),
            _ => TryEncodeKey(key).ToString()
        };
    }
}
