using System.Text.Json.Serialization;

namespace Typhon.Protocol;

/// <summary>
/// How a client extrapolates a position field between segments, and what ends a segment.
/// </summary>
/// <remarks>
/// The engine measures displacement rather than reading a velocity field, so nothing here describes the simulation — only what the client should do with the
/// numbers it receives.
/// </remarks>
public sealed class CatalogMotion
{
    /// <summary>The name of the field carrying measured displacement per tick.</summary>
    public string Velocity { get; init; }

    /// <summary>The extrapolation model, currently <c>linear</c>.</summary>
    public string Model { get; init; }

    /// <summary>Extrapolation error in metres that forces a new segment.</summary>
    public double Tolerance { get; init; }

    /// <summary>How a discontinuity is signalled, currently <c>epoch</c>: a counter the server bumps on every teleport.</summary>
    public string Discontinuity { get; init; }
}

/// <summary>
/// One replicated field: its wire name, its codec, the change group it belongs to, and any decoding hints.
/// </summary>
/// <remarks>
/// The name is the one the projection declared, never a CLR member name — renaming a C# field must not be a wire break. No storage offset appears here: a
/// client decodes a byte stream, and the server's memory layout is meaningless to it.
/// </remarks>
public sealed class CatalogField
{
    /// <summary>The declared wire name, unique within its archetype.</summary>
    public string Name { get; init; }

    /// <summary>How the value is encoded.</summary>
    public CatalogCodec Codec { get; init; }

    /// <summary>The change group this field belongs to; a session receives only the groups that changed. Absent on event and command fields.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Group { get; init; }

    /// <summary>The enum table this field's values are named by, for <see cref="CodecKind.Enum"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Enum { get; init; }

    /// <summary>Motion parameters, present only on a position field that carries segments.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CatalogMotion Motion { get; init; }

    /// <summary>How a client should interpolate this field between updates, for example <c>snap</c>. Absent means the client decides.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Smoothing { get; init; }
}

/// <summary>
/// One replicated archetype: the entities of this type a session can see, and the fields it receives for them.
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

    /// <summary>The change groups, at most four. A field names one of these; a group's index is its bit position in a record's changed mask.</summary>
    public string[] Groups { get; init; }

    /// <summary>The fields, in the order a record encodes them.</summary>
    public CatalogField[] Fields { get; init; }
}
