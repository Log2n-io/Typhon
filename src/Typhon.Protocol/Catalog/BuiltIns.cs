using System;

namespace Typhon.Protocol;

/// <summary>
/// The commands the engine itself understands (W27, W28). They sit in the catalog beside the application's, at reserved indices that never move.
/// </summary>
public static class BuiltInCommands
{
    /// <summary>The name of the client-supplied viewpoint footprint.</summary>
    public const string ClientRegion = "ClientRegion";

    /// <summary>The reserved index of <see cref="ClientRegion"/>.</summary>
    public const int ClientRegionIdx = 0;

    /// <summary>The name of the source-subscription request (#204). Reserved; its shape is decided when Phase 4 opens.</summary>
    public const string SubscribeRequest = "SubscribeRequest";

    /// <summary>The reserved index of <see cref="SubscribeRequest"/>.</summary>
    public const int SubscribeRequestIdx = 1;

    /// <summary>The fewest vertices a flat world's region may carry: a triangle.</summary>
    public const int MinRegionVertices = 3;

    /// <summary>The fewest vertices a deep world's region may carry: a tetrahedron (10 § 6).</summary>
    public const int MinRegionVertices3 = 4;

    /// <summary>The fewest vertices a region may carry over a position codec of <paramref name="dims"/> axes.</summary>
    /// <param name="dims">2 or 3.</param>
    /// <returns><see cref="MinRegionVertices"/> or <see cref="MinRegionVertices3"/>.</returns>
    public static int MinVertices(int dims) => dims == 3 ? MinRegionVertices3 : MinRegionVertices;

    /// <summary>The most footprint vertices a region may carry: a horizon-clipped frustum needs 5–8, 16 is headroom.</summary>
    public const int MaxRegionVertices = 16;

    /// <summary>The <c>ClientRegion</c> field carrying the convex footprint polygon.</summary>
    public const string RegionVerticesField = "vertices";

    /// <summary>The <c>ClientRegion</c> field carrying the viewpoint altitude, in metres.</summary>
    public const string RegionAltitudeField = "altitudeM";

    /// <summary>The <c>ClientRegion</c> field carrying the client's byte budget, in KiB per second.</summary>
    public const string RegionBudgetField = "budgetKiBps";

    /// <summary>The reserved index of a built-in command, or −1 when <paramref name="name"/> is an application command.</summary>
    /// <param name="name">The command name.</param>
    /// <returns>The reserved index, or −1.</returns>
    public static int ReservedIdx(string name) => name switch
    {
        ClientRegion => ClientRegionIdx,
        SubscribeRequest => SubscribeRequestIdx,
        _ => -1,
    };

    /// <summary>
    /// Whether <paramref name="command"/> is shaped exactly as <see cref="CreateClientRegion"/> builds it, for whatever position codec. The engine
    /// interprets this command itself, so an application command that merely takes its name must be refused, not decoded as a region.
    /// </summary>
    /// <param name="command">A command named <see cref="ClientRegion"/>.</param>
    /// <returns><see langword="true"/> when the shape matches.</returns>
    public static bool HasClientRegionShape(CatalogCommand command)
    {
        var fields = command.Fields ?? [];
        var vertices = Array.Find(fields, f => f?.Name == RegionVerticesField)?.Codec;
        var altitude = Array.Find(fields, f => f?.Name == RegionAltitudeField)?.Codec;
        var budget = Array.Find(fields, f => f?.Name == RegionBudgetField)?.Codec;
        return fields.Length == 3
            && command.Delivery == CatalogCommand.LatestDelivery
            && command.Rate is { PerSec: 5, Burst: 5 }
            && vertices is { Kind: CodecKind.List, MaxCount: MaxRegionVertices, Of.Kind: CodecKind.Pos2 or CodecKind.Pos3 }
            && vertices.MinCount == MinVertices(vertices.Of.Kind == CodecKind.Pos3 ? 3 : 2)
            && altitude?.Kind == CodecKind.F16
            && budget?.Kind == CodecKind.U16
            && Array.TrueForAll(fields, f => f is { Group: null, OnEnter: false, Enum: null });
    }

    /// <summary>
    /// Builds the <c>ClientRegion</c> command for a world whose positions use <paramref name="position"/>: its vertices are quantized exactly like the
    /// archetypes' positions, so a decoded region can never leave the world. A flat world's region is a polygon of 3–16 points, a deep world's a
    /// polyhedron of 4–16 (10 § 6).
    /// </summary>
    /// <param name="position">The grid's <see cref="CodecKind.Pos2"/> codec in a flat world, its <see cref="CodecKind.Pos3"/> codec in a deep one.</param>
    /// <returns>The command definition: latest-wins, at most 5 per second.</returns>
    public static CatalogCommand CreateClientRegion(CatalogCodec position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return new CatalogCommand
        {
            Idx = ClientRegionIdx,
            Name = ClientRegion,
            Delivery = CatalogCommand.LatestDelivery,
            Rate = new CatalogCommandRate { PerSec = 5, Burst = 5 },
            Fields =
            [
                new CatalogField
                {
                    Name = RegionVerticesField,
                    Codec = new CatalogCodec
                    {
                        Kind = CodecKind.List, Of = position, MinCount = MinVertices(position.Kind == CodecKind.Pos3 ? 3 : 2),
                        MaxCount = MaxRegionVertices,
                    },
                },
                new CatalogField { Name = RegionAltitudeField, Codec = new CatalogCodec { Kind = CodecKind.F16 } },
                new CatalogField { Name = RegionBudgetField, Codec = new CatalogCodec { Kind = CodecKind.U16 } },
            ],
        };
    }
}

/// <summary>
/// The metrics every Typhon server can publish (W25), at reserved indices 0–31 under the reserved <c>typhon.</c> prefix. Durations travel in milliseconds as
/// <c>f16</c>: microseconds would overflow a half at 65 ms.
/// </summary>
public static class BuiltInMetrics
{
    private static readonly (string Name, string Unit, CodecKind Codec, string Kind, string Scope)[] Table =
    [
        ("typhon.tick.p50", "ms", CodecKind.F16, null, null),
        ("typhon.tick.p99", "ms", CodecKind.F16, null, null),
        ("typhon.system.mean", "ms", CodecKind.F16, null, null),
        ("typhon.archetype.entities", "count", CodecKind.Varu, null, null),
        ("typhon.sessions", "count", CodecKind.Varu, null, null),
        ("typhon.net.outBytesPerSec", "B/s", CodecKind.Varu, null, null),
        ("typhon.subscriptions.track.p99", "ms", CodecKind.F16, null, null),
        ("typhon.durability.wait.p99", "ms", CodecKind.F16, null, null),
        ("typhon.session.outBytesPerSec", "B/s", CodecKind.Varu, null, CatalogMetric.SessionScope),
        ("typhon.session.skippedFrames", "count", CodecKind.Varu, CatalogMetric.CounterKind, CatalogMetric.SessionScope),
        ("typhon.session.droppedCommands", "count", CodecKind.Varu, CatalogMetric.CounterKind, CatalogMetric.SessionScope),
    ];

    /// <summary>Per-system mean duration; its labels are the system names, in schedule order.</summary>
    public const string SystemMean = "typhon.system.mean";

    /// <summary>Entities per replicated archetype; its labels are the archetype names, in index order.</summary>
    public const string ArchetypeEntities = "typhon.archetype.entities";

    /// <summary>The number of built-in metrics defined today.</summary>
    public static int Count => Table.Length;

    /// <summary>The reserved index of a built-in metric, or −1 when <paramref name="name"/> is not one.</summary>
    /// <param name="name">The metric name.</param>
    /// <returns>The reserved index, or −1.</returns>
    public static int ReservedIdx(string name)
    {
        for (var i = 0; i < Table.Length; i++)
        {
            if (string.Equals(Table[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether <paramref name="metric"/> is shaped exactly as <see cref="Create"/> builds the built-in at <paramref name="idx"/>.</summary>
    /// <param name="idx">The built-in's reserved index.</param>
    /// <param name="metric">A metric carrying that built-in's name.</param>
    /// <returns><see langword="true"/> when unit, codec, kind, scope and the presence of labels all match.</returns>
    public static bool HasShape(int idx, CatalogMetric metric)
    {
        var (_, unit, codec, kind, scope) = Table[idx];
        var labelled = Table[idx].Name is SystemMean or ArchetypeEntities;
        return metric.Unit == unit
            && metric.Codec?.Kind == codec
            && (metric.Kind ?? CatalogMetric.GaugeKind) == (kind ?? CatalogMetric.GaugeKind)
            && (metric.Scope ?? CatalogMetric.ServerScope) == (scope ?? CatalogMetric.ServerScope)
            && (metric.Labels != null) == labelled;
    }

    /// <summary>Builds the definition of the built-in metric at <paramref name="idx"/>.</summary>
    /// <param name="idx">The reserved index, in [0, <see cref="Count"/>).</param>
    /// <param name="labels">The labels, for <see cref="SystemMean"/> and <see cref="ArchetypeEntities"/>; otherwise <see langword="null"/>.</param>
    /// <returns>The metric definition.</returns>
    public static CatalogMetric Create(int idx, string[] labels = null)
    {
        var (name, unit, codec, kind, scope) = Table[idx];
        return new CatalogMetric
        {
            Idx = idx, Name = name, Unit = unit, Codec = new CatalogCodec { Kind = codec }, Kind = kind, Scope = scope, Labels = labels,
        };
    }
}

/// <summary>
/// Reason codes of an <c>ACKS</c> rejection record. Codes 128–255 belong to the application.
/// </summary>
public static class AckReasons
{
    /// <summary>The command type's rate limit was exceeded.</summary>
    public const byte RateLimited = 1;

    /// <summary>An application system rejected the command.</summary>
    public const byte Rejected = 2;

    /// <summary>A <c>ClientRegion</c> whose convex hull has fewer than three points or no area; the previous region is kept.</summary>
    public const byte RegionInvalid = 3;

    /// <summary>The first application-defined reason code.</summary>
    public const byte FirstApplicationReason = 128;
}
