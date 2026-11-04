using System.Runtime.InteropServices;

namespace SwgTatooine.Server;

/// <summary>
/// A character's own waypoint (client.md C38): its name, what it is, the colour its owner chose, where, and the installation it follows when it is one's
/// pin. Created, deleted and made active by the owner's commands alone, in <c>Workshop</c>; read off the tick by <c>GET /swg/waypoints</c>.
/// </summary>
/// <remarks>
/// <b>Not positioned and not replicated.</b> Nobody but its owner sees it, and the owner reads it through the endpoint after each of its own commands —
/// nothing else changes it, so the body carries no revision for it. 0 means no data in every field: a colour of 0 is the kind's colour, an installation
/// of 0 none.
/// </remarks>
[Component("Swg.WaypointCore", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WaypointCore
{
    /// <summary>The character's raw id.</summary>
    [Field, Index(AllowMultiple = true)] public long Owner;

    /// <summary>The installation it follows (its raw id), or 0: then it stands at <see cref="X"/>, <see cref="Z"/>.</summary>
    [Field] public long Installation;

    /// <summary>The name its owner gave it: 1 to <see cref="Waypoints.NameMaxBytes"/> UTF-8 bytes.</summary>
    [Field] public String64 Name;

    [Field] public float X;

    [Field] public float Z;

    /// <summary><c>0xRRGGBB</c>, or 0 for its kind's colour.</summary>
    [Field] public uint Color;

    [Field] public ushort Realm;

    /// <summary><see cref="WaypointKind"/>.</summary>
    [Field] public byte Kind;

    /// <summary>1 for the character's active waypoint, the one drawn brighter; at most one a character.</summary>
    [Field] public byte Active;
}

/// <summary>What a stored waypoint is: the owner's own, or an installation's pin. The derived ones — missions, city structures — are never stored.</summary>
public static class WaypointKind
{
    public const byte Custom = 1;
    public const byte Installation = 2;
}
