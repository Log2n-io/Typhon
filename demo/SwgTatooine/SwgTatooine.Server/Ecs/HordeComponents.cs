using System.Runtime.InteropServices;

namespace SwgTatooine.Server;

/// <summary>Where a horde is in its life (horde.md § 1).</summary>
public static class HordePhase
{
    /// <summary>Announced: the fortress stands, the countdown runs, nothing has marched yet.</summary>
    public const byte Incoming = 1;

    /// <summary>The waves are out: marching, or fighting round the target.</summary>
    public const byte Active = 2;

    /// <summary>Every wave spawned and killed.</summary>
    public const byte Won = 3;

    /// <summary>The duration ran out with raiders alive.</summary>
    public const byte Lost = 4;

    /// <summary>Endless waves at the end of their duration, or ended by an operator.</summary>
    public const byte Ended = 5;

    /// <summary>Whether the event is over: its fortress and raiders are gone, and nobody may answer it.</summary>
    public static bool IsOver(byte phase) => phase is Won or Lost or Ended;

    /// <summary>The name a phase is shown by, over HTTP and in the stats.</summary>
    public static string NameOf(byte phase) => phase switch
    {
        Incoming => "incoming",
        Active => "active",
        Won => "won",
        Lost => "lost",
        Ended => "ended",
        _ => "none",
    };
}

/// <summary>
/// A raid on a city (horde.md): where it comes from and goes, when, the operator's knobs, and its counters. The truth an event survives a restart by.
/// </summary>
/// <remarks>
/// <b><see cref="StorageMode.Versioned"/>, non-spatial</b>, written by the <c>Horde</c> system alone, in its side transaction: the countdown and the
/// waves run from the world tick stored here, so a server that stops before or during the event picks it up where it was. The fortress and the raiders are
/// its view, spawned from it.
/// </remarks>
[Component("Swg.HordeCore", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HordeCore
{
    /// <summary>The city attacked: its index on the planet.</summary>
    [Field] public int City;

    /// <summary>See <see cref="HordePhase"/>.</summary>
    [Field] public byte Phase;

    /// <summary>Raiders killed are replaced at the fortress until the event ends (1), or only the waves spawn (0).</summary>
    [Field] public byte Respawn;

    /// <summary>The level raiders are spawned at, and how far either side of it each one may be drawn.</summary>
    [Field] public byte Level;

    [Field] public byte LevelSpread;

    /// <summary>The world tick the waves start at, and the one the event ends at whatever is left.</summary>
    [Field] public long StartTick;

    [Field] public long EndTick;

    /// <summary>Where the fortress stands, and its quarter turn (its gate faces the target).</summary>
    [Field] public float FortX;

    [Field] public float FortZ;

    [Field] public int FortYaw;

    /// <summary>The target: the plaza between the city's shuttleport and its town hall, where the raiders go and the defenders rally.</summary>
    [Field] public float TargetX;

    [Field] public float TargetZ;

    /// <summary>Raiders per wave, the ticks between two waves, and how many waves (0: endless until <see cref="EndTick"/>).</summary>
    [Field] public int WaveSize;

    [Field] public int WavePeriodTicks;

    [Field] public int Waves;

    /// <summary>Waves out so far, and the tick the next one is due.</summary>
    [Field] public int WavesSpawned;

    [Field] public long NextWaveTick;

    /// <summary>The <see cref="CreatureTemplates"/> raiders are drawn from, as bits (only aggressive ones).</summary>
    [Field] public int TemplateMask;

    /// <summary>The attendance asked for: commitments past it are refused.</summary>
    [Field] public int TargetDefenders;

    /// <summary>How long a committed character has to arrive, and how long a decayed one waits before it may answer again, seconds.</summary>
    [Field] public int CommitTimeoutS;

    [Field] public int CooldownS;

    /// <summary>Raiders spawned and killed so far.</summary>
    [Field] public int Spawned;

    [Field] public int Killed;

    /// <summary>The fortress structure's raw id, and the raid lairs' (one per template), 0 before they stand or after they are gone.</summary>
    [Field] public long Fortress;

    [Field] public long LairA;

    [Field] public long LairB;

    /// <summary>The town hall the raiders break (horde.md § 10): its raw id and centre; 0 for a city without one.</summary>
    [Field] public long Hall;

    [Field] public float HallX;

    [Field] public float HallZ;

    /// <summary>The siege's hit points, the event's own: the town hall's (0 is the town fallen) and the fortress's (0 is the raid beaten).</summary>
    [Field] public int HallHp;

    [Field] public int HallMaxHp;

    [Field] public int FortHp;

    [Field] public int FortMaxHp;

    /// <summary>The share of each wave that holds the fortress rather than march, in percent (horde.md § 10).</summary>
    [Field] public byte GarrisonPercent;

    /// <summary>
    /// The raiders' health and hit against a creature of their level, in percent (horde.md § 10, Loïc 2026-11-05: stronger raiders, fewer defenders);
    /// 0 is 100 % — an event written before the knobs existed.
    /// </summary>
    [Field] public int RaiderHealthPct;

    [Field] public int RaiderHitPct;
}

/// <summary>What a responder's commitment is now (horde.md § 5).</summary>
public static class ResponderState
{
    /// <summary>Answered, on its way.</summary>
    public const byte Committed = 1;

    /// <summary>Reached the target while committed.</summary>
    public const byte Arrived = 2;

    /// <summary>Did not arrive in time, or was downed far away: out of the count, waiting its cooldown.</summary>
    public const byte Decayed = 3;
}

/// <summary>
/// One character's answer to a horde's call (horde.md § 5, D3): committed, arrived or decayed. Persisted, so a restart does not forget who is on the way and
/// invite as many again.
/// </summary>
[Component("Swg.HordeResponder", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HordeResponder
{
    /// <summary>The event's raw id.</summary>
    [Field, Index(AllowMultiple = true)] public long Event;

    /// <summary>The character's raw id.</summary>
    [Field] public long Character;

    /// <summary>The world ticks it answered and arrived at (0 before), and the one its cooldown ends at once decayed.</summary>
    [Field] public long CommittedTick;

    [Field] public long ArrivedTick;

    [Field] public long CooldownUntilTick;

    /// <summary>See <see cref="ResponderState"/>.</summary>
    [Field] public byte State;

    /// <summary>
    /// The world tick a committed answer must have arrived by (horde.md § 5): its own, from the distance it answered at. 0 for an answer written before it
    /// existed: the event's commit timeout from <see cref="CommittedTick"/>.
    /// </summary>
    [Field] public long DeadlineTick;
}
