using System.Runtime.InteropServices;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// One entry of the persisted realm catalog (Realms decision D-1): the IDENTITY of a named realm — the fields that decide which cell a position
/// files into — so a later open can rebuild the realm's spatial layer before the application has said anything, and refuse a registration that
/// would re-map every entity of it.
/// </summary>
/// <remarks>
/// <para><b>Realm 0 is not here.</b> It keeps its single-world record (<c>spatial.GridConfig</c>), so a database that never names a realm is
/// byte-for-byte what it was. The catalog table is created the first time a realm other than 0 is registered.</para>
/// <para><b>Identity only.</b> Policy (<see cref="RealmConfig.WhenUnobserved"/>, divisors) and the grid's tuning knobs decide when things run, never
/// where an entity is filed, so they are the application's to supply at each open — as for the realm-0 record.</para>
/// </remarks>
[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
[PublicAPI]
public struct RealmR1
{
    /// <summary>Fully-qualified schema name of this record ("Typhon.Schema.Realm").</summary>
    public const string SchemaName = "Typhon.Schema.Realm";

    /// <summary>The realm id.</summary>
    public int Id;

    /// <summary>Incarnation of the id: bumped when a retired id is reused (realm lifecycle). Zero today.</summary>
    public int Generation;

    /// <summary>World bounds, minimum corner.</summary>
    public double WorldMinX;

    /// <summary>See <see cref="WorldMinX"/>.</summary>
    public double WorldMinY;

    /// <summary>See <see cref="WorldMinX"/>.</summary>
    public double WorldMinZ;

    /// <summary>World bounds, maximum corner.</summary>
    public double WorldMaxX;

    /// <summary>See <see cref="WorldMaxX"/>.</summary>
    public double WorldMaxY;

    /// <summary>See <see cref="WorldMaxX"/>.</summary>
    public double WorldMaxZ;

    /// <summary>Cell edge length.</summary>
    public double CellSize;

    /// <summary>The cell-crossing hysteresis band, as a fraction of the cell (part of CC-02's contract, hence of identity).</summary>
    public float MigrationHysteresisRatio;

    /// <summary>Lifecycle state (Realms D5): <see cref="StateLive"/>, <see cref="StateClosing"/> (unregistered, not yet proven empty) or
    /// <see cref="StateRetired"/> (empty at an open; the id is free and a later registration reuses this row).</summary>
    public int State;

    /// <summary>A registered realm.</summary>
    public const int StateLive = 0;

    /// <summary>Unregistered; reopened as Closing until an open finds it empty after recovery.</summary>
    public const int StateClosing = 1;

    /// <summary>Retired: its id may be registered again.</summary>
    public const int StateRetired = 2;
}
