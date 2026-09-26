using System.Collections.Generic;

namespace Typhon.Protocol;

/// <summary>
/// The wire protocol version. Negotiated separately from the package version: a client and server agree on this, not on a NuGet number.
/// </summary>
public sealed class CatalogProtocolVersion
{
    /// <summary>Major version. A mismatch is fatal — the wire shape differs.</summary>
    public int Major { get; init; }

    /// <summary>Minor version. A client of a lower minor decodes a higher one, ignoring blocks and fixed-width fields it does not know.</summary>
    public int Minor { get; init; }
}

/// <summary>
/// Which application this catalog describes, so a client can tell one server's contract from another's.
/// </summary>
public sealed class CatalogApp
{
    /// <summary>The application's name.</summary>
    public string Name { get; init; }

    /// <summary>The application's own revision of its replication declarations.</summary>
    public int Revision { get; init; }
}

/// <summary>
/// Tick timing a client needs before it receives its first frame.
/// </summary>
public sealed class CatalogTick
{
    /// <summary>The server's nominal tick period in microseconds; <c>PERIOD</c> in a frame reports the actual one under time dilation.</summary>
    public int PeriodUs { get; init; }

    /// <summary>How often a client must send <c>PING</c>, in hertz.</summary>
    public int PingHz { get; init; }
}

/// <summary>
/// Server-wide ceilings a client acts on (W30). Everything else the server enforces stays out of the catalog.
/// </summary>
public sealed class CatalogLimits
{
    /// <summary>The largest server-to-client message, in bytes; a .NET or TCP client sizes its receive buffer once from it.</summary>
    public int FrameBytes { get; init; }

    /// <summary>The largest client-to-server message after <c>HELLO</c>, in bytes; the SDK batches <c>COMMANDS</c> under it or is closed with 1009.</summary>
    public int ClientMessageBytes { get; init; }

    /// <summary>How long a dropped session can be resumed, in milliseconds; the SDK chooses between resuming and a fresh connection from it.</summary>
    public int ResumeGraceMs { get; init; }
}

/// <summary>
/// Everything a client needs to decode a Typhon stream without any generated code or knowledge of the server's C# types: which archetypes are replicated,
/// which fields each carries, in what codec and group, plus events, commands, grids and metrics.
/// </summary>
/// <remarks>
/// <para>
/// It is called the <i>catalog</i>, not the schema: "schema" is Typhon's storage schema, which rules SCHEMA-01…07 constrain and which this deliberately does
/// not describe. The catalog travels in <c>WELCOME</c> as canonical JSON and is hashed so a returning client can offer the hash back and skip it.
/// </para>
/// <para>
/// <b>Nothing in here describes server memory.</b> No CLR type name and no offset appears anywhere — not the field's offset within its component, nor the
/// component's column offset within the cluster. A client decodes bytes; handing out the layout would couple the wire to storage and make renaming a C# type a
/// wire break.
/// </para>
/// </remarks>
public sealed class Catalog
{
    /// <summary>The wire protocol version this catalog is written for.</summary>
    public CatalogProtocolVersion Protocol { get; init; }

    /// <summary>The application the catalog describes.</summary>
    public CatalogApp App { get; init; }

    /// <summary>Tick timing.</summary>
    public CatalogTick Tick { get; init; }

    /// <summary>Server-wide ceilings a client acts on.</summary>
    public CatalogLimits Limits { get; init; }

    /// <summary>The session kinds the application declares (W21). Informational: the engine never interprets a kind.</summary>
    public string[] SessionKinds { get; init; }

    /// <summary>
    /// The realm kinds the application declares (<c>typhon.3</c>), in canonical order; a <c>REALM</c> block names its kind by index here. <c>""</c> is the
    /// default kind; absent means it alone.
    /// </summary>
    public string[] RealmKinds { get; init; }

    /// <summary>The replicated archetypes, ordered so that each one's position is its <see cref="CatalogArchetype.Idx"/>.</summary>
    public CatalogArchetype[] Archetypes { get; init; }

    /// <summary>Enum value names by enum name. A value's index in the array is the integer that travels.</summary>
    public Dictionary<string, string[]> Enums { get; init; }

    /// <summary>The declared events, in index order.</summary>
    public CatalogEvent[] Events { get; init; }

    /// <summary>The commands a client may send, in index order.</summary>
    public CatalogCommand[] Commands { get; init; }

    /// <summary>The spatial grids.</summary>
    public CatalogGrid[] Grids { get; init; }

    /// <summary>The metrics the server publishes, in index order.</summary>
    public CatalogMetric[] Metrics { get; init; }
}
