using System;
using System.Collections.Generic;
using System.Numerics;

namespace SwgTatooine;

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
    /// <summary>The god camera's profile: the whole planet, every archetype, through a <c>World</c> observer.</summary>
    public const string GodProfile = "god-world";

    /// <summary>The session kind a client names in <c>HELLO</c>.</summary>
    public const string GodKind = "god";

    /// <summary>
    /// A player's profile: a disc around the session's player, over the archetypes that move — players, city NPCs and creatures, without the scenery.
    /// </summary>
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

    /// <summary>The replication grid's cell side (<see cref="SubscriptionsOptions.ReplicationCellM"/>): a third of <see cref="PlayerRadiusM"/>.</summary>
    public const double ReplicationCellM = PlayerRadiusM / 3d;

    /// <summary>The fastest anything on Tatooine moves, in metres per second — a mounted player.</summary>
    /// <remarks>
    /// It sizes the motion codec: the teleport threshold is what separates "it moved" from "it was put somewhere else", and the velocity width is derived from
    /// it together with the tick period. Declaring it too high wastes a bit per segment; too low turns a sprint into a teleport.
    /// </remarks>
    private const double MaxSpeedMps = 12.0;

    private static SubscriptionsCommands _pushCommands;

    /// <summary>
    /// The simulation's "this entity changed something a client sees" (ADR-067): a system that writes a replicated value calls it for the slot it wrote.
    /// </summary>
    /// <param name="cluster">The cluster being iterated.</param>
    /// <param name="slot">The entity's slot.</param>
    public static void Replicate<T>(in ClusterRef<T> cluster, int slot) where T : class => _pushCommands?.Replicate(in cluster, slot);

    /// <summary><see cref="Replicate{T}(in ClusterRef{T}, int)"/> for a set of slots.</summary>
    /// <param name="cluster">The cluster being iterated.</param>
    /// <param name="slots">The slots.</param>
    public static void Replicate<T>(in ClusterRef<T> cluster, ulong slots) where T : class => _pushCommands?.Replicate(in cluster, slots);

    /// <summary>Declares everything a client can see.</summary>
    /// <param name="subs">The runtime's registry, before <c>Start</c>.</param>
    /// <param name="automatic">Whether the engine detects changes itself instead of relying on the simulation's <c>Replicate</c> calls (experimental).</param>
    public static void Declare(SubscriptionsRegistry subs, bool automatic = false)
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

        // The god camera through a World observer, the players through a disc with no band: the anchor's slack is its hysteresis for observer motion
        // (push-model.md § 4.5).
        var detection = automatic ? PushDetection.Automatic : PushDetection.Explicit;
        subs.Profile(GodProfile, p => p
            .Detection(detection)
            .World()
            .Of<Creature>()
            .Of<CityNpc>()
            .Of<Player>()
            .Of<CreatureLair>()
            .Of<WorldObject>());

        subs.Profile(PlayerProfile, p => p
            .Detection(detection)
            .Sphere(PlayerRadiusM)
            .Of<Player>()
            .Of<CityNpc>()
            .Of<Creature>());
    }

    /// <summary>
    /// Binds every session that opens to <see cref="GodProfile"/>.
    /// </summary>
    /// <param name="tick">The tick context of the system this is called from.</param>
    /// <remarks>
    /// A session with no profile receives nothing, so this is not optional wiring — it is the moment a connection becomes a viewer. The request is staged
    /// and applied by the next tick's prologue, which is what makes it safe to call from a system on any worker.
    /// </remarks>
    public static void BindOpenedSessions(TickContext tick)
    {
        var subs = tick.Subscriptions;
        if (subs == null)
        {
            return;
        }

        _pushCommands = subs;

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
    /// <b>Sessions are spread across DIFFERENT players on purpose.</b> Placing them all at one point would give every session the same disc, and a
    /// measurement taken that way would report a per-session cost that no real population has. Spreading them is what makes the numbers mean something.
    /// </para>
    /// <para>
    /// A session with no viewpoint sees nothing at all, so this runs every tick for every open session rather than once at admission: the players move, and
    /// a disc left where a player was is a view of somewhere they have left.
    /// </para>
    /// </remarks>
    private static long _placeTicks;

    /// <summary>The runtime's scheduler, for the periodic worker-idle line.</summary>
    internal static DagScheduler Scheduler;
    private static (double InDispatchMs, double IdleMs, double ParkedMs, long Parks, long Backstops, long Spells, long Wakes) IdleFrom;
    private static long IdleTickFrom;
    private static (double ActiveMs, double TickWallMs, long Ticks, int Workers) _utilFrom;

    /// <summary>
    /// Per system over the report window, from the scheduler's telemetry ring: its span per tick, the worker time summed over its chunks, and how much of
    /// the pool that span left unused (span × pool − work). A span with idle workers can still be overlapped by a DAG sibling, so the unused figure is an
    /// upper bound on what the system wastes, not a measure of it.
    /// </summary>
    private static void ReportSystemEfficiency()
    {
        var ring = Scheduler.Telemetry;
        var systems = Scheduler.Systems;
        var pool = Math.Max(1, Scheduler.WorkerCount);
        var span = new double[systems.Length];
        var work = new double[systems.Length];
        var chunks = new long[systems.Length];
        var ran = new int[systems.Length];
        var transition = new double[systems.Length];
        var first = Math.Max(IdleTickFrom, ring.OldestAvailableTick);
        var ticks = 0;
        var wall = 0d;
        for (var t = first; t <= ring.NewestTick; t++)
        {
            ref readonly var tick = ref ring.GetTick(t);
            if (tick.ActualDurationMs <= 0f)
            {
                continue;
            }

            ticks++;
            wall += tick.ActualDurationMs;
            var metrics = ring.GetSystemMetrics(t);
            for (var i = 0; i < metrics.Length && i < systems.Length; i++)
            {
                if (metrics[i].WasSkipped)
                {
                    continue;
                }

                span[i] += metrics[i].DurationUs;
                work[i] += metrics[i].WorkUs;
                transition[i] += metrics[i].TransitionLatencyUs;
                chunks[i] += metrics[i].WorkersTouched;
                ran[i]++;
            }
        }

        if (ticks == 0)
        {
            return;
        }

        var order = new int[systems.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => span[b].CompareTo(span[a]));
        var line = new System.Text.StringBuilder();
        line.Append($"  system efficiency over {ticks} ticks ({wall / ticks:F2} ms/tick, pool {pool}): name span/work ms per tick, chunks, eff, unused worker-ms");
        for (var k = 0; k < Math.Min(14, order.Length); k++)
        {
            var i = order[k];
            if (ran[i] == 0)
            {
                continue;
            }

            var s = span[i] / 1000d / ticks;
            var w = work[i] / 1000d / ticks;
            var c = (double)chunks[i] / ran[i];
            var eff = s <= 0 ? 0 : w * 100d / (s * pool);
            line.Append($"\n    {systems[i].Name,-34} span {s,6:F3} work {w,7:F3} chunks {c,5:F1} eff {eff,5:F1} % unused {(s * pool) - w,7:F2}");
        }

        // The replication track in DAG order, whatever its cost: an empty stage still costs its hand-off.
        for (var i = 0; i < systems.Length; i++)
        {
            if (!systems[i].Name.StartsWith("Subscriptions", StringComparison.Ordinal))
            {
                continue;
            }

            var runs = Math.Max(1, ran[i]);
            line.AppendLine().Append($"    track {systems[i].Name,-28} ran {ran[i],4} span {span[i] / runs,7:F1} us transition {transition[i] / runs,6:F1} us ")
                .Append($"chunks {(double)chunks[i] / runs,5:F1}");
        }

        Console.Error.WriteLine(line.ToString());
    }
    private static long SendWindowFrom, SendFramesFrom, SendBytesFrom, SendAllocFrom, SendItemsFrom;
    private static double SendCpuFrom;
    private static int SendGen0From;

    public static void PlacePlayerSessions(TickContext tick)
    {
        var subs = tick.Subscriptions;
        var tx = tick.Transaction;
        if (subs == null || tx == null)
        {
            return;
        }

        // The periodic report. Error, not Out: a redirected stdout is block-buffered and this process is stopped rather than asked to exit, so the buffer is
        // never flushed and the diagnostic is lost exactly when it is being collected.
        if (++_placeTicks % 300 == 0)
        {
            var (projected, dormant) = subs.ProjectionBlocks;
            var ef = subs.EnterFlow;
            Console.Error.WriteLine(
                $"  replication (cumulative): blocks {projected} projected, {dormant} dormant, sleeping {subs.SleepingClusters}; "
                + $"records {ef.Entered} entered, {ef.Left} left");
            var idf = subs.IdentityFlow;
            Console.Error.WriteLine(
                $"  identities: {idf.Minted} minted, {idf.Released} released, {idf.Reused} reused; "
                + $"{subs.EntriesMigrated} entries relocated between clusters");
            var sendPath = subs.SendPath;
            var st = subs.SendTotals;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            var alloc = GC.GetTotalAllocatedBytes();
            var items = System.Threading.ThreadPool.CompletedWorkItemCount;
            var gen0 = GC.CollectionCount(0);
            if (SendWindowFrom != 0)
            {
                var wallMs = (now - SendWindowFrom) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                Console.Error.WriteLine(
                    $"  send path: wake {sendPath.WakeMsPerPublish:F3} ms/publish on the driver ({sendPath.WokenPerPublish:F0} woken), pool delay {sendPath.QueueDelayUs:F0} us, "
                    + $"send {sendPath.SendUs:F1} us ({sendPath.SendsSync} sync, {sendPath.SendsAsync} async); window: {(st.Frames - SendFramesFrom) * 1000d / wallMs:F0} frames/s, "
                    + $"{(st.Bytes - SendBytesFrom) / wallMs / 1000d:F1} MB/s, process CPU {(cpu - SendCpuFrom) / wallMs:F2} cores, "
                    + $"alloc {(alloc - SendAllocFrom) / wallMs / 1000d:F2} MB/s, pool items {(items - SendItemsFrom) * 1000d / wallMs:F0}/s, "
                    + $"pool threads {System.Threading.ThreadPool.ThreadCount}, gen0 {gen0 - SendGen0From}");
            }

            if (Scheduler != null && SendWindowFrom != 0)
            {
                var wi = Scheduler.WorkerIdle;
                var ticks = Math.Max(1, Scheduler.CurrentTickNumber - IdleTickFrom);
                var inDispatch = wi.InDispatchMs - IdleFrom.InDispatchMs;
                var idle = wi.IdleMs - IdleFrom.IdleMs;
                var parked = wi.ParkedMs - IdleFrom.ParkedMs;
                Console.Error.WriteLine(
                    $"  scheduler: {inDispatch / ticks:F2} ms worker time in dispatches per tick, idle {(inDispatch <= 0 ? 0 : idle * 100d / inDispatch):F1} % "
                    + $"(parked {(inDispatch <= 0 ? 0 : parked * 100d / inDispatch):F1} %, spinning {(inDispatch <= 0 ? 0 : (idle - parked) * 100d / inDispatch):F1} %); "
                    + $"per tick: {(double)(wi.Parks - IdleFrom.Parks) / ticks:F1} parks, {(double)(wi.Wakes - IdleFrom.Wakes) / ticks:F1} wakes, "
                    + $"{(double)(wi.Backstops - IdleFrom.Backstops) / ticks:F2} backstops, {(double)(wi.Spells - IdleFrom.Spells) / ticks:F1} idle spells");
            }

            if (Scheduler != null && SendWindowFrom != 0)
            {
                var wu = Scheduler.WorkerUtilization;
                var uTicks = wu.Ticks - _utilFrom.Ticks;
                if (uTicks > 0)
                {
                    var active = wu.ActiveMs - _utilFrom.ActiveMs;
                    var wall = wu.TickWallMs - _utilFrom.TickWallMs;
                    Console.Error.WriteLine(
                        $"  worker utilization: {active / uTicks:F2} ms active per tick over {wall / uTicks:F2} ms tick wall, "
                        + $"{(wall <= 0 ? 0 : active * 100d / (wall * wu.Workers)):F1} % of {wu.Workers} workers");
                }
            }

            if (Scheduler != null && SendWindowFrom != 0)
            {
                ReportSystemEfficiency();
            }

            if (Scheduler != null)
            {
                _utilFrom = Scheduler.WorkerUtilization;
                IdleFrom = Scheduler.WorkerIdle;
                IdleTickFrom = Scheduler.CurrentTickNumber;
            }

            SendWindowFrom = now;
            SendFramesFrom = st.Frames;
            SendBytesFrom = st.Bytes;
            SendCpuFrom = cpu;
            SendAllocFrom = alloc;
            SendItemsFrom = items;
            SendGen0From = gen0;
            var ee = subs.EpochEnter;
            Console.Error.WriteLine($"  epoch enter: {ee.MeanUs:F1} us mean over {ee.Chunks} chunks");
            var fs = subs.FrameSpan;
            Console.Error.WriteLine($"  frame span: {fs.SpanMs:F2} ms wall, {fs.BusyMs:F2} ms busy, {fs.Concurrency:F1} concurrent, start spread {fs.StartSpreadMs:F2} ms");
            var pm = subs.FramePrologueMs;
            Console.Error.WriteLine($"  frame prologue: {pm.Prologue:F2} ms/tick serial (sweep {pm.Sweep:F2}, prepare {pm.Prepare:F2})");
            var pp = subs.ProjectPrologueMs;
            Console.Error.WriteLine(
                $"  project: serial {pp.Prepare + pp.Drain + pp.Mark:F2} ms/tick (prepare {pp.Prepare:F2}, drain {pp.Drain:F2}, mark {pp.Mark:F2}), "
                + $"parallel busy {pp.Busy:F2} ms/tick");
            var fb = subs.FrameBalance;
            Console.Error.WriteLine($"  frame balance: {fb.Effective:F1} effective workers, {fb.Efficiency * 100d:F0} % efficiency over {fb.Ticks} ticks");
            var ph = subs.FramePhases;
            if (ph.Gather + ph.Encode > 0d)
            {
                Console.Error.WriteLine(
                    $"  frame phases (ms CPU, cumulative): gather {ph.Gather:F0}, sort {ph.Sort:F0}, encode {ph.Encode:F0}, publish {ph.Publish:F0}");
            }
        }

        // NOT disposed: the accessor comes from the TICK's transaction, which owns it and releases it. Disposing one taken from a transaction this
        // method did not create tears down the cached EntityMap and chunk accessors mid-tick, which stops later systems reading.
        var accessor = tx.For<Player>();

        // Which sessions still need a player. Collected first so the walk below can hand one out the moment it meets a player nobody holds.
        Unbound.Clear();
        Seen.Clear();
        foreach (var session in subs.OpenSessions)
        {
            if (!string.Equals(subs.SessionKindOf(session), PlayerKind, StringComparison.Ordinal))
            {
                continue;
            }

            Seen.Add(session.Value);
            if (!BoundPlayer.ContainsKey(session.Value))
            {
                Unbound.Add(session);
            }
        }

        // ONE walk: this tick's position for every player a session holds, and a player for every session that does not hold one yet.
        BoundPositions.Clear();
        var cursor = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
            var placements = cluster.GetReadOnlySpan(Player.Bounds);
            var ids = cluster.EntityIds;
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                var id = ids[slot];
                var held = BoundIds.Contains(id);
                if (!held && cursor >= Unbound.Count)
                {
                    continue;
                }

                var b = placements[slot].Bounds;
                var at = new Vector3D((b.MinX + b.MaxX) * 0.5, (b.MinY + b.MaxY) * 0.5, 0d);
                if (!held)
                {
                    var session = Unbound[cursor++];
                    BoundPlayer[session.Value] = id;
                    BoundIds.Add(id);
                }

                BoundPositions[id] = at;
            }
        }

        foreach (var session in subs.OpenSessions)
        {
            if (string.Equals(subs.SessionKindOf(session), PlayerKind, StringComparison.Ordinal)
                && BoundPlayer.TryGetValue(session.Value, out var id)
                && BoundPositions.TryGetValue(id, out var at))
            {
                subs.Place(session, at);
            }
        }


        // A closed session gives its player back, or the maps grow for the life of the process and every player eventually reads as held — at which
        // point a new session is bound to nothing and sees nothing.
        Retired.Clear();
        foreach (var (sessionValue, id) in BoundPlayer)
        {
            if (!Seen.Contains(sessionValue))
            {
                Retired.Add(sessionValue);
                BoundIds.Remove(id);
            }
        }

        for (var i = 0; i < Retired.Count; i++)
        {
            BoundPlayer.Remove(Retired[i]);
        }
    }

    /// <summary>The player each session watches, for the life of the session.</summary>
    /// <remarks>
    /// <para>
    /// <b>A session is an observer, and an observer has to be somewhere in particular.</b> This used to re-pick the player per session per tick by walking
    /// the Player clusters and taking the n-th one — so a session was bound to an ORDINAL IN AN ITERATION ORDER rather than to a character. That order is
    /// not stable: players migrate between clusters, repair redistributes them, and clusters are created and released, so session n watched a different
    /// character on almost every tick and its viewpoint jumped to wherever that character happened to stand.
    /// </para>
    /// <para>
    /// <b>Measured, it moved the viewpoints 118.8 m per tick</b> against the 0.1 m a player running at <c>PlayerRunSpeedMps</c> covers at 50 Hz — about
    /// twelve hundred times too fast, and the distance between two arbitrary characters rather than a distance anybody travelled. Every session therefore
    /// entered and left most of a disc every tick: 322 enters and 321 leaves per frame with 6 600 enters permanently owed, a backlog that could never
    /// drain because the next tick moved the disc again. That is where "the enter backlog" and the 1.05 enter-to-leave ratio came from, and both are
    /// artefacts of this method rather than anything the subscriptions track did.
    /// </para>
    /// <para>
    /// <b>Spreading the sessions across DIFFERENT players is still the point</b> and is why the binding walks for a player nobody holds: a measurement taken
    /// with every session on one spot reports a per-session cost no real population has. Doing it ONCE is what makes each session an observer instead of a
    /// teleport.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<uint, long> BoundPlayer = [];

    /// <summary>The players held by some session, so the walk can tell a free one from a taken one without searching.</summary>
    private static readonly HashSet<long> BoundIds = [];

    /// <summary>Scratch, reused every tick: this tick's position for each held player.</summary>
    private static readonly Dictionary<long, Vector3D> BoundPositions = [];

    /// <summary>Scratch: the player sessions open this tick that hold no player yet.</summary>
    private static readonly List<SessionId> Unbound = [];

    /// <summary>Scratch: the player sessions seen open this tick, so the closed ones can give their players back.</summary>
    private static readonly HashSet<uint> Seen = [];

    /// <summary>Scratch: the bindings to drop, collected before the dictionary is written.</summary>
    private static readonly List<uint> Retired = [];
}
