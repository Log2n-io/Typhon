using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Subscriptions.E2EHost;

// The world a live client test sees. Small on purpose — a dozen movers and a handful of rocks — and deterministic: every value is a function of the
// entity's index and the tick, so two runs send the same frames and a failure reproduces.

/// <summary>A mover's behaviour state, replicated as an enum of three bits.</summary>
public enum E2eMode : byte
{
    Idle = 0,
    Wander = 1,
    Chase = 2,
    Flee = 3,
}

[Component("Typhon.E2E.Bounds", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct E2eBounds
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    public float Speed;
}

[Component("Typhon.E2E.State", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct E2eState
{
    [Field]
    public byte Template;

    [Field]
    public E2eMode Mode;

    /// <summary>A flag stored as a byte: a reflection-measured <c>bool</c> offset is refused (SCHEMA-07).</summary>
    [Field]
    public byte Alerted;

    [Field]
    public ushort Level;

    [Field]
    public int Health;

    [Field]
    public int MaxHealth;

    /// <summary>A balance above 2⁵³ (W32): exact on every client, which a double would round.</summary>
    [Field]
    public ulong Credits;

    /// <summary>A signed 64-bit value, as a <c>vari64</c>.</summary>
    [Field]
    public long Debt;

    /// <summary>A double sent whole (<c>f64</c>).</summary>
    [Field]
    public double Rate;

    /// <summary>A point, sent as <c>f32 × 3</c> (W33).</summary>
    [Field]
    public Point3F Aim;
}

/// <summary>A mover's label: text in a group of its own, stored out of line (13 § 6), with a non-ASCII character in it.</summary>
[Component("Typhon.E2E.Label", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct E2eLabel
{
    [Field]
    public String64 Name;
}

[Archetype]
public partial class E2eMover : Archetype<E2eMover>
{
    public static readonly Comp<E2eBounds> Bounds = Register<E2eBounds>();
    public static readonly Comp<E2eState> State = Register<E2eState>();
    public static readonly Comp<E2eLabel> Label = Register<E2eLabel>();
}

[Archetype]
public partial class E2eRock : Archetype<E2eRock>
{
    public static readonly Comp<E2eBounds> Bounds = Register<E2eBounds>();
    public static readonly Comp<E2eState> State = Register<E2eState>();
}

/// <summary>Broadcast every fifth tick: an event a client must receive without asking for it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct E2ePulse
{
    public uint Seq;
    public ushort Kind;
    public ulong Stamp;
}

/// <summary>The command a client sends; the host answers it with <see cref="E2eEchoed"/>, which proves the round trip.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct E2eEcho
{
    public uint Value;
    public ushort Code;
    public ulong Token;
}

[StructLayout(LayoutKind.Sequential)]
public struct E2eEchoed
{
    public uint Value;
    public ushort Code;
    public ulong Token;
}
