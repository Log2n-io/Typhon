using System.Text.Json.Serialization;

namespace Typhon.Protocol;

/// <summary>
/// An event the server may send: a fact about something that happened, routed to the sessions that should learn of it.
/// </summary>
public sealed class CatalogEvent
{
    /// <summary>The wire index, assigned by canonicalization from the ordinal sort of <see cref="Name"/>.</summary>
    public int Idx { get; init; }

    /// <summary>The declared wire name.</summary>
    public string Name { get; init; }

    /// <summary>How the event is routed — for example <c>interest</c> for everyone who can see its subject.</summary>
    public string Scope { get; init; }

    /// <summary>The event's payload fields, in encode order.</summary>
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
    /// <summary>The wire index, assigned by canonicalization from the ordinal sort of <see cref="Name"/>.</summary>
    public int Idx { get; init; }

    /// <summary>The declared wire name.</summary>
    public string Name { get; init; }

    /// <summary>Delivery discipline: <c>queued</c> keeps every command in order, <c>latest</c> coalesces to the newest per session.</summary>
    public string Delivery { get; init; }

    /// <summary>The rate limit for this command type. Absent when the type is not rate-limited.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CatalogCommandRate Rate { get; init; }

    /// <summary>The command's payload fields, in encode order.</summary>
    public CatalogField[] Fields { get; init; }
}

/// <summary>
/// A spatial grid a client can reason about: the cell geometry behind aggregate tiers and region requests.
/// </summary>
public sealed class CatalogGrid
{
    /// <summary>The wire index, assigned by canonicalization from the grid's geometry.</summary>
    public int Idx { get; init; }

    /// <summary>World-space origin of cell (0, 0).</summary>
    public double[] Origin { get; init; }

    /// <summary>Cell edge length in world units.</summary>
    public double Cell { get; init; }

    /// <summary>Cell counts per axis.</summary>
    public int[] Dims { get; init; }

    /// <summary>The archetype indices placed on this grid.</summary>
    public int[] Archetypes { get; init; }
}

/// <summary>
/// A metric the server publishes in its statistics block.
/// </summary>
public sealed class CatalogMetric
{
    /// <summary>The wire index, assigned by canonicalization from the ordinal sort of <see cref="Name"/>.</summary>
    public int Idx { get; init; }

    /// <summary>The metric's dotted name, for example <c>tick.p99</c>.</summary>
    public string Name { get; init; }

    /// <summary>The unit the value is expressed in, for example <c>ms</c>.</summary>
    public string Unit { get; init; }

    /// <summary>How the value is encoded.</summary>
    public CatalogCodec Codec { get; init; }
}
