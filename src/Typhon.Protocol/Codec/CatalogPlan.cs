using System;
using System.Collections.Generic;

namespace Typhon.Protocol;

/// <summary>What a decoded field value is made of.</summary>
public enum FieldValueKind
{
    /// <summary>One to four numbers (<see cref="FieldPlan.Components"/>): integers, quantized scalars, vectors, a quaternion.</summary>
    Number,

    /// <summary>UTF-8 text.</summary>
    Text,

    /// <summary>Raw bytes.</summary>
    Bytes,

    /// <summary>A counted sequence of numeric elements.</summary>
    List,

    /// <summary>A codec this library does not know, skipped by its declared width.</summary>
    Skipped,
}

/// <summary>
/// One field compiled for encoding and decoding: its codec's parameters resolved into the numbers the arithmetic needs, and its place in its section.
/// </summary>
public sealed class FieldPlan
{
    internal FieldPlan(string name, CatalogField field, CatalogCodec codec, Dictionary<string, string[]> enums)
    {
        Name = name;
        Field = field;
        Codec = codec;
        Kind = codec.Kind;
        Packed = CatalogSerializer.IsPacked(Kind);
        BitCount = Kind == CodecKind.Bool ? 1 : Kind == CodecKind.Bits ? codec.N : 0;
        VelocityUnitExp = codec.UnitExp ?? 0;
        (ValueKind, Components) = Kind switch
        {
            CodecKind.Pos2 or CodecKind.Vec2 or CodecKind.Vel2 => (FieldValueKind.Number, 2),
            CodecKind.Pos3 or CodecKind.Vec3 or CodecKind.Vel3 => (FieldValueKind.Number, 3),
            CodecKind.Quat3 => (FieldValueKind.Number, 4),
            CodecKind.Str => (FieldValueKind.Text, 0),
            CodecKind.Bytes or CodecKind.Blob => (FieldValueKind.Bytes, 0),
            CodecKind.List => (FieldValueKind.List, 0),
            CodecKind.Unknown => (FieldValueKind.Skipped, 0),
            _ => (FieldValueKind.Number, 1),
        };

        if (Kind == CodecKind.List)
        {
            // Compilation is the decoder's trust boundary: a list decodes into a bounded stack buffer, so its element must be numeric and its count capped
            // whether or not the catalog went through the validator.
            if (codec.Of == null || !CatalogValidator.IsListElement(codec.Of.Kind) || codec.MaxCount is < 0 or > ProtocolConstants.MaxListCount)
            {
                throw new CatalogException([$"list '{name}' needs a numeric element codec and at most {ProtocolConstants.MaxListCount} elements"]);
            }

            Element = new FieldPlan(name, null, codec.Of);
            Components = Element.Components;
        }

        // A position's quantum is the realm frame's (typhon.3), per frame: RealmFrame.Step.
        if (Kind is CodecKind.Quant)
        {
            QuantStep = new double[Components];
            for (var i = 0; i < Components; i++)
            {
                QuantStep[i] = WireMath.QuantStep(codec.Min[i], codec.Max[i], codec.Bits);
            }
        }

        EnumCount = field?.Enum != null && enums != null && enums.TryGetValue(field.Enum, out var names) ? names.Length : 0;
    }

    internal FieldPlan(string name, CatalogField field, CatalogCodec codec)
        : this(name, field, codec, null)
    {
    }

    /// <summary>The field's wire name.</summary>
    public string Name { get; }

    /// <summary>The catalog field, or <see langword="null"/> for a position codec or a list element.</summary>
    public CatalogField Field { get; }

    /// <summary>The codec.</summary>
    public CatalogCodec Codec { get; }

    /// <summary>The codec kind.</summary>
    public CodecKind Kind { get; }

    /// <summary>Whether the field lives in its section's leading bit pack.</summary>
    public bool Packed { get; }

    /// <summary>For a packed field, its first bit within the pack.</summary>
    public int BitOffset { get; internal set; }

    /// <summary>
    /// The field's position in its owner's flat field list — <see cref="ArchetypePlan.Fields"/>, <see cref="ArchetypePlan.OwnerFields"/> or a message body —
    /// so a store can index its columns without a lookup by name.
    /// </summary>
    public int Ordinal { get; internal set; }

    /// <summary>For a packed field, its width in bits.</summary>
    public int BitCount { get; }

    /// <summary>What a value of this field is made of.</summary>
    public FieldValueKind ValueKind { get; }

    /// <summary>Numbers per value (per element, for a list); 0 for text and bytes.</summary>
    public int Components { get; }

    /// <summary>For a list, the element's plan.</summary>
    public FieldPlan Element { get; }

    /// <summary>For a velocity: the unit's binary exponent — one code is <c>2^VelocityUnitExp</c> metres per tick (W5).</summary>
    public int VelocityUnitExp { get; }

    /// <summary>For a <c>quant</c> codec, its step, computed once exactly as <see cref="WireMath.QuantStep"/> does; <see langword="null"/> otherwise.</summary>
    public double[] QuantStep { get; }

    /// <summary>The number of names of the field's enum, or 0 when it has none; a server refuses a command value at or above it (W13).</summary>
    public int EnumCount { get; }
}

/// <summary>
/// A section compiled for encoding: its fields in wire order (packed first) and the size of its leading pack.
/// </summary>
public sealed class SectionPlan
{
    internal SectionPlan(FieldPlan[] fields)
    {
        Fields = fields;
        var bits = 0;
        foreach (var f in fields)
        {
            if (f.Packed)
            {
                if (PackedCount != Array.IndexOf(fields, f))
                {
                    throw new CatalogException([$"field '{f.Name}' is packed but follows a byte-aligned field: the section is not in wire order (W11)"]);
                }

                f.BitOffset = bits;
                bits += f.BitCount;
                PackedCount++;
            }
        }

        PackBytes = (bits + 7) / 8;
    }

    /// <summary>The fields in wire order; the first <see cref="PackedCount"/> are packed.</summary>
    public FieldPlan[] Fields { get; }

    /// <summary>How many leading fields are packed.</summary>
    public int PackedCount { get; }

    /// <summary>Bytes of the leading pack: ⌈Σn / 8⌉, 0 when no field is packed.</summary>
    public int PackBytes { get; }

    internal static readonly SectionPlan Empty = new([]);
}

/// <summary>An archetype's position, compiled.</summary>
public sealed class PositionPlan
{
    internal PositionPlan(CatalogPosition position)
    {
        Moving = position.Kind == CatalogPosition.MotionKind;
        Linear = Moving && position.Model == CatalogPosition.LinearModel;
        Pos = new FieldPlan("position", null, position.Pos);
        Dims = Pos.Components;
        if (Linear)
        {
            // An absolute unit (W5, typhon.3): nothing of the position codec — whose bounds are a realm's — enters the velocity decode.
            Vel = new FieldPlan("velocity", null, position.Vel);
        }
    }

    /// <summary>Whether segments are replicated (<c>motion</c>) rather than a single position on enter (<c>static</c>).</summary>
    public bool Moving { get; }

    /// <summary>Whether a segment carries a velocity.</summary>
    public bool Linear { get; }

    /// <summary>2 or 3.</summary>
    public int Dims { get; }

    /// <summary>The position codec.</summary>
    public FieldPlan Pos { get; }

    /// <summary>The velocity codec, for the linear model.</summary>
    public FieldPlan Vel { get; }
}

/// <summary>An archetype compiled for encoding and decoding its records.</summary>
public sealed class ArchetypePlan
{
    internal ArchetypePlan(CatalogArchetype archetype, Dictionary<string, string[]> enums)
    {
        Archetype = archetype;
        Idx = archetype.Idx;
        Name = archetype.Name;
        Position = archetype.Position == null ? null : new PositionPlan(archetype.Position);
        Groups = archetype.Groups ?? [];

        if (Groups.Length > ProtocolConstants.MaxGroups || (archetype.Owner?.Groups?.Length ?? 0) > ProtocolConstants.MaxGroups)
        {
            throw new CatalogException([$"archetype '{Name}' has more groups than a u8 mask addresses"]);
        }

        var fields = new List<FieldPlan>();
        OnEnter = BuildSection(archetype.Fields, f => f.OnEnter, fields, enums);
        GroupSections = new SectionPlan[Groups.Length];
        for (var g = 0; g < Groups.Length; g++)
        {
            var group = Groups[g];
            GroupSections[g] = BuildSection(archetype.Fields, f => !f.OnEnter && f.Group == group, fields, enums);
        }

        Fields = fields.ToArray();

        OwnerGroups = archetype.Owner?.Groups ?? [];
        OwnerSections = new SectionPlan[OwnerGroups.Length];
        var ownerFields = new List<FieldPlan>();
        for (var g = 0; g < OwnerGroups.Length; g++)
        {
            var group = OwnerGroups[g];
            OwnerSections[g] = BuildSection(archetype.Owner.Fields, f => f.Group == group, ownerFields, enums);
        }

        OwnerFields = ownerFields.ToArray();
    }

    /// <summary>The catalog archetype.</summary>
    public CatalogArchetype Archetype { get; }

    /// <summary>The wire index.</summary>
    public int Idx { get; }

    /// <summary>The wire name.</summary>
    public string Name { get; }

    /// <summary>The position, or <see langword="null"/> when the archetype is not spatial.</summary>
    public PositionPlan Position { get; }

    /// <summary>The public change groups, in bit order.</summary>
    public string[] Groups { get; }

    /// <summary>The onEnter section.</summary>
    public SectionPlan OnEnter { get; }

    /// <summary>One section per public group, in bit order.</summary>
    public SectionPlan[] GroupSections { get; }

    /// <summary>Every public field, in wire order.</summary>
    public FieldPlan[] Fields { get; }

    /// <summary>The owner change groups, in bit order.</summary>
    public string[] OwnerGroups { get; }

    /// <summary>One section per owner group, in bit order.</summary>
    public SectionPlan[] OwnerSections { get; }

    /// <summary>Every owner field, in wire order.</summary>
    public FieldPlan[] OwnerFields { get; }

    private static SectionPlan BuildSection(
        CatalogField[] declared,
        Func<CatalogField, bool> belongs,
        List<FieldPlan> all,
        Dictionary<string, string[]> enums)
    {
        var fields = new List<FieldPlan>();
        foreach (var f in declared ?? [])
        {
            if (belongs(f))
            {
                var plan = new FieldPlan(f.Name, f, f.Codec, enums);
                plan.Ordinal = all.Count;
                fields.Add(plan);
                all.Add(plan);
            }
        }

        return fields.Count == 0 ? SectionPlan.Empty : new SectionPlan(fields.ToArray());
    }
}

/// <summary>An event or command type compiled: its index and its single body section.</summary>
public sealed class MessagePlan
{
    internal MessagePlan(int idx, string name, CatalogField[] fields, Dictionary<string, string[]> enums)
    {
        Idx = idx;
        Name = name;
        var plans = new FieldPlan[fields?.Length ?? 0];
        for (var i = 0; i < plans.Length; i++)
        {
            plans[i] = new FieldPlan(fields[i].Name, fields[i], fields[i].Codec, enums) { Ordinal = i };
        }

        Body = new SectionPlan(plans);
    }

    /// <summary>The wire index.</summary>
    public int Idx { get; }

    /// <summary>The wire name.</summary>
    public string Name { get; }

    /// <summary>The body, in wire order.</summary>
    public SectionPlan Body { get; }
}

/// <summary>A metric compiled: its codec and how many values it contributes to its <c>STATS</c> segment.</summary>
public sealed class MetricPlan
{
    internal MetricPlan(CatalogMetric metric)
    {
        Metric = metric;
        Idx = metric.Idx;
        Name = metric.Name;
        Session = metric.Scope == CatalogMetric.SessionScope;
        ValueCount = metric.Labels?.Length ?? 1;
        Value = new FieldPlan(metric.Name, null, metric.Codec);
    }

    /// <summary>The catalog metric.</summary>
    public CatalogMetric Metric { get; }

    /// <summary>The wire index.</summary>
    public int Idx { get; }

    /// <summary>The metric name.</summary>
    public string Name { get; }

    /// <summary>Whether it belongs to the per-session segment.</summary>
    public bool Session { get; }

    /// <summary>
    /// Its position within its own segment — <see cref="CatalogPlan.ServerMetrics"/> or <see cref="CatalogPlan.SessionMetrics"/>, whichever
    /// <see cref="Session"/> names. Not <see cref="Idx"/>: the wire indices interleave the two scopes and skip the built-ins a runtime does not publish.
    /// </summary>
    /// <remarks>
    /// Assigned when the plan is compiled, because the alternative is what it replaced: a sink handed a <see cref="MetricPlan"/> per VALUE had to recover
    /// its array with <c>Array.IndexOf</c>, which is a linear scan per sample and therefore quadratic in the metric count — on the one block whose whole
    /// purpose is to grow that count.
    /// </remarks>
    public int Offset { get; internal set; }

    /// <summary>Values per emission: its label count, or 1 for a scalar.</summary>
    public int ValueCount { get; }

    /// <summary>The codec of each value.</summary>
    public FieldPlan Value { get; }
}

/// <summary>
/// A canonical catalog compiled into the plans both directions work from: the server's encoder, a client's decoder and the golden-vector generator.
/// </summary>
/// <remarks>
/// Compiled once per catalog — at <c>Start</c> on a server, at <c>WELCOME</c> on a client — so no record decode ever looks a codec parameter up by name.
/// </remarks>
public sealed class CatalogPlan
{
    private readonly MessagePlan[] _events;
    private readonly MessagePlan[] _commands;

    private CatalogPlan(Catalog catalog)
    {
        Catalog = catalog;
        var enums = catalog.Enums ?? new Dictionary<string, string[]>();
        Archetypes = new ArchetypePlan[catalog.Archetypes?.Length ?? 0];
        for (var i = 0; i < Archetypes.Length; i++)
        {
            if (catalog.Archetypes[i].Idx != i)
            {
                throw new CatalogException([$"archetype '{catalog.Archetypes[i].Name}' has index {catalog.Archetypes[i].Idx} at position {i}"]);
            }

            Archetypes[i] = new ArchetypePlan(catalog.Archetypes[i], enums);
        }

        _events = Index(catalog.Events, e => e.Idx, e => new MessagePlan(e.Idx, e.Name, e.Fields, enums));
        _commands = Index(catalog.Commands, c => c.Idx, c => new MessagePlan(c.Idx, c.Name, c.Fields, enums));

        var server = new List<MetricPlan>();
        var session = new List<MetricPlan>();
        var previousIdx = -1;
        foreach (var m in catalog.Metrics ?? [])
        {
            if (m.Idx <= previousIdx)
            {
                throw new CatalogException([$"metric '{m.Name}' is out of index order: STATS segments are laid out in index order"]);
            }

            previousIdx = m.Idx;
            var segment = m.Scope == CatalogMetric.SessionScope ? session : server;
            segment.Add(new MetricPlan(m) { Offset = segment.Count });
        }

        ServerMetrics = server.ToArray();
        SessionMetrics = session.ToArray();

        Grids = catalog.Grids ?? [];
        RealmKinds = catalog.RealmKinds is { Length: > 0 } kinds ? kinds : DefaultRealmKinds;
        for (var i = 0; i < Grids.Length; i++)
        {
            if (Grids[i].Idx != i)
            {
                throw new CatalogException([$"grid at position {i} has index {Grids[i].Idx}"]);
            }
        }
    }

    /// <summary>The canonical catalog this plan was compiled from.</summary>
    public Catalog Catalog { get; }

    /// <summary>The archetypes, by index.</summary>
    public ArchetypePlan[] Archetypes { get; }

    /// <summary>Server-scope metrics, in index order: the first segment of a <c>STATS</c> block.</summary>
    public MetricPlan[] ServerMetrics { get; }

    /// <summary>Session-scope metrics, in index order: the second segment of a <c>STATS</c> block.</summary>
    public MetricPlan[] SessionMetrics { get; }

    /// <summary>The grids, by index.</summary>
    public CatalogGrid[] Grids { get; }

    /// <summary>The realm kinds a catalog that declares none has: the default kind alone.</summary>
    public static readonly string[] DefaultRealmKinds = [""];

    /// <summary>The realm kinds, in canonical order: a <c>REALM</c> block's <c>kindIdx</c> indexes this. <see cref="DefaultRealmKinds"/> when none.</summary>
    public string[] RealmKinds { get; }

    /// <summary>Compiles a catalog that is already canonical.</summary>
    /// <param name="canonical">A validated, canonical catalog.</param>
    /// <returns>The plan.</returns>
    public static CatalogPlan Compile(Catalog canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return new CatalogPlan(canonical);
    }

    /// <summary>The archetype at a wire index read from a peer.</summary>
    /// <param name="idx">The index.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="WireFormatException">No such archetype.</exception>
    public ArchetypePlan Archetype(int idx) =>
        (uint)idx < (uint)Archetypes.Length ? Archetypes[idx] : throw WireFormatException.Malformed($"archetype index {idx} does not exist");

    /// <summary>The event type at a wire index read from a peer.</summary>
    /// <param name="idx">The index.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="WireFormatException">No such event.</exception>
    public MessagePlan Event(int idx) =>
        (uint)idx < (uint)_events.Length && _events[idx] != null ? _events[idx] : throw WireFormatException.Malformed($"event index {idx} does not exist");

    /// <summary>The command type at a wire index read from a peer.</summary>
    /// <param name="idx">The index.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="WireFormatException">No such command.</exception>
    public MessagePlan Command(int idx) =>
        (uint)idx < (uint)_commands.Length && _commands[idx] != null
            ? _commands[idx]
            : throw WireFormatException.Malformed($"command index {idx} does not exist");

    /// <summary>The command type with a given name, or <see langword="null"/>.</summary>
    /// <param name="name">The command name.</param>
    /// <returns>The plan.</returns>
    public MessagePlan CommandByName(string name) => Array.Find(_commands, c => c != null && c.Name == name);

    /// <summary>The event type with a given name, or <see langword="null"/>.</summary>
    /// <param name="name">The event name.</param>
    /// <returns>The plan.</returns>
    public MessagePlan EventByName(string name) => Array.Find(_events, e => e != null && e.Name == name);

    /// <summary>The archetype with a given name, or <see langword="null"/>.</summary>
    /// <param name="name">The archetype name.</param>
    /// <returns>The plan.</returns>
    public ArchetypePlan ArchetypeByName(string name) => Array.Find(Archetypes, a => a.Name == name);

    // Wire indices are dense from the reserved base (W27), so anything beyond a few thousand is a hostile or broken catalog, not a large one.
    private const int MaxMessageIdx = 1 << 16;

    private static MessagePlan[] Index<T>(T[] items, Func<T, int> idx, Func<T, MessagePlan> build)
    {
        var max = -1;
        foreach (var item in items ?? [])
        {
            var i = idx(item);
            if (i is < 0 or >= MaxMessageIdx)
            {
                throw new CatalogException([$"wire index {i} is outside [0, {MaxMessageIdx})"]);
            }

            max = Math.Max(max, i);
        }

        var result = new MessagePlan[max + 1];
        foreach (var item in items ?? [])
        {
            if (result[idx(item)] != null)
            {
                throw new CatalogException([$"wire index {idx(item)} is assigned twice"]);
            }

            result[idx(item)] = build(item);
        }

        return result;
    }
}
