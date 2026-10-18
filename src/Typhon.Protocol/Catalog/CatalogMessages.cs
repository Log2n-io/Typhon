using System;
using System.Text.Json.Serialization;

namespace Typhon.Protocol;

/// <summary>
/// An event the server may send: a fact about something that happened, routed to the sessions that should learn of it.
/// </summary>
public sealed class CatalogEvent
{
    /// <summary>The wire index: built-in events keep their reserved index; application events are numbered from 16 in ordinal name order (W27).</summary>
    public int Idx { get; init; }

    /// <summary>The declared wire name.</summary>
    public string Name { get; init; }

    /// <summary>How the event is routed — for example <c>interest</c> for everyone who can see its subject.</summary>
    public string Scope { get; init; }

    /// <summary>The event's payload fields, in wire order once canonical.</summary>
    public CatalogField[] Fields { get; init; }
}

/// <summary>
/// The rate a client may send one command type at: a token bucket, refilled per second, with a burst ceiling.
/// </summary>
/// <remarks>
/// Enforced on the transport thread before the command reaches the ring, so an over-rate client costs the tick nothing. This is the throughput limit; the
/// ring's own depth governs only how much lateness it tolerates.
/// </remarks>
public sealed class CatalogCommandRate
{
    /// <summary>Sustained commands per second.</summary>
    public int PerSec { get; init; }

    /// <summary>Bucket depth, so a client may burst to this before the sustained rate binds.</summary>
    public int Burst { get; init; }
}

/// <summary>
/// A typed command a client may send. Commands enter the tick and are validated by ordinary application systems; a client never transacts directly.
/// </summary>
public sealed class CatalogCommand
{
    /// <summary>The <see cref="Delivery"/> that keeps every command, in order.</summary>
    public const string QueuedDelivery = "queued";

    /// <summary>The <see cref="Delivery"/> that coalesces to the newest command per session.</summary>
    public const string LatestDelivery = "latest";

    /// <summary>
    /// The wire index: built-in commands keep their reserved index (<c>ClientRegion</c> 0, <c>SubscribeRequest</c> 1); application commands are numbered
    /// from 16 in ordinal name order (W27).
    /// </summary>
    public int Idx { get; init; }

    /// <summary>The declared wire name.</summary>
    public string Name { get; init; }

    /// <summary>Delivery discipline: <see cref="QueuedDelivery"/> or <see cref="LatestDelivery"/>.</summary>
    public string Delivery { get; init; }

    /// <summary>The rate limit for this command type. Absent when the type is not rate-limited.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CatalogCommandRate Rate { get; init; }

    /// <summary>The command's payload fields, in wire order once canonical.</summary>
    public CatalogField[] Fields { get; init; }
}

/// <summary>
/// A spatial grid a client can reason about: the cell geometry behind aggregate tiers and region requests.
/// </summary>
public sealed class CatalogGrid
{
    /// <summary>The wire index, assigned by canonicalization from the grid's geometry.</summary>
    public int Idx { get; init; }

    /// <summary>
    /// The tile, in the realm's replication cells (<c>typhon.3</c>): origin and dimensions are the realm frame's, so one grid is valid in every realm
    /// (12-realms § 5.4).
    /// </summary>
    public int TileCells { get; init; }

    /// <summary>The archetype indices counted on this grid, in the order an <c>AGG</c> cell lists their counts.</summary>
    public int[] Archetypes { get; init; }
}

/// <summary>
/// A group set (14 § 3.5): entities sharing a key, held whole — a session that holds a group holds every current member, and is told when it does.
/// </summary>
public sealed class CatalogGroupSet
{
    /// <summary>The <see cref="Selector"/> of a set whose groups a session holds while its observer meets a region's extent.</summary>
    public const string RegionSelector = "region";

    /// <summary>The codecs a key may be declared with: the integer the key field holds. On the wire every key is a <c>vari</c>.</summary>
    public static System.Collections.ObjectModel.ReadOnlyCollection<string> KeyCodecs { get; } = Array.AsReadOnly<string>(["u8", "i8", "u16", "i16", "u32", "i32", "u64", "i64"]);

    /// <summary>The wire index, the set's position in ordinal name order.</summary>
    public int Idx { get; init; }

    /// <summary>The set's declared name, unique among sets.</summary>
    public string Name { get; init; }

    /// <summary>What decides which groups a session holds; <see cref="RegionSelector"/> is the only one.</summary>
    public string Selector { get; init; }

    /// <summary>The key's integer type, one of <see cref="KeyCodecs"/>.</summary>
    public string Key { get; init; }

    /// <summary>The member archetypes' indices, ascending.</summary>
    public int[] Members { get; init; }
}

/// <summary>
/// A metric the server publishes in its <c>STATS</c> block (W25).
/// </summary>
public sealed class CatalogMetric
{
    /// <summary>The <see cref="Scope"/> of a metric encoded once for every subscriber.</summary>
    public const string ServerScope = "server";

    /// <summary>The <see cref="Scope"/> of a metric with a value per session.</summary>
    public const string SessionScope = "session";

    /// <summary>The <see cref="Kind"/> of a windowed value.</summary>
    public const string GaugeKind = "gauge";

    /// <summary>The <see cref="Kind"/> of a cumulative count, modulo 2³², so a missed emission loses nothing.</summary>
    public const string CounterKind = "counter";

    /// <summary>The wire index: built-in metrics keep their reserved index; application metrics are numbered from 32 in ordinal name order.</summary>
    public int Idx { get; init; }

    /// <summary>The metric's dotted name, for example <c>typhon.tick.p99</c>. The <c>typhon.</c> prefix is reserved for built-ins.</summary>
    public string Name { get; init; }

    /// <summary>The unit the value is expressed in, for example <c>ms</c>.</summary>
    public string Unit { get; init; }

    /// <summary>How each value is encoded.</summary>
    public CatalogCodec Codec { get; init; }

    /// <summary><see cref="SessionScope"/>, or absent for <see cref="ServerScope"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Scope { get; init; }

    /// <summary><see cref="CounterKind"/>, or absent for <see cref="GaugeKind"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Kind { get; init; }

    /// <summary>For a vector metric, one label per value, in wire order — the order is data and is never sorted. Absent for a scalar.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string[] Labels { get; init; }
}
