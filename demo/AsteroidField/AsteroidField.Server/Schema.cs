using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace AsteroidField.Server;

// The world: a flat asteroid belt on the X/Y plane (a Unity client draws it on X/Z). Positions are metres, velocities metres per second.

/// <summary>Where something is and how it moves. The spatial field is what the engine indexes and what replication's motion segments describe.</summary>
[Component("AsteroidField.Body", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Body
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    /// <summary>Velocity, metres per second.</summary>
    [Field]
    public float Vx;

    [Field]
    public float Vy;

    /// <summary>Heading in radians, 0 along +Y (a ship's nose); replicated as an angle. An asteroid leaves it 0.</summary>
    [Field]
    public float Angle;

    [Field]
    public float Pad;
}

/// <summary>An asteroid's rock: how big, and how much ore is left in it.</summary>
[Component("AsteroidField.Rock", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Rock
{
    [Field]
    public ushort Ore;

    /// <summary>1 (pebble) to 4 (boulder): the client scales its mesh by it. Sent once, on enter.</summary>
    [Field]
    public byte Size;

    [Field]
    public byte Pad;

    [Field]
    public int Pad2;
}

/// <summary>Who flies a ship, what it is mining, and what it holds.</summary>
[Component("AsteroidField.Pilot", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Pilot
{
    /// <summary>The session flying it (<see cref="SessionId.Value"/>), 0 for a drone.</summary>
    [Field]
    public uint Session;

    /// <summary>The asteroid being mined, or <see cref="EntityId.Null"/>. Replicated as the asteroid's netId, so every client draws the beam.</summary>
    [Field]
    public EntityId Target;

    /// <summary>Ore in the hold. Owner-only: the session flying it sees it, nobody else.</summary>
    [Field]
    public int Cargo;

    /// <summary>Credits earned by selling cargo. Owner-only.</summary>
    [Field]
    public int Credits;

    /// <summary>1 for a drone the server flies, 0 for a player's ship.</summary>
    [Field]
    public byte Drone;

    [Field]
    public byte Pad;

    [Field]
    public ushort Pad2;
}

[Archetype]
public partial class Asteroid : Archetype<Asteroid>
{
    public static readonly Comp<Body> Body = Register<Body>();
    public static readonly Comp<Rock> Rock = Register<Rock>();
}

[Archetype]
public partial class Ship : Archetype<Ship>
{
    public static readonly Comp<Body> Body = Register<Body>();
    public static readonly Comp<Pilot> Pilot = Register<Pilot>();
}

// ── What a client sends ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Stick input, sent every frame and coalesced: only the newest counts.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Steer
{
    /// <summary>-1 (reverse) to 1 (full thrust).</summary>
    public float Thrust;

    /// <summary>-1 (left) to 1 (right).</summary>
    public float Turn;
}

/// <summary>Start mining the asteroid with this netId (0 stops). The server checks the client was shown it, and the range.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Mine
{
    public uint Target;
}

/// <summary>Sell the hold's ore for credits.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Sell
{
    public byte Unused;
}

// ── What the server tells ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>An asteroid was mined out: every client near it draws the burst. Best effort — a missed burst is a missed effect, not lost state.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Depleted
{
    public float X;
    public float Y;
    public byte Size;
}

/// <summary>The hold is full. Reliable, to the ship's pilot only: a pilot who missed it would keep mining nothing.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CargoFull
{
    public EntityId Ship;
    public int Cargo;
}
