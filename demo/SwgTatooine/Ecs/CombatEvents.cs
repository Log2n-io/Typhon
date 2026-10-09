using System.Runtime.InteropServices;

namespace SwgTatooine;

/// <summary>
/// One thing that happened to somebody else's entity: damage landed, or a mission paid out. The demo's only
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
/// <b>One queue, not four.</b> Damage to a creature, to a lair and to a player, and a mission's payout, differ only in
/// which archetype the target lives in and what the amount means. Splitting them would give the consumer four drains and
/// four <c>Span</c>s to size, and the DAG four edges that all say the same thing. <see cref="TargetKind"/> carries the
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
    /// mission payout has no attacker.
    /// </summary>
    /// <remarks>
    /// Carried because the consumer needs it for two separate reasons: a damaged creature turns on its attacker (which is
    /// how a lair gets pulled), and a kill pays whoever landed the last hit. Both were impossible before, because a
    /// shooter's identity was never captured anywhere (gap G7).
    /// </remarks>
    public EntityId Attacker;

    /// <summary>Hit points for <see cref="CombatEventKind.Damage"/>, credits for <see cref="CombatEventKind.MissionReward"/>, unused otherwise.</summary>
    public int Amount;

    /// <summary>Where <see cref="CombatEventKind.MissionAssigned"/> sends the player; unused by the other kinds.</summary>
    public float X;

    /// <summary>See <see cref="X"/>.</summary>
    public float Z;

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

    /// <summary>Credits a completed destroy mission pays its owner. Written to <c>Inventory</c>, so the tick carries a WAL record.</summary>
    public const byte MissionReward = 1;

    /// <summary>
    /// A destroy mission has just been built for this player, at <see cref="CombatEvent.X"/>, <see cref="CombatEvent.Z"/>: go there and break it.
    /// </summary>
    /// <remarks>
    /// <b>The half of the mission loop that never existed.</b> A mission lair teleported to a point one to two kilometres from a player and then nothing told
    /// the player, so a player in <c>PlayerActivity.Combat</c> walked to a random point inside a point-of-interest disc instead — kilometres across, against a
    /// 75 m weapon. Measured before this event existed: 2 121 weapon cycles with nothing in range and not one shot fired in a 120-tick run. Without it
    /// AC-1 cannot be met by the simulation at all, only by a test that places a player by hand.
    /// </remarks>
    public const byte MissionAssigned = 2;
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
}
