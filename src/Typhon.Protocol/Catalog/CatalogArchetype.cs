using System.Text.Json.Serialization;

namespace Typhon.Protocol;

/// <summary>
/// An archetype's position on the wire (W15, W16): what an enter record starts with and what a motion segment carries. Absent when the archetype is not
/// spatial.
/// </summary>
/// <remarks>
/// It carries only what a client decodes. The extrapolation tolerance and the teleport speed are server policy no client reads; hashing them would re-send
/// the catalog to every client on every tuning change. A segment always carries <c>t0</c> (<c>tickLo</c>) and <c>epoch</c> (<c>u8</c>): they are protocol
/// constants, not catalog entries.
/// </remarks>
public sealed class CatalogPosition
{
    /// <summary>The kind for an archetype that moves: segments are replicated.</summary>
    public const string MotionKind = "motion";

    /// <summary>The kind for an archetype that never moves: its position is sent once, on enter.</summary>
    public const string StaticKind = "static";

    /// <summary>The model for linear extrapolation: a segment carries a velocity.</summary>
    public const string LinearModel = "linear";

    /// <summary>The model without extrapolation: a segment carries a position only, and the client interpolates between samples.</summary>
    public const string NoneModel = "none";

    /// <summary><see cref="MotionKind"/> or <see cref="StaticKind"/>.</summary>
    public string Kind { get; init; }

    /// <summary><see cref="LinearModel"/> or <see cref="NoneModel"/>, for a moving archetype only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Model { get; init; }

    /// <summary>The position codec: <see cref="CodecKind.Pos2"/> or <see cref="CodecKind.Pos3"/>.</summary>
    public CatalogCodec Pos { get; init; }

    /// <summary>The velocity codec — <see cref="CodecKind.Vel2"/> or <see cref="CodecKind.Vel3"/>, matching <see cref="Pos"/> — for the linear model.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CatalogCodec Vel { get; init; }
}

/// <summary>
/// One replicated field: its wire name, its codec, and either the change group it belongs to or the fact that it travels on enter only.
/// </summary>
/// <remarks>
/// The name is the one the projection declared, never a CLR member name — renaming a C# field must not be a wire break. No storage offset appears here: a
/// client decodes a byte stream, and the server's memory layout is meaningless to it.
/// </remarks>
public sealed class CatalogField
{
    /// <summary>The declared wire name, unique within its archetype, event or command.</summary>
    public string Name { get; init; }

    /// <summary>How the value is encoded.</summary>
    public CatalogCodec Codec { get; init; }

    /// <summary>
    /// The change group this field belongs to; a session receives only the groups that changed. Exactly one of this and <see cref="OnEnter"/> is set on an
    /// archetype field; neither is set on an event or command field.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Group { get; init; }

    /// <summary>The field travels in enter records only and never changes while the entity is in view (W15).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OnEnter { get; init; }

    /// <summary>The enum table naming this field's values; allowed on <c>bits</c>, <c>u8</c>, <c>u16</c> and <c>varu</c> (W13).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Enum { get; init; }

    /// <summary>How a client should present this field between updates, for example <c>snap</c>. Absent means the client decides.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Smoothing { get; init; }
}

/// <summary>
/// The owner-only part of an archetype (W17): values only the controlling session receives, in <c>SELF</c>, with their own groups and bit space.
/// </summary>
/// <remarks>
/// A separate section rather than a visibility flag on groups, so owner data is unreachable from <c>ENTITIES</c> by construction: confusing the two bit spaces
/// cannot leak one player's owner values to another. The names are public — everyone receives the catalog — only the values are owner-scoped.
/// </remarks>
public sealed class CatalogOwner
{
    /// <summary>The owner change groups, 1 to 8; canonical order assigns their bits in <c>SELF</c>'s mask.</summary>
    public string[] Groups { get; init; }

    /// <summary>The owner fields; each names an owner group.</summary>
    public CatalogField[] Fields { get; init; }
}

/// <summary>
/// One replicated archetype: the entities of this type a session can see, and what it receives for them.
/// </summary>
public sealed class CatalogArchetype
{
    /// <summary>
    /// The wire index for this archetype. Assigned by canonicalization from the ordinal sort of <see cref="Name"/>, never from declaration order — see
    /// <see cref="CatalogSerializer.Canonicalize"/>.
    /// </summary>
    public int Idx { get; init; }

    /// <summary>The declared wire name.</summary>
    public string Name { get; init; }

    /// <summary>The public change groups, at most eight; canonical order assigns bit i of a state record's mask to the i-th group (W14).</summary>
    public string[] Groups { get; init; }

    /// <summary>Where and how the archetype's entities are positioned; absent when it is not spatial.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CatalogPosition Position { get; init; }

    /// <summary>The public fields, in wire order once canonical: onEnter section, then each group's; packed fields first in a section (W11).</summary>
    public CatalogField[] Fields { get; init; }

    /// <summary>The owner-only section; absent when the archetype has none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CatalogOwner Owner { get; init; }
}
