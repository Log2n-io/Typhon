using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// The geometry of one push implementation (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 3.5, L6): the event it records and the cell key
/// it files events under. <see cref="PushReplication{TEvent}"/> is instantiated once per implementation, so the JIT compiles each without the other's axis.
/// </summary>
/// <remarks>
/// Every member is read through a <c>ref</c> of the struct, never an <c>in</c> or <c>ref readonly</c>: a call through a type parameter on a readonly reference
/// copies the struct first, which on a 48 or 64 B event is the cost the layout exists to avoid.
/// </remarks>
internal interface IPushEvent<TSelf> where TSelf : unmanaged, IPushEvent<TSelf>
{
    /// <summary>Whether this is the deep implementation: three axes, tile-ordered keys.</summary>
    static abstract bool Deep { get; }

    /// <summary>A cell's key: ascending keys are the implementation's cell order.</summary>
    static abstract ulong Key(int cx, int cy, int cz);

    static abstract int KeyX(ulong key);

    static abstract int KeyY(ulong key);

    static abstract int KeyZ(ulong key);

    /// <summary>
    /// The probe unit a key belongs to — a row in the flat implementation, a tile of 4³ cells in the deep one. A unit's cells are contiguous in key
    /// order.
    /// </summary>
    static abstract ulong Unit(ulong key);

    /// <summary>The unit of the probe-unit coordinates (<c>ux, uy, uz</c>): a row is (0, cy, 0), a tile (cx / 4, cy / 4, cz / 4).</summary>
    static abstract ulong UnitOf(int ux, int uy, int uz);

    /// <summary>The first key of a unit.</summary>
    static abstract ulong UnitFirstKey(ulong unit);

    /// <summary>
    /// A key compressed to the grid's used bits, order-preserving — what the index's radix sort runs on. <paramref name="bitsX"/> and <paramref name="bitsY"/>
    /// are the bits of the compressed X and Y coordinates (cells in the flat implementation, tiles in the deep one).
    /// </summary>
    static abstract ulong Compress(ulong key, int bitsX, int bitsY);

    /// <summary>The inverse of <see cref="Compress"/>.</summary>
    static abstract ulong Expand(ulong compressed, int bitsX, int bitsY);

    nint Block { get; set; }

    ulong OldKey { get; set; }

    ulong NewKey { get; set; }

    float OldX { get; set; }

    float OldY { get; set; }

    /// <summary>The old position's geometric z: always 0 in the flat implementation (10 § 3.4).</summary>
    float OldZ { get; set; }

    float NewX { get; set; }

    float NewY { get; set; }

    float NewZ { get; set; }

    uint NetId { get; set; }

    ushort Archetype { get; set; }

    byte Slot { get; set; }

    byte Flags { get; set; }

    /// <summary>The change groups this tick stamped — what an update to a session that already holds the entity carries.</summary>
    byte Groups { get; set; }

    /// <summary>Distance LOD: with <see cref="PushEvent.FarFlush"/>, the groups changed since the entity's previous far flush.</summary>
    byte FlushGroups { get; set; }
}

/// <summary>
/// One projected slot of a push archetype this tick, with where it was and where it is: the flat implementation's event (10 § 3.5), 48 bytes. The unit the
/// frame stage fans out.
/// </summary>
/// <remarks>
/// Positions are the DECODED quantized positions — exactly what the wire carries — so every path that tests a distance (the push step, the sweep, a cell
/// delivery) tests the same number and the geometric known-set is exact rather than approximately consistent.
/// </remarks>
internal struct PushEvent : IPushEvent<PushEvent>
{
    public const byte HasOld = 1;
    public const byte HasNew = 2;
    public const byte Segment = 4;

    /// <summary>Set by the projection on an arrival by migration: never dropped as a no-op, so the entity's latest event names the slot it is in now.</summary>
    public const byte Arrived = 8;

    /// <summary>
    /// Distance LOD: this is the entity's far flush — its phase tick — and <see cref="FlushGroups"/> (with <see cref="FlushSegment"/>) is the union of what
    /// changed since its previous one. A session that holds the entity far sends that union; any other treats the event as it would without the flag.
    /// </summary>
    public const byte FarFlush = 16;

    /// <summary>Distance LOD: the far flush carries the motion segment.</summary>
    public const byte FlushSegment = 32;

    // Laid out to 48 bytes, a key's 42 bits in eight and six bytes: the struct is copied into the index and read by every session that reaches its cell.
    private nint _block;

    // The cells of the old and new positions as packed keys, computed once by the projecting worker so no session re-derives them.
    private ulong _oldKey;
    private float _oldX;
    private float _oldY;
    private float _newX;
    private float _newY;
    private uint _netId;
    private uint _newKeyLow;
    private ushort _newKeyHigh;
    private ushort _archetype;
    private byte _slot;
    private byte _flags;
    private byte _groups;
    private byte _flushGroups;

    private const int AxisBits = 21;
    private const ulong AxisMask = (1UL << AxisBits) - 1;

    public static bool Deep => false;

    /// <summary><c>(cy &lt;&lt; 21) | cx</c>: ascending keys are row-major order, and a row is <c>key &gt;&gt; 21</c>. A flat grid has no z.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Key(int cx, int cy, int cz) => ((ulong)(uint)cy << AxisBits) | (uint)cx;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int KeyX(ulong key) => (int)(key & AxisMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int KeyY(ulong key) => (int)((key >> AxisBits) & AxisMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int KeyZ(ulong key) => 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Unit(ulong key) => key >> AxisBits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong UnitOf(int ux, int uy, int uz) => (uint)uy;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong UnitFirstKey(ulong unit) => unit << AxisBits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Compress(ulong key, int bitsX, int bitsY) => ((ulong)(uint)KeyY(key) << bitsX) | (uint)KeyX(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Expand(ulong compressed, int bitsX, int bitsY) =>
        Key((int)(compressed & ((1UL << bitsX) - 1)), (int)(compressed >> bitsX), 0);

    public nint Block
    {
        readonly get => _block;
        set => _block = value;
    }

    public ulong OldKey
    {
        readonly get => _oldKey;
        set => _oldKey = value;
    }

    public ulong NewKey
    {
        readonly get => _newKeyLow | ((ulong)_newKeyHigh << 32);
        set
        {
            _newKeyLow = (uint)value;
            _newKeyHigh = (ushort)(value >> 32);
        }
    }

    public float OldX
    {
        readonly get => _oldX;
        set => _oldX = value;
    }

    public float OldY
    {
        readonly get => _oldY;
        set => _oldY = value;
    }

    public float OldZ
    {
        readonly get => 0f;
        set { }
    }

    public float NewX
    {
        readonly get => _newX;
        set => _newX = value;
    }

    public float NewY
    {
        readonly get => _newY;
        set => _newY = value;
    }

    public float NewZ
    {
        readonly get => 0f;
        set { }
    }

    public uint NetId
    {
        readonly get => _netId;
        set => _netId = value;
    }

    public ushort Archetype
    {
        readonly get => _archetype;
        set => _archetype = value;
    }

    public byte Slot
    {
        readonly get => _slot;
        set => _slot = value;
    }

    public byte Flags
    {
        readonly get => _flags;
        set => _flags = value;
    }

    public byte Groups
    {
        readonly get => _groups;
        set => _groups = value;
    }

    public byte FlushGroups
    {
        readonly get => _flushGroups;
        set => _flushGroups = value;
    }

    /// <summary>The primary cell: the new position's, or the old one's for a leave-only event.</summary>
    public readonly ulong PrimaryKey => (_flags & HasNew) != 0 ? NewKey : _oldKey;
}

/// <summary>
/// The deep implementation's event (10 § 3.5): three decoded axes and two 63-bit tile-ordered keys, in one 64-byte cache line.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct PushEvent3 : IPushEvent<PushEvent3>
{
    private nint _block;
    private ulong _oldKey;
    private ulong _newKey;
    private float _oldX;
    private float _oldY;
    private float _oldZ;
    private float _newX;
    private float _newY;
    private float _newZ;
    private uint _netId;
    private ushort _archetype;
    private byte _slot;
    private byte _flags;
    private byte _groups;
    private byte _flushGroups;

    // The key: tiles of 4³ cells major, so a tile's cells are contiguous — (tileZ << 38 | tileY << 19 | tileX) << 6 | z & 3 << 4 | y & 3 << 2 | x & 3.
    // 19 bits of tile per axis, 21 bits of cell: the spatial VDB key's width.
    private const int TileBits = 19;
    private const ulong TileMask = (1UL << TileBits) - 1;

    public static bool Deep => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Key(int cx, int cy, int cz)
    {
        var tile = ((ulong)((uint)cz >> 2) << (2 * TileBits)) | ((ulong)((uint)cy >> 2) << TileBits) | ((uint)cx >> 2);
        return (tile << 6) | (uint)(((cz & 3) << 4) | ((cy & 3) << 2) | (cx & 3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int KeyX(ulong key) => (int)((((key >> 6) & TileMask) << 2) | (key & 3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int KeyY(ulong key) => (int)((((key >> (6 + TileBits)) & TileMask) << 2) | ((key >> 2) & 3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int KeyZ(ulong key) => (int)(((key >> (6 + (2 * TileBits))) << 2) | ((key >> 4) & 3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Unit(ulong key) => key >> 6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong UnitOf(int ux, int uy, int uz) => ((ulong)(uint)uz << (2 * TileBits)) | ((ulong)(uint)uy << TileBits) | (uint)ux;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong UnitFirstKey(ulong unit) => unit << 6;

    // The tile coordinates compressed to the grid's tile bits (Z takes what is left), the six in-tile bits below them.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Compress(ulong key, int bitsX, int bitsY)
    {
        var tile = key >> 6;
        var tx = tile & TileMask;
        var ty = (tile >> TileBits) & TileMask;
        var tz = tile >> (2 * TileBits);
        return (((((tz << bitsY) | ty) << bitsX) | tx) << 6) | (key & 63);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Expand(ulong compressed, int bitsX, int bitsY)
    {
        var tile = compressed >> 6;
        var tx = tile & ((1UL << bitsX) - 1);
        var ty = (tile >> bitsX) & ((1UL << bitsY) - 1);
        var tz = tile >> (bitsX + bitsY);
        return (((tz << (2 * TileBits)) | (ty << TileBits) | tx) << 6) | (compressed & 63);
    }

    public nint Block
    {
        readonly get => _block;
        set => _block = value;
    }

    public ulong OldKey
    {
        readonly get => _oldKey;
        set => _oldKey = value;
    }

    public ulong NewKey
    {
        readonly get => _newKey;
        set => _newKey = value;
    }

    public float OldX
    {
        readonly get => _oldX;
        set => _oldX = value;
    }

    public float OldY
    {
        readonly get => _oldY;
        set => _oldY = value;
    }

    public float OldZ
    {
        readonly get => _oldZ;
        set => _oldZ = value;
    }

    public float NewX
    {
        readonly get => _newX;
        set => _newX = value;
    }

    public float NewY
    {
        readonly get => _newY;
        set => _newY = value;
    }

    public float NewZ
    {
        readonly get => _newZ;
        set => _newZ = value;
    }

    public uint NetId
    {
        readonly get => _netId;
        set => _netId = value;
    }

    public ushort Archetype
    {
        readonly get => _archetype;
        set => _archetype = value;
    }

    public byte Slot
    {
        readonly get => _slot;
        set => _slot = value;
    }

    public byte Flags
    {
        readonly get => _flags;
        set => _flags = value;
    }

    public byte Groups
    {
        readonly get => _groups;
        set => _groups = value;
    }

    public byte FlushGroups
    {
        readonly get => _flushGroups;
        set => _flushGroups = value;
    }
}
