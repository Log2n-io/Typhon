using System;
using System.Numerics;

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

    /// <summary>
    /// A small-view profile: the populated parts of the world a player cares about, without the scenery.
    /// </summary>
    /// <remarks>
    /// <b>A proxy for a player's view, not a player's view.</b> A real client watches a region around itself, which is a <c>Sphere</c> or <c>ClientRegion</c>
    /// observer — Phase 1 resolves the <c>World</c> observer and refuses the others, so spatial interest cannot be measured yet. What this profile does give
    /// is a session whose watched set is a fraction of the world's (players and city NPCs, ~1.5 k of ~16.7 k), which is the property that matters for asking
    /// how per-session cost scales with view size. Anything measured through it should be read as "a session with a small view", never as "a player".
    /// </remarks>
    public const string PlayerProfile = "player-lite";

    /// <summary>The session kind a small-view client names in <c>HELLO</c>.</summary>
    public const string PlayerKind = "player";

    /// <summary>How far a player sees, in metres.</summary>
    /// <remarks>
    /// Chosen as a plausible awareness range for a ground game at this world scale, not measured from anything: what it is here for is that a player's view
    /// is a DISC rather than the world, and the exact figure only moves the constant. Tatooine's cells are 256 m, so a disc of this size spans a handful of
    /// them and the cluster index has something to reject.
    /// </remarks>
    private const double PlayerRadiusM = 192d;

    /// <summary>How far a player keeps seeing something it already saw, in metres.</summary>
    /// <remarks>
    /// The band between this and <see cref="PlayerRadiusM"/> is what stops an entity on the boundary entering and leaving on alternate ticks. Every
    /// re-entry costs a full enter record, so thrash is bandwidth rather than merely noise.
    /// </remarks>
    private const double PlayerLeaveRadiusM = 208d;

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

        subs.Sessions.Kinds(GodKind, PlayerKind);

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

        subs.Profile(PlayerProfile, p => p
            .Sphere(PlayerRadiusM, PlayerLeaveRadiusM)
            .Of<Player>()
            .Of<CityNpc>()
            .Of<Creature>());
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
                // By kind, so one run can carry both shapes and a measurement can say which it measured.
                subs.Session(e.Session).Profile(e.SessionKind == PlayerKind ? PlayerProfile : GodProfile);
            }
        }
    }

    /// <summary>
    /// Places every player session's disc for this tick, spreading the sessions over the world's players.
    /// </summary>
    /// <param name="tick">The tick context of the system this is called from.</param>
    /// <param name="viewpoints">Where the world's players are, this tick.</param>
    /// <remarks>
    /// <para>
    /// <b>Sessions are spread across DIFFERENT players on purpose.</b> Placing them all at one point would give every session the same disc, and identical
    /// views are the one case the shared-frame path serves at the cost of one — so a measurement taken that way would report a per-session cost that no real
    /// population has. Spreading them is what makes the numbers mean something.
    /// </para>
    /// <para>
    /// A session with no viewpoint sees nothing at all, so this runs every tick for every open session rather than once at admission: the players move, and
    /// a disc left where a player was is a view of somewhere they have left.
    /// </para>
    /// </remarks>
    private static long _placeTicks;

    public static void PlacePlayerSessions(TickContext tick)
    {
        var subs = tick.Subscriptions;
        var tx = tick.Transaction;
        if (subs == null || tx == null)
        {
            return;
        }

        // How much of the interest resolution was shared, once every thousand ticks. Printed rather than kept, because the answer decides whether a
        // measurement that shows no change means "the sharing does not pay" or "the sharing never happened" — and those call for opposite next steps.
        if (++_placeTicks % 300 == 0)
        {
            var (cells, shared) = subs.InterestSharingLastTick;

            // Error, not Out: a redirected stdout is block-buffered and this process is stopped rather than asked to exit, so the buffer is never flushed
            // and the diagnostic is lost exactly when it is being collected.
            var (difference, full) = subs.GatherShape;
            var (visited, carried) = subs.GatherSlots;
            var (projected, dormant) = subs.ProjectionBlocks;
            Console.Error.WriteLine(
                $"  interest sharing: {cells} cells resolved, {shared} sessions shared; gathers: {difference} difference, {full} full; "
                + $"slots: {visited} read, {carried} carried; blocks: {projected} projected, {dormant} dormant, sleeping {subs.SleepingClusters}");
            var vp = subs.VisitParts;
            Console.Error.WriteLine($"  visit made of: entered {vp.Entered}, changed {vp.Changed}, owed {vp.Owed}");

            // Per PUBLISHED frame, not per tick: a session with nothing to say produces no frame, and dividing by ticks would report the flow of the
            // sessions that spoke as if it were everyone's. The ratio of the first two is the question — a view filling emits enters and few leaves.
            var ef = subs.EnterFlow;
            var vf = subs.ViewFill;
            var perFrame = vf.Frames == 0 ? 0d : 1d / vf.Frames;
            Console.Error.WriteLine(
                $"  enter flow: {ef.Entered} entered, {ef.Left} left, {ef.Deferred} deferred over {vf.Frames} frames "
                + $"({ef.Entered * perFrame:F1} / {ef.Left * perFrame:F1} / {ef.Deferred * perFrame:F1} per frame)");
            Console.Error.WriteLine($"  view fill: {vf.Known:F0} entities known per frame, {vf.Owed:F1} enters owed");
            var cc = subs.ClusterCandidates;
            Console.Error.WriteLine($"  cluster candidates: {cc.Collected} collected, {cc.Accepted} accepted");
            var ee = subs.EpochEnter;
            Console.Error.WriteLine($"  epoch enter: {ee.MeanUs:F1} us mean over {ee.Chunks} chunks");
            var fs = subs.FrameSpan;
            Console.Error.WriteLine($"  frame span: {fs.SpanMs:F2} ms wall, {fs.BusyMs:F2} ms busy, {fs.Concurrency:F1} concurrent, start spread {fs.StartSpreadMs:F2} ms");
            var pm = subs.FramePrologueMs;
            Console.Error.WriteLine($"  frame prologue: {pm.Prologue:F2} ms/tick serial (sweep {pm.Sweep:F2}, prepare {pm.Prepare:F2})");
            var fb = subs.FrameBalance;
            Console.Error.WriteLine($"  frame balance: {fb.Effective:F1} effective workers, {fb.Efficiency * 100d:F0} % efficiency over {fb.Ticks} ticks");
            var census = subs.ShareCensus;
            var ratio = census.ChangedSlots == 0 ? 0d : (double)census.Records / census.ChangedSlots;
            Console.Error.WriteLine($"  share census: {census.ChangedSlots} changed slots, {census.Records} records emitted, ratio {ratio:F1}x");

            var sharedRuns = subs.SharedRuns;
            if (sharedRuns.Built > 0 || sharedRuns.Runs > 0)
            {
                var covered = census.Records == 0 ? 0d : 100d * sharedRuns.Records / census.Records;
                Console.Error.WriteLine(
                    $"  shared runs: {sharedRuns.Runs} referenced carrying {sharedRuns.Records} records ({covered:F1}% of all records), "
                    + $"{sharedRuns.Refused} clusters refused, {sharedRuns.Built} records encoded once");
                var miss = subs.SharedRunMisses;
                Console.Error.WriteLine($"  shared misses: gated {miss.Gated}, no run {miss.NoRun}, not reached {miss.NotReached}");
                var sk = subs.SharedRunSkips;
                Console.Error.WriteLine($"  shared build: {sk.Published} published, skipped: released {sk.Released}, init {sk.Init}, no change {sk.NoChange}");
            }
            var gs = subs.GatherRunShape;
            Console.Error.WriteLine($"  gather shape: {gs.Walked} runs, {gs.Empty} empty");
            var sm = subs.StaleMask;
            Console.Error.WriteLine($"  stale change-mask: {sm.Slots} slots read over {sm.Runs} runs");
            var fc = subs.FullGatherCauses;
            Console.Error.WriteLine($"  full-gather causes: reset {fc.Reset}, forced {fc.Forced}, incomplete {fc.Incomplete}, behind {fc.Behind}");
            var ph = subs.FramePhases;
            if (ph.Gather + ph.Encode > 0d)
            {
                Console.Error.WriteLine(
                    $"  frame phases (ms CPU, cumulative): gather {ph.Gather:F0}, select {ph.Select:F0}, sweep {ph.Sweep:F0}, sort {ph.Sort:F0}, "
                    + $"encode {ph.Encode:F0}, publish {ph.Publish:F0}");
            }
        }

        // NOT disposed: the accessor comes from the TICK's transaction, which owns it and releases it. Disposing one taken from a transaction this
        // method did not create tears down the cached EntityMap and chunk accessors mid-tick, which stops later systems reading.
        var accessor = tx.For<Player>();
        {
            var enumerator = accessor.GetClusterEnumerator();
            var cluster = default(ClusterRef<Player>);
            var occupancy = 0UL;
            var wraps = 0;

            foreach (var session in subs.OpenSessions)
            {
                if (!string.Equals(subs.SessionKindOf(session), PlayerKind, StringComparison.Ordinal))
                {
                    continue;
                }

                // The next player in the walk, wrapping when the sessions outnumber them. Advancing the SAME walk across sessions is what spreads the discs:
                // placing them all on one player would make every view identical, and identical views are the case the shared-frame path serves at the cost
                // of one — a measurement taken that way reports a per-session cost no real population has.
                var found = false;
                while (!found)
                {
                    while (occupancy == 0)
                    {
                        if (!enumerator.MoveNext())
                        {
                            // BOUNDED. The old form restarted the walk and only gave up when a FRESH enumerator yielded no cluster at all, so a population
                            // whose clusters all happened to be empty — a cluster whose last occupant migrated out, before the drain releases it — spun this
                            // loop forever on the tick path with no progress and nothing to report.
                            if (++wraps > 1)
                            {
                                return;
                            }

                            enumerator = accessor.GetClusterEnumerator();
                            if (!enumerator.MoveNext())
                            {
                                return;
                            }
                        }

                        cluster = enumerator.Current;
                        occupancy = cluster.OccupancyBits;
                    }

                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

#pragma warning disable TYPHON009
                    var placements = cluster.GetSpan(Player.Bounds);
#pragma warning restore TYPHON009
                    ref readonly var placement = ref placements[slot];
                    var b = placement.Bounds;
                    subs.Place(session, new Vector3D((b.MinX + b.MaxX) * 0.5, (b.MinY + b.MaxY) * 0.5, 0d));
                    found = true;
                }
            }
        }
    }
}
