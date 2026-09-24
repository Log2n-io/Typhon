using System;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// The primitive a projected value is read as, once the compiler has resolved the declaration's selector against the component's measured layout.
/// </summary>
/// <remarks>
/// This is the column loop's <i>read</i> axis, the way <see cref="CodecKind"/> is its <i>encode</i> axis: both are chosen once per column and become struct
/// type parameters, so neither costs a branch per entity. It is deliberately a small closed enum rather than a <see cref="Type"/> — a type would have to be
/// switched on at tick time, which is exactly what compiling exists to remove.
/// </remarks>
internal enum ProjectionSourceType : byte
{
    /// <summary>Unset — no source was resolved.</summary>
    None = 0,

    /// <summary>A one-byte <see cref="bool"/>.</summary>
    Boolean,

    /// <summary>A signed byte.</summary>
    SByte,

    /// <summary>An unsigned byte, which is also how a byte-backed enum reads.</summary>
    Byte,

    /// <summary>A signed 16-bit integer.</summary>
    Int16,

    /// <summary>An unsigned 16-bit integer.</summary>
    UInt16,

    /// <summary>A signed 32-bit integer.</summary>
    Int32,

    /// <summary>An unsigned 32-bit integer.</summary>
    UInt32,

    /// <summary>A signed 64-bit integer. It never reaches the wire as 64 bits; the codec narrows it and the registry made that narrowing explicit.</summary>
    Int64,

    /// <summary>An unsigned 64-bit integer, narrowed the same way.</summary>
    UInt64,

    /// <summary>An IEEE single.</summary>
    Single,

    /// <summary>An IEEE double.</summary>
    Double,
}

/// <summary>
/// One declared field, resolved: where its bytes are in a cluster, how they are read, how they are quantized, and where the result belongs on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>The offset pair is the whole point.</b> <see cref="ComponentOffsetInCluster"/> comes from <see cref="ArchetypeClusterInfo.ComponentOffset"/> and
/// <see cref="FieldOffsetInComponent"/> from <c>DBComponentDefinition.Field.OffsetInComponentStorage</c>. Together with
/// <see cref="ComponentSize"/> — the column's stride — they address slot <c>i</c>'s value at
/// <c>ComponentOffsetInCluster + i * ComponentSize + FieldOffsetInComponent</c>, which is the archetype's own cluster layout and nothing else (SUB-01).
/// </para>
/// <para>
/// A value type, held in an array, because the projection pass walks these one after another per block: an array of references would chase a pointer per
/// column for data that is read once and thrown away.
/// </para>
/// </remarks>
internal readonly struct CompiledField
{
    /// <summary>The name this field travels under.</summary>
    public string Name { get; init; }

    /// <summary>The component's slot in the archetype, from <see cref="ArchetypeMetadata.GetSlot"/>.</summary>
    public byte ComponentSlot { get; init; }

    /// <summary>Byte offset of the component's column from the cluster base, from <see cref="ArchetypeClusterInfo.ComponentOffset"/>.</summary>
    public int ComponentOffsetInCluster { get; init; }

    /// <summary>The column's stride: the component's storage size, from <see cref="ArchetypeClusterInfo.ComponentSize"/>.</summary>
    public int ComponentSize { get; init; }

    /// <summary>Byte offset of the value inside one component, from <c>DBComponentDefinition.Field.OffsetInComponentStorage</c>.</summary>
    public int FieldOffsetInComponent { get; init; }

    /// <summary>Byte offset of a <c>Fraction</c>'s denominator inside the same component, or <c>-1</c> when the field is not a ratio.</summary>
    public int RatioOffsetInComponent { get; init; }

    /// <summary>How the value is read out of the column.</summary>
    public ProjectionSourceType SourceType { get; init; }

    /// <summary>The wire codec, with every parameter the engine fills already filled.</summary>
    public CatalogCodec Codec { get; init; }

    /// <summary>The codec's kind, hoisted out of <see cref="Codec"/> so the per-column dispatch reads one field.</summary>
    public CodecKind CodecKind { get; init; }

    /// <summary>The codec's width in bits for a quantizing kind, or the bit count of a packed kind.</summary>
    public int CodecBits { get; init; }

    /// <summary>
    /// The CLR enum whose names are this field's catalog value set (W13), or <see langword="null"/> when the field carries none.
    /// </summary>
    /// <remarks>
    /// <b>The one piece of metadata that has to survive compilation.</b> An <c>enum</c> is an attribute on an integer codec, not a codec of its own, so the
    /// catalog needs both the key (<c>EnumType.Name</c>) and the value set (<c>Enum.GetNames</c>) when it is built from the plan. Carrying the type rather
    /// than the names keeps the decision about how they are spelled with the catalog builder, and a <see cref="Type"/> handle invokes no user code — the
    /// plan's "no reflection survives" rule is about the per-entity path, which never reads this.
    /// </remarks>
    public Type EnumType { get; init; }

    /// <summary>
    /// Whether the declaration marked this integer codec saturating — <c>Codec.Saturate()</c>, required of any 64-bit source, and exported as the codec's
    /// <c>!</c> in the catalog. Dropping it here would build a catalog whose <c>varu</c> no longer says the narrowing was explicit.
    /// </summary>
    public bool Saturating { get; init; }

    /// <summary>Lowest code an integer kind accepts; the clamp is the codec's range, not the source's.</summary>
    public double CodeMin { get; init; }

    /// <summary>Highest code an integer kind accepts.</summary>
    public double CodeMax { get; init; }

    /// <summary>Lower bound of a <c>quant</c> codec, inclusive.</summary>
    public double QuantMin { get; init; }

    /// <summary>Upper bound of a <c>quant</c> codec, exclusive.</summary>
    public double QuantMax { get; init; }

    /// <summary>The wire section: <c>0</c> for the <c>onEnter</c> section, <c>1 + groupBit</c> for a change group (W11).</summary>
    public int Section { get; init; }

    /// <summary>The change group's bit in its section's mask, or <c>-1</c> for an <c>onEnter</c> field, which belongs to no group (W15).</summary>
    public int GroupBit { get; init; }

    /// <summary>The field's position in its side's flat wire-order list — public or owner — so a store indexes its columns with no lookup by name.</summary>
    public int Ordinal { get; init; }

    /// <summary>Whether the field lives in its section's leading bit pack: a <c>bits</c> or a <c>bool</c> (W12).</summary>
    public bool Packed { get; init; }

    /// <summary>For a packed field, its first bit within its section's pack.</summary>
    public int BitOffset { get; init; }

    /// <summary>For a packed field, its width in bits; <c>1</c> for a <c>bool</c>.</summary>
    public int BitCount { get; init; }

    /// <summary>Bytes this field contributes to a byte-aligned body; <c>0</c> for a packed field, an upper bound for a variable-length codec.</summary>
    public int MaxBodyBytes { get; init; }

    /// <summary>Whether the field belongs to the archetype's owner section, which has its own bit space (W17).</summary>
    public bool Owner { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} @ +{ComponentOffsetInCluster}/{ComponentSize}+{FieldOffsetInComponent} as {Codec?.Type}";
}

/// <summary>
/// One wire section: a contiguous run of <see cref="CompiledProjectionPlan.Fields"/> (or of its owner list) with its leading bit pack.
/// </summary>
/// <remarks>
/// A section is the unit of encoding <i>and</i> the unit of selection, which is what lets S2 concatenate the group bodies S1 encoded once: one implicit pack
/// per section at its start, so no pack ever spans two groups (W12).
/// </remarks>
internal readonly struct CompiledSection
{
    /// <summary>Index of the section's first field in the flat wire-order list.</summary>
    public int FirstField { get; init; }

    /// <summary>How many fields the section holds.</summary>
    public int FieldCount { get; init; }

    /// <summary>How many of those lead the section as packed fields.</summary>
    public int PackedCount { get; init; }

    /// <summary>Bytes of the leading pack: ⌈Σn / 8⌉, <c>0</c> when no field is packed.</summary>
    public int PackBytes { get; init; }

    /// <summary>The section's body size in bytes: its pack plus every byte-aligned field, an upper bound where a codec is variable-length.</summary>
    public int MaxBodyBytes { get; init; }
}

/// <summary>
/// One change group: fields that change together, sharing a change tick and a bit of their side's mask.
/// </summary>
/// <remarks>
/// <b>Canonical order, not declaration order.</b> The bit is the group's index in the ordinal sort of the group names, which is what the catalog exports and
/// what a client's mask means (W14). Reordering a declaration therefore changes nothing on the wire.
/// </remarks>
internal readonly struct CompiledGroup
{
    /// <summary>The group's name.</summary>
    public string Name { get; init; }

    /// <summary>The group's bit in its side's <c>u8</c> mask: its index in canonical order.</summary>
    public int Bit { get; init; }

    /// <summary>
    /// The group's slot in <see cref="ReplicationHotEntry.GroupTicks"/>. Motion takes slot <c>0</c> when the archetype has any, so the public groups start
    /// above it; <c>-1</c> for an owner group, whose data lives in the block's owner entry rather than in the hot entry.
    /// </summary>
    public int TickSlot { get; init; }

    /// <summary>The fields that change together, as a section of the flat wire-order list.</summary>
    public CompiledSection Section { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} (bit {Bit}, tick slot {TickSlot})";
}

/// <summary>
/// An archetype's position, resolved: where the spatial value lives in a cluster, and the two codecs a segment is built from.
/// </summary>
/// <remarks>
/// <para>
/// A position is not a field (W15/W16): it travels only in an enter record and in motion segments, it belongs to no change group, and its bounds come from
/// the spatial grid rather than from the declaration. The velocity codec's width <i>and</i> its <c>quantaDiv</c> are derived here together, once, from the
/// archetype's teleport speed — see <see cref="ProjectionCompiler.VelocityCodec"/>.
/// </para>
/// <para>
/// A class rather than a struct: one per archetype, read once per cluster at most, and wide enough that copying it by value would buy nothing.
/// </para>
/// </remarks>
internal sealed class CompiledPosition
{
    /// <summary>Whether the position travels as motion segments rather than as one value on enter.</summary>
    public bool Moving { get; init; }

    /// <summary>Whether a segment carries a velocity — the <c>linear</c> model.</summary>
    public bool Linear { get; init; }

    /// <summary>Axes on the wire: 2 or 3.</summary>
    public int Dims { get; init; }

    /// <summary>The position component's slot in the archetype.</summary>
    public byte ComponentSlot { get; init; }

    /// <summary>Byte offset of the position component's column from the cluster base.</summary>
    public int ComponentOffsetInCluster { get; init; }

    /// <summary>The position column's stride.</summary>
    public int ComponentSize { get; init; }

    /// <summary>Byte offset of the spatial value inside the component — the component's <c>[SpatialIndex]</c> field.</summary>
    public int FieldOffsetInComponent { get; init; }

    /// <summary>The shape of the spatial value: a bounding box, a sphere or a point, which decides how a position is taken from it.</summary>
    public SpatialFieldType SpatialFieldType { get; init; }

    /// <summary>The <c>pos2</c> / <c>pos3</c> codec, with the grid's bounds.</summary>
    public CatalogCodec Pos { get; init; }

    /// <summary>The <c>vel2</c> / <c>vel3</c> codec with its derived width, or <see langword="null"/> when the model carries no velocity.</summary>
    public CatalogCodec Vel { get; init; }

    /// <summary>The position quantum on each axis, computed exactly as <see cref="WireMath.QuantStep"/> does.</summary>
    public double[] PositionStep { get; init; }

    /// <summary>The finest axis step, which is the one the velocity width has to cover.</summary>
    public double FinestPositionStep { get; init; }

    /// <summary>The extrapolation error, in metres, at which a new segment is sent. Server policy: it never reaches the catalog.</summary>
    public double ToleranceMetres { get; init; }

    /// <summary>The teleport threshold in metres per second. Server policy, and the number the velocity width is derived from.</summary>
    public double TeleportMaxSpeedMps { get; init; }

    /// <summary>How long a moving entity may keep one segment, in seconds.</summary>
    public double MaxAgeSeconds { get; init; }

    /// <summary>Whether the width was sized for the nominal tick period only, ignoring overload time dilation.</summary>
    public bool IgnoresTickDilation { get; init; }

    /// <summary>The tick multiplier the width was sized for: the runtime's largest allowed one, or <c>1</c> under the opt-out.</summary>
    public int SizedForTickMultiplier { get; init; }

    /// <summary>The velocity component's slot when <c>VelocityFrom</c> named one, otherwise <c>0xFF</c>.</summary>
    public byte VelocityComponentSlot { get; init; }

    /// <summary>Byte offset of the declared velocity column from the cluster base, or <c>-1</c> when the velocity is measured.</summary>
    public int VelocityComponentOffsetInCluster { get; init; }

    /// <summary>The declared velocity column's stride, or <c>0</c> when the velocity is measured.</summary>
    public int VelocityComponentSize { get; init; }

    /// <summary>Byte offset of the declared velocity value inside its component, or <c>-1</c> when the velocity is measured.</summary>
    public int VelocityFieldOffsetInComponent { get; init; }

    /// <summary>Whether <c>VelocityFrom</c> replaced the measurement.</summary>
    public bool VelocityIsDeclared => VelocityFieldOffsetInComponent >= 0;

    /// <summary>
    /// Bytes of one pre-encoded motion segment: <c>p0</c> (dims × pos bytes), <c>v</c> when linear (dims × vel bytes), <c>t0</c> (2) and <c>epoch</c> (1).
    /// </summary>
    public int SegmentBytes { get; init; }
}

/// <summary>
/// Everything the projection pass needs to walk one archetype, resolved at <c>Start</c> and never touched again.
/// </summary>
/// <remarks>
/// <para>
/// <b>No reflection and no delegate survive compilation.</b> A declaration names a component handle and a selector; what comes out here is a pair of byte
/// offsets, a codec kind and a group bit. Nothing in this object can invoke user code, which is what makes the per-entity pass a loop over integers.
/// </para>
/// <para>
/// <b>It holds the cluster layout and nothing else of the engine</b> (SUB-01). No component segment, no entity location, no per-archetype engine state: the
/// only thing replication needs in order to read a value is the layout every other cluster reader uses, and holding anything more would make replication a
/// second reader of the storage with its own idea of where a value is.
/// </para>
/// </remarks>
internal sealed class CompiledProjectionPlan
{
    /// <summary>The archetype's wire name.</summary>
    public string Name { get; init; }

    /// <summary>The archetype's CLR type, for diagnostics and for the catalog builder's lookup.</summary>
    public Type ArchetypeType { get; init; }

    /// <summary>The archetype's process-global catalog id.</summary>
    public ushort ArchetypeCatalogId { get; init; }

    /// <summary>The order the projection was declared in. The wire index is canonical and is assigned when the catalog is built.</summary>
    public int DeclarationIndex { get; init; }

    /// <summary>Whether the archetype was declared static: its record is sent once, on enter, and never updated.</summary>
    public bool IsStatic { get; init; }

    /// <summary>The archetype's cluster layout — the one structure replication reads component values through.</summary>
    public ArchetypeClusterInfo ClusterLayout { get; init; }

    /// <summary>The archetype's cluster slot count, <c>N</c>.</summary>
    public int SlotCount { get; init; }

    /// <summary>The public fields in canonical wire order: <c>onEnter</c> section first, then each group's, packed fields leading each section (W11).</summary>
    public CompiledField[] Fields { get; init; }

    /// <summary>The <c>onEnter</c> section, which is section 0 of <see cref="Fields"/>.</summary>
    public CompiledSection OnEnter { get; init; }

    /// <summary>The public change groups in canonical order; bit <c>i</c> of a state record's mask is <c>Groups[i]</c> (W14).</summary>
    public CompiledGroup[] Groups { get; init; }

    /// <summary>The owner fields in canonical wire order, with their own ordinals.</summary>
    public CompiledField[] OwnerFields { get; init; }

    /// <summary>The owner change groups, in their own bit space (W17).</summary>
    public CompiledGroup[] OwnerGroups { get; init; }

    /// <summary>The position, or <see langword="null"/> when the archetype declared none.</summary>
    public CompiledPosition Position { get; init; }

    /// <summary>
    /// The block layout: the hot, cold and owner entry offsets a block of this archetype is carved into — with v̂'s bytes in the cold entry when the
    /// archetype's visibility slack is above zero (<see cref="VisibilitySlackM"/>).
    /// </summary>
    public ReplicationBlockLayout BlockLayout { get; init; }

    /// <summary>
    /// The archetype's visibility slack <c>h_A</c> in metres (09 § 2): v̂ moves to the entity's position only when that position is more than this far
    /// from it. Zero is exact — v̂ is the last projected position, every tick. Resolved by the compiler from the profiles that observe the archetype.
    /// </summary>
    public double VisibilitySlackM { get; init; }

    /// <summary>Bytes one owner entry reserves — the owner sections' bodies — or <c>0</c> when the archetype declares none.</summary>
    public int OwnerEntrySize { get; init; }

    /// <summary>
    /// The widest state body the public groups can produce, which is what <see cref="ReplicationHotEntry.PackedState"/> has to hold.
    /// </summary>
    public int MaxStateBodyBytes { get; init; }

    /// <summary>How many of the four <see cref="ReplicationHotEntry.GroupTicks"/> slots this archetype uses, the motion segment's included.</summary>
    public int TickSlotCount { get; init; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Name}: {Fields.Length} field(s) in {Groups.Length} group(s), {OwnerFields.Length} owner field(s), N={SlotCount}";
}
