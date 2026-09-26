using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Typhon.Protocol;

/// <summary>
/// Thrown when a catalog breaks a rule of 03-wire-protocol. The message lists every problem found, not only the first.
/// </summary>
public sealed class CatalogException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="problems">Every problem found.</param>
    public CatalogException(IReadOnlyList<string> problems) : base(Format(problems)) => Problems = problems;

    /// <summary>Every problem found, one per entry.</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string Format(IReadOnlyList<string> problems)
    {
        var sb = new StringBuilder($"the catalog breaks {problems.Count} wire rule(s):");
        foreach (var p in problems)
        {
            sb.Append("\n  - ").Append(p);
        }

        return sb.ToString();
    }
}

/// <summary>
/// Checks a catalog against the registration rules of 03-wire-protocol § 12 before anything is encoded or decoded against it.
/// </summary>
/// <remarks>
/// <para>
/// A server validates at <c>Start</c>, so a bad declaration fails the application, never a session; a client validates what it receives, so a broken or
/// hostile server fails at <c>WELCOME</c>, never mid-stream. The rules are the ones that make decoding well-defined: widths the codecs accept, bounds a
/// quantizer can use without losing the round trip, groups a <c>u8</c> mask can address, grids whose cell index fits, lists whose elements decode as
/// numbers, and built-ins whose shape is the engine's, not merely their name.
/// </para>
/// <para>
/// <b>Hostile input is a catalog too.</b> Every collection may contain <see langword="null"/> elements after a parse; each one is a problem, never a crash.
/// A parameter the codec kind does not read is refused rather than ignored: a decoder that honoured, say, <c>fixedBytes</c> on a known codec would read a
/// different width than the one that encoded it.
/// </para>
/// </remarks>
public static class CatalogValidator
{
    /// <summary>The most cells a grid may have: its cell index must fit comfortably in 32 bits and its counts in memory.</summary>
    public const long MaxGridCells = 1L << 24;

    private static readonly string[] EnumCodecs = ["bits", "u8", "u16", "varu"];

    /// <summary>Throws <see cref="CatalogException"/> listing every rule <paramref name="catalog"/> breaks.</summary>
    /// <param name="catalog">The catalog, canonical or as declared.</param>
    public static void Validate(Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var problems = new List<string>();
        Check(catalog, problems);
        if (problems.Count > 0)
        {
            throw new CatalogException(problems);
        }
    }

    private static void Check(Catalog c, List<string> problems)
    {
        if (c.Protocol == null || c.Protocol.Major < 1 || c.Protocol.Minor < 0)
        {
            problems.Add("protocol is missing or invalid");
        }

        if (c.App == null || string.IsNullOrEmpty(c.App.Name))
        {
            problems.Add("app.name is missing");
        }

        if (c.Tick == null || c.Tick.PeriodUs <= 0 || c.Tick.PingHz <= 0)
        {
            problems.Add("tick.periodUs and tick.pingHz must be positive");
        }

        var maxBytes = int.MaxValue;
        if (c.Limits == null || c.Limits.FrameBytes <= 0 || c.Limits.ClientMessageBytes <= 0 || c.Limits.ResumeGraceMs < 0)
        {
            problems.Add("limits.frameBytes and limits.clientMessageBytes must be positive, limits.resumeGraceMs non-negative");
        }
        else
        {
            maxBytes = c.Limits.FrameBytes;
        }

        CheckStrings("sessionKinds", c.SessionKinds, ProtocolConstants.SessionKindMaxBytes, problems);
        CheckRealmKinds(c.RealmKinds, problems);

        var enums = c.Enums ?? new Dictionary<string, string[]>();
        foreach (var (name, names) in enums)
        {
            if (string.IsNullOrEmpty(name))
            {
                problems.Add("an enum needs a name");
            }

            CheckStrings($"enum '{name}'", names, int.MaxValue, problems);
            if (names is null or { Length: 0 })
            {
                problems.Add($"enum '{name}' has no names");
            }
        }

        var archetypes = c.Archetypes ?? [];
        if (archetypes.Length > ProtocolConstants.MaxArchetypes)
        {
            problems.Add($"{archetypes.Length} archetypes; at most {ProtocolConstants.MaxArchetypes}");
        }

        CheckUniqueNames(archetypes, a => a.Name, "archetype", problems);
        foreach (var a in archetypes)
        {
            if (a != null)
            {
                CheckArchetype(a, enums, maxBytes, problems);
            }
        }

        var events = c.Events ?? [];
        CheckUniqueNames(events, e => e.Name, "event", problems);
        foreach (var e in events)
        {
            if (e == null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(e.Scope))
            {
                problems.Add($"event '{e.Name}' needs a scope");
            }

            if (e.Name == BuiltInEvents.EventsLost && !BuiltInEvents.HasEventsLostShape(e))
            {
                problems.Add($"event '{BuiltInEvents.EventsLost}' is a built-in; declare it with BuiltInEvents.CreateEventsLost, not by name");
            }

            CheckMessageFields($"event '{e.Name}'", e.Fields, enums, maxBytes, problems);
        }

        var commands = c.Commands ?? [];
        CheckUniqueNames(commands, x => x.Name, "command", problems);
        foreach (var cmd in commands)
        {
            if (cmd != null)
            {
                CheckCommand(cmd, enums, maxBytes, problems);
            }
        }

        var metrics = c.Metrics ?? [];
        CheckUniqueNames(metrics, m => m.Name, "metric", problems);
        foreach (var m in metrics)
        {
            if (m != null)
            {
                CheckMetric(m, problems);
            }
        }

        CheckGrids(c.Grids ?? [], archetypes.Length, problems);
    }

    private static void CheckCommand(CatalogCommand cmd, Dictionary<string, string[]> enums, int maxBytes, List<string> problems)
    {
        if (cmd.Delivery is not (CatalogCommand.QueuedDelivery or CatalogCommand.LatestDelivery))
        {
            problems.Add($"command '{cmd.Name}': delivery must be 'queued' or 'latest'");
        }

        if (cmd.Rate != null && (cmd.Rate.PerSec <= 0 || cmd.Rate.Burst <= 0))
        {
            problems.Add($"command '{cmd.Name}': rate.perSec and rate.burst must be positive");
        }

        switch (cmd.Name)
        {
            case BuiltInCommands.ClientRegion when !BuiltInCommands.HasClientRegionShape(cmd):
                problems.Add($"command '{BuiltInCommands.ClientRegion}' is a built-in; declare it with BuiltInCommands.CreateClientRegion, not by name");
                break;
            case BuiltInCommands.SubscribeRequest:
                problems.Add($"command '{BuiltInCommands.SubscribeRequest}' is reserved; its shape is decided when source subscriptions are built");
                break;
        }

        CheckMessageFields($"command '{cmd.Name}'", cmd.Fields, enums, maxBytes, problems);
    }

    private static void CheckArchetype(CatalogArchetype a, Dictionary<string, string[]> enums, int maxBytes, List<string> problems)
    {
        var where = $"archetype '{a.Name}'";
        var groups = a.Groups ?? [];
        CheckGroups(where, groups, allowEmpty: true, problems);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in a.Fields ?? [])
        {
            if (f == null)
            {
                problems.Add($"{where}: a field is null");
                continue;
            }

            var at = $"{where} field '{f.Name}'";
            if (!names.Add(f.Name ?? string.Empty))
            {
                problems.Add($"{at} is declared twice");
            }

            var hasGroup = !string.IsNullOrEmpty(f.Group);
            if (hasGroup == f.OnEnter)
            {
                problems.Add($"{at} must set exactly one of group and onEnter");
            }
            else if (hasGroup && Array.IndexOf(groups, f.Group) < 0)
            {
                problems.Add($"{at} names group '{f.Group}', which the archetype does not declare");
            }

            CheckField(at, f, enums, allowList: false, maxBytes, problems);
        }

        if (a.Position != null)
        {
            CheckPosition(where, a.Position, problems);
        }

        if (a.Owner != null)
        {
            var ownerGroups = a.Owner.Groups ?? [];
            CheckGroups($"{where} owner", ownerGroups, allowEmpty: false, problems);
            foreach (var f in a.Owner.Fields ?? [])
            {
                if (f == null)
                {
                    problems.Add($"{where}: an owner field is null");
                    continue;
                }

                var at = $"{where} owner field '{f.Name}'";
                if (!names.Add(f.Name ?? string.Empty))
                {
                    problems.Add($"{at} is declared twice");
                }

                if (f.OnEnter || string.IsNullOrEmpty(f.Group) || Array.IndexOf(ownerGroups, f.Group) < 0)
                {
                    problems.Add($"{at} must name one of the owner groups, and cannot be onEnter");
                }

                CheckField(at, f, enums, allowList: false, maxBytes, problems);
            }
        }
    }

    private static void CheckGroups(string where, string[] groups, bool allowEmpty, List<string> problems)
    {
        if (groups.Length > ProtocolConstants.MaxGroups || (!allowEmpty && groups.Length == 0))
        {
            problems.Add($"{where}: {groups.Length} groups; {(allowEmpty ? 0 : 1)}..{ProtocolConstants.MaxGroups} allowed");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in groups)
        {
            if (string.IsNullOrEmpty(g) || !seen.Add(g))
            {
                problems.Add($"{where}: group '{g}' is empty or declared twice");
            }
        }
    }

    private static void CheckMessageFields(string where, CatalogField[] fields, Dictionary<string, string[]> enums, int maxBytes, List<string> problems)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fields ?? [])
        {
            if (f == null)
            {
                problems.Add($"{where}: a field is null");
                continue;
            }

            var at = $"{where} field '{f.Name}'";
            if (!names.Add(f.Name ?? string.Empty))
            {
                problems.Add($"{at} is declared twice");
            }

            if (!string.IsNullOrEmpty(f.Group) || f.OnEnter)
            {
                problems.Add($"{at}: event and command fields carry no group and no onEnter");
            }

            CheckField(at, f, enums, allowList: true, maxBytes, problems);
        }
    }

    private static void CheckField(string at, CatalogField f, Dictionary<string, string[]> enums, bool allowList, int maxBytes, List<string> problems)
    {
        if (string.IsNullOrEmpty(f.Name))
        {
            problems.Add($"{at}: a field needs a name");
        }

        if (f.Codec == null)
        {
            problems.Add($"{at}: codec is missing");
            return;
        }

        if (f.Codec.Kind is CodecKind.Vel2 or CodecKind.Vel3)
        {
            problems.Add($"{at}: a vel codec is only valid inside a position");
        }

        if (f.Codec.Kind == CodecKind.List && !allowList)
        {
            problems.Add($"{at}: a list is only valid in event and command fields");
        }

        CheckCodec(at, f.Codec, maxBytes, problems);

        if (!string.IsNullOrEmpty(f.Enum))
        {
            if (Array.IndexOf(EnumCodecs, f.Codec.Type) < 0)
            {
                problems.Add($"{at}: an enum is allowed only on bits, u8, u16 and varu, not '{f.Codec.Type}'");
            }
            else if (!enums.TryGetValue(f.Enum, out var names) || names == null)
            {
                problems.Add($"{at}: enum '{f.Enum}' is not declared");
            }
            else
            {
                var capacity = f.Codec.Kind switch
                {
                    CodecKind.Bits => 1L << Math.Clamp(f.Codec.N, 1, ProtocolConstants.MaxPackedBits),
                    CodecKind.U8 => 256L,
                    _ => 65536L,
                };

                if (names.Length > capacity)
                {
                    problems.Add($"{at}: enum '{f.Enum}' has {names.Length} names; the codec holds {capacity}");
                }
            }
        }
    }

    private static void CheckPosition(string where, CatalogPosition p, List<string> problems)
    {
        var dims = p.Pos?.Kind switch
        {
            CodecKind.Pos2 => 2,
            CodecKind.Pos3 => 3,
            _ => 0,
        };

        if (dims == 0)
        {
            problems.Add($"{where} position: pos must be a pos2 or pos3 codec");
        }
        else
        {
            CheckCodec($"{where} position.pos", p.Pos, int.MaxValue, problems);
        }

        switch (p.Kind)
        {
            case CatalogPosition.StaticKind:
                if (p.Model != null || p.Vel != null)
                {
                    problems.Add($"{where} position: a static position has no model and no vel");
                }

                break;
            case CatalogPosition.MotionKind:
                if (p.Model == CatalogPosition.LinearModel)
                {
                    var velDims = p.Vel?.Kind switch
                    {
                        CodecKind.Vel2 => 2,
                        CodecKind.Vel3 => 3,
                        _ => 0,
                    };

                    if (velDims == 0 || velDims != dims)
                    {
                        problems.Add($"{where} position: the linear model needs a vel codec matching pos's dimensions");
                    }
                    else
                    {
                        CheckCodec($"{where} position.vel", p.Vel, int.MaxValue, problems);
                    }
                }
                else if (p.Model == CatalogPosition.NoneModel)
                {
                    if (p.Vel != null)
                    {
                        problems.Add($"{where} position: the none model carries no vel");
                    }
                }
                else
                {
                    problems.Add($"{where} position: model must be 'linear' or 'none'");
                }

                break;
            default:
                problems.Add($"{where} position: kind must be 'motion' or 'static'");
                break;
        }
    }

    private static void CheckMetric(CatalogMetric m, List<string> problems)
    {
        var where = $"metric '{m.Name}'";
        var reserved = BuiltInMetrics.ReservedIdx(m.Name);
        if (reserved >= 0)
        {
            if (!BuiltInMetrics.HasShape(reserved, m))
            {
                problems.Add($"{where} is a built-in; declare it with BuiltInMetrics.Create, not by name");
            }
        }
        else if ((m.Name ?? string.Empty).StartsWith(ProtocolConstants.BuiltInMetricPrefix, StringComparison.Ordinal))
        {
            problems.Add($"{where}: the '{ProtocolConstants.BuiltInMetricPrefix}' prefix is reserved for built-in metrics");
        }

        if (m.Unit == null)
        {
            problems.Add($"{where}: unit is missing");
        }

        if (m.Scope is not (null or CatalogMetric.ServerScope or CatalogMetric.SessionScope))
        {
            problems.Add($"{where}: scope must be 'server' or 'session'");
        }

        if (m.Kind is not (null or CatalogMetric.GaugeKind or CatalogMetric.CounterKind))
        {
            problems.Add($"{where}: kind must be 'gauge' or 'counter'");
        }

        if (m.Labels != null)
        {
            if (m.Labels.Length == 0)
            {
                problems.Add($"{where}: a vector metric needs at least one label");
            }

            CheckStrings($"{where} labels", m.Labels, int.MaxValue, problems);
        }

        if (m.Codec == null)
        {
            problems.Add($"{where}: codec is missing");
            return;
        }

        if (m.Codec.Kind is not (CodecKind.U8 or CodecKind.U16 or CodecKind.U32 or CodecKind.I8 or CodecKind.I16 or CodecKind.I32 or CodecKind.Varu
            or CodecKind.Vari or CodecKind.F16 or CodecKind.F32 or CodecKind.Unorm or CodecKind.Quant or CodecKind.Unknown))
        {
            problems.Add($"{where}: codec '{m.Codec.Type}' is not a metric codec");
        }

        CheckCodec(where, m.Codec, int.MaxValue, problems);
    }

    private static void CheckGrids(CatalogGrid[] grids, int archetypeCount, List<string> problems)
    {
        var shapes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < grids.Length; i++)
        {
            var g = grids[i];
            if (g == null)
            {
                problems.Add("a grid is null");
                continue;
            }

            var where = $"grid {i}";

            // Origin and dimensions are the realm frame's (typhon.3): the catalog carries the tile alone, in replication cells.
            if (g.TileCells < 1)
            {
                problems.Add($"{where}: tileCells must be at least 1");
            }

            var seen = new HashSet<int>();
            foreach (var idx in g.Archetypes ?? [])
            {
                if (idx < 0 || idx >= archetypeCount || !seen.Add(idx))
                {
                    problems.Add($"{where}: archetype index {idx} does not exist or is listed twice");
                }
            }

            var key = new StringBuilder().Append(g.TileCells).Append('#').AppendJoin(',', g.Archetypes ?? []).ToString();
            if (!shapes.TryAdd(key, i))
            {
                problems.Add($"{where} duplicates grid {shapes[key]}");
            }
        }
    }

    private static void CheckRealmKinds(string[] kinds, List<string> problems)
    {
        // Absent or empty means the default kind alone: a catalog with nothing realm-framed need not name one.
        if (kinds == null)
        {
            return;
        }

        if (kinds.Length > ProtocolConstants.MaxRealmKinds)
        {
            problems.Add($"realmKinds: {kinds.Length} kinds; at most {ProtocolConstants.MaxRealmKinds}");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in kinds)
        {
            if (k == null || Encoding.UTF8.GetByteCount(k) > ProtocolConstants.SessionKindMaxBytes || !seen.Add(k))
            {
                problems.Add($"realmKinds: '{k}' is null, longer than {ProtocolConstants.SessionKindMaxBytes} UTF-8 bytes, or listed twice");
            }
        }
    }

    private static void CheckCodec(string at, CatalogCodec codec, int maxBytes, List<string> problems)
    {
        CheckUnreadParameters(at, codec, problems);
        switch (codec.Kind)
        {
            case CodecKind.Unknown:
                if (string.IsNullOrEmpty(codec.Type) || codec.FixedBytes <= 0 || codec.FixedBytes > maxBytes)
                {
                    problems.Add($"{at}: codec '{codec.Type}' is unknown and declares no usable fixedBytes, so it cannot be skipped");
                }

                break;
            case CodecKind.Quant:
                CheckBits(at, codec.Bits, problems);
                CheckBounds(at, codec, 1, problems);
                break;
            case CodecKind.Pos2:
            case CodecKind.Pos3:
                // Realm-framed (typhon.3, SUB-30): bits and bounds are the REALM block's, and a parameter here is refused as unread.
                break;
            case CodecKind.Vec2:
            case CodecKind.Vec3:
                CheckBits(at, codec.Bits, problems);
                if (!(codec.Scale > 0) || !double.IsFinite(codec.Scale))
                {
                    problems.Add($"{at}: scale must be positive and finite");
                }

                break;
            case CodecKind.Vel2:
            case CodecKind.Vel3:
                CheckBits(at, codec.Bits, problems);
                if (codec.UnitExp is not (>= ProtocolConstants.MinVelocityUnitExp and <= ProtocolConstants.MaxVelocityUnitExp))
                {
                    problems.Add($"{at}: unitExp must be an integer in [{ProtocolConstants.MinVelocityUnitExp}, {ProtocolConstants.MaxVelocityUnitExp}]");
                }

                break;
            case CodecKind.Unorm:
            case CodecKind.Snorm:
            case CodecKind.Angle:
                CheckBits(at, codec.Bits, problems);
                break;
            case CodecKind.Bits:
                if (codec.N is < 1 or > ProtocolConstants.MaxPackedBits)
                {
                    problems.Add($"{at}: bits.n must be in [1, {ProtocolConstants.MaxPackedBits}]");
                }

                break;
            case CodecKind.Str:
            case CodecKind.Blob:
                if (codec.MaxBytes <= 0 || codec.MaxBytes > maxBytes)
                {
                    problems.Add($"{at}: maxBytes must be positive and within limits.frameBytes");
                }

                break;
            case CodecKind.Bytes:
                if (codec.N <= 0 || codec.N > maxBytes)
                {
                    problems.Add($"{at}: bytes.n must be positive and within limits.frameBytes");
                }

                break;
            case CodecKind.List:
                if (codec.Of == null)
                {
                    problems.Add($"{at}: a list needs an element codec");
                    break;
                }

                if (!IsListElement(codec.Of.Kind))
                {
                    problems.Add($"{at}: a list element must be a numeric byte-aligned codec, not '{codec.Of.Type}'");
                }
                else
                {
                    CheckCodec($"{at} element", codec.Of, maxBytes, problems);
                }

                if (codec.MinCount < 0 || codec.MinCount > codec.MaxCount || codec.MaxCount > ProtocolConstants.MaxListCount)
                {
                    problems.Add($"{at}: list counts must satisfy 0 ≤ minCount ≤ maxCount ≤ {ProtocolConstants.MaxListCount}");
                }

                break;
        }
    }

    /// <summary>Whether a codec may be a list element: numeric, byte-aligned, and independent of the frame.</summary>
    /// <param name="kind">The element kind.</param>
    /// <returns><see langword="true"/> when allowed.</returns>
    public static bool IsListElement(CodecKind kind) => kind is CodecKind.U8 or CodecKind.I8 or CodecKind.U16 or CodecKind.I16 or CodecKind.U32
        or CodecKind.I32 or CodecKind.Varu or CodecKind.Vari or CodecKind.EntityRef or CodecKind.F32 or CodecKind.F16 or CodecKind.Quant or CodecKind.Pos2
        or CodecKind.Pos3 or CodecKind.Vec2 or CodecKind.Vec3 or CodecKind.Unorm or CodecKind.Snorm or CodecKind.Angle or CodecKind.Quat3;

    // A parameter a kind does not read would be ignored by this library and honoured by some other decoder: refuse it so every decoder reads the same width.
    private static void CheckUnreadParameters(string at, CatalogCodec codec, List<string> problems)
    {
        if (codec.Kind == CodecKind.Unknown)
        {
            return;
        }

        var reads = codec.Kind switch
        {
            CodecKind.Quant => Parameter.Bits | Parameter.Bounds,
            CodecKind.Pos2 or CodecKind.Pos3 => Parameter.None,
            CodecKind.Vec2 or CodecKind.Vec3 => Parameter.Bits | Parameter.Scale,
            CodecKind.Vel2 or CodecKind.Vel3 => Parameter.Bits | Parameter.UnitExp,
            CodecKind.Unorm or CodecKind.Snorm or CodecKind.Angle => Parameter.Bits,
            CodecKind.Bits or CodecKind.Bytes => Parameter.N,
            CodecKind.Str or CodecKind.Blob => Parameter.MaxBytes,
            CodecKind.List => Parameter.List,
            _ => Parameter.None,
        };

        var present = Parameter.None;
        present |= codec.Bits != 0 ? Parameter.Bits : 0;
        present |= codec.Min != null || codec.Max != null ? Parameter.Bounds : 0;
        present |= codec.Scale != 0 || double.IsNaN(codec.Scale) ? Parameter.Scale : 0;
        present |= codec.UnitExp != null ? Parameter.UnitExp : 0;
        present |= codec.N != 0 ? Parameter.N : 0;
        present |= codec.MaxBytes != 0 ? Parameter.MaxBytes : 0;
        present |= codec.Of != null || codec.MinCount != 0 || codec.MaxCount != 0 ? Parameter.List : 0;
        present |= codec.FixedBytes != 0 ? Parameter.FixedBytes : 0;

        var unread = present & ~reads;
        if (unread != 0)
        {
            problems.Add($"{at}: codec '{codec.Type}' does not read {unread}");
        }
    }

    [Flags]
    private enum Parameter
    {
        None = 0,
        Bits = 1,
        Bounds = 2,
        Scale = 4,
        UnitExp = 8,
        N = 16,
        MaxBytes = 32,
        List = 64,
        FixedBytes = 128,
    }

    private static void CheckBits(string at, int bits, List<string> problems)
    {
        if (bits is not (8 or 16 or 24 or 32))
        {
            problems.Add($"{at}: bits must be 8, 16, 24 or 32");
        }
    }

    private static void CheckBounds(string at, CatalogCodec codec, int axes, List<string> problems)
    {
        if (codec.Min == null || codec.Max == null || codec.Min.Length != axes || codec.Max.Length != axes)
        {
            problems.Add($"{at}: min and max need {axes} value(s) each");
            return;
        }

        if (codec.Bits is not (8 or 16 or 24 or 32))
        {
            return;
        }

        for (var i = 0; i < axes; i++)
        {
            var min = codec.Min[i];
            var max = codec.Max[i];
            if (!double.IsFinite(min) || !double.IsFinite(max) || !(max > min) || !double.IsFinite(max - min))
            {
                problems.Add($"{at}: axis {i} needs finite bounds with max > min and a finite range");
                continue;
            }

            // Below this step, min absorbs q × step and decoding stops round-tripping (W2).
            var magnitude = Math.Max(Math.Abs(min), Math.Abs(max));
            var ulp = Math.BitIncrement(magnitude) - magnitude;
            if (WireMath.QuantStep(min, max, codec.Bits) < WireMath.Pow2(8) * ulp)
            {
                problems.Add($"{at}: axis {i} step is below 2⁸ ulps of its bounds, so quantization would not round-trip");
            }
        }
    }

    private static void CheckStrings(string where, string[] values, int maxUtf8Bytes, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in values ?? [])
        {
            if (string.IsNullOrEmpty(v) || Encoding.UTF8.GetByteCount(v) > maxUtf8Bytes || !seen.Add(v))
            {
                problems.Add($"{where}: '{v}' is empty, longer than {maxUtf8Bytes} UTF-8 bytes, or listed twice");
            }
        }
    }

    private static void CheckUniqueNames<T>(T[] items, Func<T, string> name, string what, List<string> problems)
        where T : class
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item == null)
            {
                problems.Add($"a {what} is null");
                continue;
            }

            var n = name(item);
            if (string.IsNullOrEmpty(n))
            {
                problems.Add($"a {what} needs a name");
            }
            else if (!seen.Add(n))
            {
                problems.Add($"{what} '{n}' is declared twice");
            }
        }
    }
}
