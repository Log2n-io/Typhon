using System;

namespace SwgTatooine.Replication;

/// <summary>
/// What a connected client sees of Tatooine, declared through the public replication API and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is the blueprint claim.</b> It contains no framing, no codec arithmetic, no varint, no socket: an application says which archetypes are
/// replicated, which of their fields travel, how position is quantized and who may look at what — and the engine does the rest. If anything below starts to
/// look like protocol code, the API has failed rather than the demo.
/// </para>
/// <para>
/// <b>Five archetypes, three shapes.</b> Creatures, city NPCs and players move, so they carry motion and change groups; lairs and world objects never move,
/// so they are sent once on enter and never updated — which costs a client nothing per tick and is the case that most easily goes unnoticed if the projection
/// compiler treats "no change group" as an error rather than as a shape.
/// </para>
/// </remarks>
public static class TatooineReplication
{
    /// <summary>The profile every session is bound to: the whole planet, every archetype.</summary>
    /// <remarks>
    /// A god camera, which is what the demo shows. A real client would use a near/far tier around its own position; that is Phase 2's observer work, and the
    /// declaration below is deliberately the simplest thing that proves the path.
    /// </remarks>
    public const string GodProfile = "god-world";

    /// <summary>The session kind a client names in <c>HELLO</c>.</summary>
    public const string GodKind = "god";

    /// <summary>The fastest anything on Tatooine moves, in metres per second — a mounted player.</summary>
    /// <remarks>
    /// It sizes the motion codec: the teleport threshold is what separates "it moved" from "it was put somewhere else", and the velocity width is derived from
    /// it together with the tick period. Declaring it too high wastes a bit per segment; too low turns a sprint into a teleport.
    /// </remarks>
    private const double MaxSpeedMps = 12.0;

    /// <summary>Declares everything a client can see.</summary>
    /// <param name="subs">The runtime's registry, before <c>Start</c>.</param>
    public static void Declare(SubscriptionsRegistry subs)
    {
        ArgumentNullException.ThrowIfNull(subs);

        subs.Sessions.Kinds(GodKind);

        subs.Archetype<Creature>(a => a
            .Motion(Creature.Bounds, m => m.Tolerance(0.05).Teleport(MaxSpeedMps))
            .OnEnter(Creature.Ai, x => x.AggroRadius, Codec.F16, name: "aggro")
            .Field(Creature.Ai, x => x.Mode, Codec.U8, name: "mode")
            .Fraction(Creature.Vitals, v => v.Health, v => v.MaxHealth, bits: 8, name: "hp", group: "vitals"));

        subs.Archetype<CityNpc>(a => a
            .Motion(CityNpc.Bounds, m => m.Tolerance(0.05).Teleport(MaxSpeedMps))
            .Field(CityNpc.Ai, x => x.Mode, Codec.U8, name: "mode"));

        subs.Archetype<Player>(a => a
            .Motion(Player.Bounds, m => m.Teleport(MaxSpeedMps))
            .Field(Player.State, s => s.Activity, Codec.U8, name: "activity")
            .Fraction(Player.Vitals, v => v.Health, v => v.MaxHealth, bits: 8, name: "hp", group: "vitals"));

        subs.Archetype<CreatureLair>(a => a
            .Position(CreatureLair.Bounds)
            .OnEnter(CreatureLair.Spawner, l => l.CreatureTemplate, Codec.U16, name: "template"));

        subs.Archetype<WorldObject>(a => a
            .Position(WorldObject.Bounds)
            .OnEnter(WorldObject.Struct, s => s.Kind, Codec.U8, name: "kind")
            .OnEnter(WorldObject.Struct, s => s.OwnerRegion, Codec.I16, name: "region"));

        subs.Profile(GodProfile, p => p
            .World()
            .Of<Creature>()
            .Of<CityNpc>()
            .Of<Player>()
            .Of<CreatureLair>()
            .Of<WorldObject>());
    }

    /// <summary>
    /// Binds every session that opens to <see cref="GodProfile"/>.
    /// </summary>
    /// <param name="tick">The tick context of the system this is called from.</param>
    /// <remarks>
    /// A session with no profile is in no tick's session set and receives nothing, so this is not optional wiring — it is the moment a connection becomes a
    /// viewer. The request is staged and applied by the next tick's prologue, which is what makes it safe to call from a system on any worker.
    /// </remarks>
    public static void BindOpenedSessions(TickContext tick)
    {
        var subs = tick.Subscriptions;
        if (subs == null)
        {
            return;
        }

        foreach (ref readonly var e in subs.SessionEvents)
        {
            if (e.Kind == SessionEventKind.Opened)
            {
                subs.Session(e.Session).Profile(GodProfile);
            }
        }
    }
}
