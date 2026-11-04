using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace SwgTatooine.Server;

/// <summary>
/// A player installation's truth (crafting-build.md § 10): whose, on which lot, what it draws, at what rate into what hopper, since when, and until when it
/// is paid. Versioned — it is value: the running time paid for and the units it made are the owner's — and written only by <c>Workshop</c>.
/// </summary>
/// <remarks>
/// <b>Not positioned.</b> What the world sees is its <see cref="InstallationMarker"/>, a static, replicated view rebuilt from this one on reopen, so this
/// archetype stays on the non-spatial side of <c>EW-02</c>: the inventory endpoint and the audits read it off the tick. 0 means no data in every field.
/// </remarks>
[Component("Swg.InstallationCore", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct InstallationCore
{
    /// <summary>The character's raw id.</summary>
    [Field, Index(AllowMultiple = true)] public long Owner;

    /// <summary>Its lot (<see cref="Installations.LotOf"/>): one installation a lot.</summary>
    [Field] public long Lot;

    /// <summary>The world tick it last settled at: what it held then is <see cref="BankedMilli"/>.</summary>
    [Field] public long T0;

    /// <summary>Thousandths of a unit held at <see cref="T0"/>.</summary>
    [Field] public long BankedMilli;

    /// <summary>The world tick its upkeep runs out at: it makes nothing past it.</summary>
    [Field] public long PaidUntil;

    /// <summary>Units it makes a running hour, and its hopper — its kit's stats.</summary>
    [Field] public int RatePerHour;

    [Field] public int Hopper;

    /// <summary>The kit's own history, given back at a pick-up: who made it, its serial and life factor.</summary>
    [Field] public long KitCrafter;

    [Field] public int KitSerial;

    /// <summary>Where it stands: the lot's centre, and the ground there.</summary>
    [Field] public float X;

    [Field] public float Z;

    [Field] public float Y;

    /// <summary>The realm its lot is on.</summary>
    [Field] public ushort Realm;

    /// <summary>The material template it draws (an extractor).</summary>
    [Field] public ushort Family;

    /// <summary><see cref="InstallationKind"/>.</summary>
    [Field] public byte Kind;

    /// <summary>The kit's template, given back at a pick-up.</summary>
    [Field] public ushort KitTemplate;

    [Field] public short KitLifeMilli;
}

/// <summary>An installation's marker, where it stands. Never moves.</summary>
[Component("Swg.InstallationPlacement", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct InstallationPlacement : IGroundPlacement
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    /// <summary>Altitude, metres, as a structure's (<see cref="StructurePlacement.Y"/>).</summary>
    [Field, Replicate(CodecKind.Quant, Min = Ground.MinM, Max = Ground.MaxM, Bits = 16, Name = "y")]
    public float Y;

    public readonly float X => (Bounds.MinX + Bounds.MaxX) * 0.5f;

    public readonly float Z => (Bounds.MinY + Bounds.MaxY) * 0.5f;

    public void SetAt(float x, float z, float y, float halfExtent) => Place.At(ref Bounds, ref Y, x, z, y, halfExtent);
    readonly float IGroundPlacement.Y => Y;
}

/// <summary>
/// What a client draws of an installation: its kind, its asset and heading — the fields a structure carries, so it is drawn as one — what it draws, and
/// its state, written by <c>Workshop</c> when it changes.
/// </summary>
[Component("Swg.InstallationView", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct InstallationView
{
    [Field, OnEnter(CodecKind.U8, Name = "kind", Saturate = true)] public int Kind;

    [Field, OnEnter(CodecKind.U8, Name = "yaw", Saturate = true)] public int Yaw;

    [Field, OnEnter(CodecKind.U8, Name = "asset", Saturate = true)] public int Asset;

    [Field, OnEnter(CodecKind.U16, Name = "family", Saturate = true)] public int Family;

    /// <summary><see cref="InstallationState"/>.</summary>
    [Field, Replicate(CodecKind.U8, Name = "state", Saturate = true)] public int State;

    /// <summary>The installation's raw id, for nothing but the server's own bookkeeping: not replicated.</summary>
    [Field] public long Core;
}

/// <summary>The realm an installation's marker is in.</summary>
[Component("Swg.InstallationRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct InstallationRealm
{
    [Field]
    [RealmKey]
    public ushort Value;

    public InstallationRealm(ushort value) => Value = value;
}
