using System.Runtime.InteropServices;

namespace SwgTatooine.Server;

/// <summary>
/// One thing that happened to somebody else's entity: damage landed, or a downed player's time was up. The demo's only
/// <c>EventQueue</c> payload (SWG-02).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an event rather than a write.</b> Until SWG-02 every exchange in this world was resolved by the archetype that
/// was going to be written, which meant inverting it: combat walked CREATURES and asked who was standing nearby, because
/// a system whose input is Player could not safely write a Creature. That inversion is what gap G8 records — any player
/// within 75 m damaged every creature in range, whatever it was doing, and nothing anywhere stored who was fighting whom.
/// </para>
/// <para>
/// The answer is <c>06-gameplay.md § 2</c> principle 3: a producer pushes the effect and the system that owns the target
/// applies it. Both combat producers are parallel systems over their own archetype and write nothing outside it; one
/// serial consumer drains the queue and opens each target. That keeps the cross-archetype write on a single thread, which
/// is what #907 is still open about, while the expensive half — the spatial query, the range test, the weapon cadence —
/// stays parallel where the entities are.
/// </para>
/// <para>
/// <b>One queue, not three.</b> Damage to a creature, to a lair and to a player differ only in
/// which archetype the target lives in. Splitting them would give the consumer three drains and
/// three <c>Span</c>s to size, and the DAG three edges that all say the same thing. <see cref="TargetKind"/> carries the
/// discrimination in one byte, and the consumer needs it anyway: an <see cref="EntityId"/> does not say which components
/// to write.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct CombatEvent
{
    /// <summary>The entity this happens to — the one the consumer opens.</summary>
    public EntityId Target;

    /// <summary>
    /// Who caused it: the player that fired, or the creature that attacked. <c>EntityId.Null</c> when nobody did — a
    /// revival has no attacker.
    /// </summary>
    /// <remarks>
    /// Carried because the consumer needs it for two separate reasons: a damaged creature turns on its attacker (which is
    /// how a lair gets pulled), and a kill pays whoever landed the last hit. Both were impossible before, because a
    /// shooter's identity was never captured anywhere (gap G7).
    /// </remarks>
    public EntityId Attacker;

    /// <summary>Hit points, for <see cref="CombatEventKind.Damage"/>.</summary>
    public int Amount;

    /// <summary>See <see cref="CombatEventKind"/>.</summary>
    public byte Kind;

    /// <summary>Which archetype <see cref="Target"/> belongs to. See <see cref="CombatTargetKind"/>.</summary>
    public byte TargetKind;
}

/// <summary>What a <see cref="CombatEvent"/> is.</summary>
internal static class CombatEventKind
{
    /// <summary>Hit points to subtract from the target's vitals.</summary>
    public const byte Damage = 0;

    /// <summary>
    /// A downed player's time is up (<see cref="PlayerVitals.UpTick"/>): a knocked-out one gets up with a fifth of its health, a dead one wakes whole as a clone
    /// outside the nearest city's shuttleport. Pushed by the player's own combat pass, applied in the drain, which owns the teleport.
    /// </summary>
    public const byte Revive = 1;
}

/// <summary>Which archetype a <see cref="CombatEvent.Target"/> lives in, so the consumer knows what to write.</summary>
/// <remarks>
/// It is not derivable from the <see cref="EntityId"/> at the cost the consumer can afford — and even if it were, naming
/// it at the producer is the honest form: the producer knows exactly what it aimed at.
/// </remarks>
/// <remarks>
/// <b>Public although <see cref="CombatEvent"/> is internal</b>, because it is the domain of a PUBLIC field:
/// <c>PlayerSession.TargetKind</c> is what a client sets through <c>SetTarget</c> and what the combat systems read, and a
/// caller outside this assembly that has to spell 0 or 1 by hand is one that breaks silently if these are renumbered.
/// </remarks>
public static class CombatTargetKind
{
    public const byte Creature = 0;

    public const byte Lair = 1;

    public const byte Player = 2;

    /// <summary>
    /// Something a client may select and inspect but nothing can damage: a building, a city NPC. Stored as the session's target and never fired at.
    /// </summary>
    /// <remarks>
    /// <b>Its own kind, because folding it into <see cref="Player"/> was a crash.</b> The shooter read <c>PlayerVitals</c> from whatever it was handed, and
    /// a building has none: the read threw, the strict tick abort stopped the world (tick 9 682 of a served demo, 2026-10-24). It was latent for as long as
    /// a session's player never fired; it fired the day it did.
    /// </remarks>
    public const byte Unshootable = 3;
}

/// <summary>
/// One death — a creature's, a camp foe's or a lair's — as the serial combat drain saw it: who killed what, of which level and kind, where (character-level.md
/// § 2). Captured there because nothing after it can: a broken wild lair is destroyed by the lair director later in the same tick.
/// </summary>
/// <remarks>
/// <b>Pushed into two queues</b>, <see cref="SimBridge.KillXpQueue"/> and <see cref="SimBridge.KillLootQueue"/>: <c>EventQueue.Drain</c> consumes, so one
/// queue cannot feed both the XP and the loot systems, and a death is a few a second, so the second push is free.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct KillEvent
{
    /// <summary>The Player body whose blow killed; <c>EntityId.Null</c> when the blow was a creature's. A crowd body is kept, and resolves to no character.</summary>
    public EntityId Killer;

    /// <summary>The creature, or the lair.</summary>
    public EntityId Victim;

    /// <summary>A creature's home lair (<see cref="CreatureHome.Lair"/>); the lair itself for a lair; <c>EntityId.Null</c> when it has none.</summary>
    public EntityId Lair;

    /// <summary>That lair's <see cref="Server.Lair.Mission"/> (a held mission), <c>EntityId.Null</c> for a wild lair.</summary>
    public EntityId Mission;

    /// <summary>The tick it died on.</summary>
    public long Tick;

    /// <summary>Where it died.</summary>
    public float X;

    public float Z;

    /// <summary>Its realm.</summary>
    public ushort Realm;

    /// <summary>See <see cref="KillVictimKind"/>.</summary>
    public byte VictimKind;

    /// <summary>Its content level, 1 to 50; 0 when it has none.</summary>
    public byte VictimLevel;

    /// <summary>Its creature template (<see cref="CreatureTemplates"/>): the brain's for a creature or foe, the lair's for a lair. What loot is made of.</summary>
    public byte Species;
}

/// <summary>What a <see cref="KillEvent"/> killed.</summary>
internal static class KillVictimKind
{
    public const byte Creature = 1;

    public const byte Foe = 2;

    public const byte Lair = 3;
}
