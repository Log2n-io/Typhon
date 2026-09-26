using System;
using System.Diagnostics;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>The frame stage's session half: every session bound to a profile, served from <see cref="PushReplication"/>'s cell index.</summary>
/// <remarks>
/// <para>
/// <b>No known-set.</b> A session's records are gathered by <see cref="PushReplication.Gather"/> into per-archetype sub-lists, encoded by
/// <see cref="EntitiesEncoder"/>, and published through the send state and frame pool. What a session knows is geometry, not a per-entity set.
/// </para>
/// <para>
/// <b>A frame that is not published is caught up from the push log</b> on the session's next frame, while every missed tick is still in it; after that, the
/// next frame is a RESET that re-delivers the view cell by cell. Measured by <see cref="PushReplication.LogCatchUps"/> and
/// <see cref="PushReplication.Resets"/>.
/// </para>
/// </remarks>
internal sealed unsafe partial class FrameAssembler
{
    /// <summary>The push path, when some profile observes an archetype.</summary>
    internal PushReplication Push;

    /// <summary>The served realm's frame, which every <c>RESET</c> frame carries as its first block (<c>typhon.3</c>); set by the runtime.</summary>
    internal RealmFrame Realm;

    // A REALM block at its largest: type, the length prefix BeginLengthPrefixed reserves (5), u16 id, u16 generation, flags, varu kind, u32 tag,
    // u8 bits, f64 cell, six f64 bounds.
    private const int RealmBlockBound = 1 + 5 + 2 + 2 + 1 + 5 + 4 + 1 + 8 + (6 * 8);

    /// <summary>The declared profiles, which name each session's observer.</summary>
    internal SubscriptionProfiles Profiles;

    /// <summary>The engine, whose EntityMap and clusters a followed entity's position is read from (09 § 6).</summary>
    internal DatabaseEngine Engine;

    /// <summary>The ingress, whose session rows hold the region each ClientRegion session last sent (09 § 7).</summary>
    internal SubscriptionsIngress Ingress;

    private static readonly ClientRegionCommand NoRegion;

    /// <summary>Frames whose followed entity was gone, served at its last position instead (09 § 6, Q6). The application rebinds or unplaces.</summary>
    public long BoundLost;

    // The entity each push session follows this tick (Bind or AroundControlled), or Null; resolved in the parallel chunk, not the serial prologue.
    private EntityId[] _pushFollow = [];

    // Per slot: the last position a followed entity was read at, and for which session generation — what a session keeps when its entity is gone.
    private readonly Vector3D[] _followed = [];
    private readonly uint[] _followedGeneration = [];
    private readonly EntityId[] _followedEntity = [];
    private readonly bool[] _followedValid = [];

    private SessionId[] _pushSessions = [];
    private Vector3D[] _pushViewpoints = [];
    private bool[] _pushPlaced = [];
    private int[] _pushProfiles = [];
    private double[] _pushRadius = [];
    private bool[] _pushWorld = [];
    private bool[] _pushRegion = [];
    private int[] _pushDivisor = [];

    // Per push session this tick: the replication of its realm (R4.3).
    private PushReplication[] _pushReplication = [];
    private int _pushSessionCount;
    private int _pushCursor;

    // Sessions bound to no profile, served events alone (09 § 11): Broadcast and EmitTo reach them. Their last committed events tick, by slot.
    private SessionId[] _eventSessions = [];
    private int _eventSessionCount;
    private int _eventCursor;

    // Aggregates (09 § 8), by slot, committed with the frame: the tick of the session's last AGG, the anchor it was centred on, and the generation they
    // belong to (0: none sent yet, so the next is a RESET).
    private readonly uint[] _aggLastTick = [];
    private readonly ushort[] _aggGeneration = [];
    private readonly Vector3D[] _aggAnchor = [];

    // DEBUG (09 § 15), by slot, committed with the frame: the generation the grid was sent to (0: none yet) and the hash of the last PUSH_GEOMETRY sent.
    private readonly ushort[] _debugGeneration = [];
    private readonly ulong[] _debugHash = [];

    // A ClientRegion session's aggregate region at its last AGG, sorted (09 § 8): its hull less what its near tier held.
    private readonly uint[][] _aggRegion = [];
    private readonly int[] _aggRegionCount = [];

    /// <summary>Serial: collects this tick's push sessions and their viewpoints, prepares their rows, and indexes the push events.</summary>
    private void BeginPushTick()
    {
        _pushSessionCount = 0;
        _pushCursor = 0;
        _eventSessionCount = 0;
        _eventCursor = 0;
        if (Push == null || Profiles == null)
        {
            return;
        }

        // This tick's rejections, sorted by session into the acknowledgement history (11 § 2.3).
        RecordAcks();

        // Overload (09 § 10): while the tick is stretched, every profile is served half as often (up to every fourth tick) and every Sphere session's LOD
        // level rises a step (the budget loop adds it); the enter budget shrinks by the multiplier. Stateless: it follows the multiplier the tick started
        // with, and the detector's hold is what brings it back.
        var multiplier = Math.Max(1, Volatile.Read(ref _tickMultiplier));
        var overload = multiplier > 1 ? 1 : 0;
        var n = 0;
        var tick = (uint)_tick;
        var hub = Push.Hub;
        NoteRealmMoves();
        foreach (var session in _sessions)
        {
            var hasProfile = Profiles.TryGetProfile(session, out var profile, out var world, out var divisor);

            // The session's realm this tick (R4.3, 12-realms § 1.3): its followed entity's for Bind and AroundControlled — after this tick's fence, so a
            // teleport moves its sessions in the same tick — realm 0 for At, and otherwise where the application placed it.
            var source = hasProfile ? Profiles.SourceOf(profile) : ViewpointSource.Placed;
            var follow = EntityId.Null;
            int realm;
            switch (source)
            {
                case ViewpointSource.Fixed:
                    realm = RealmId.Default.Value;
                    break;
                case ViewpointSource.Bound:
                    follow = Profiles.BoundEntityOf(profile);
                    realm = AnchorRealm(session, follow, _anyRealmMoves);
                    break;
                case ViewpointSource.Controlled:
                    follow = _sessions.ControlledOf(session);
                    realm = AnchorRealm(session, follow, _anyRealmMoves && Self == null);
                    break;
                default:
                    realm = PlacedRealm(session);
                    break;
            }

            // The realm's kind picks the profile's variant (12-realms § 1.4) — in the switch itself, so a realm and variant change is one reset. A realm no
            // session may be in (no replication declared) puts the session in none; a kind the profile excludes serves it nothing there.
            var kind = RealmKindOf(realm);
            if (kind == UnreplicatedRealm)
            {
                realm = RealmId.NoneValue;
                RealmsUnreplicated++;
            }
            else if (hasProfile)
            {
                var variant = Profiles.VariantOf(profile, kind);
                if (variant < 0)
                {
                    RealmsUnserved++;
                }

                hasProfile = variant >= 0 && Profiles.IsServed(variant);
                if (hasProfile)
                {
                    profile = variant;
                    world = Profiles.IsWorld(variant);
                    divisor = Profiles.DivisorOf(variant);
                }
            }

            PrepareSession(session);
            var state = StateOf(session);
            PushReplication replication = null;
            if (hasProfile && realm != RealmId.NoneValue)
            {
                // Its realm-local slot and link state. A new slot while the client still holds frames: what it holds is another realm's, or this realm's
                // from before the slot went back — only a RESET says what it holds now (SUB-29).
                replication = hub.Place(session, (ushort)realm, tick, out var joined);
                if (joined && state != null && state.FramesProduced > 0)
                {
                    state.PendingReset = true;
                }
            }
            else
            {
                hub.Leave(session.Slot);
            }

            NoteRealm(session, state, realm);
            if (replication == null)
            {
                // No profile, no realm or one replication does not serve: no view. A broadcast or an EmitTo still reaches it (09 § 11), and so do its SELF
                // and its acknowledgements (11 § 2), in a frame of those alone — which carries its REALM on a switch.
                if (_eventSessionCount == _eventSessions.Length)
                {
                    Array.Resize(ref _eventSessions, Math.Max(16, _eventSessionCount * 2));
                }

                _eventSessions[_eventSessionCount++] = session;
                continue;
            }

            divisor = Math.Min(divisor << overload, 4);

            if (n == _pushSessions.Length)
            {
                var grown = Math.Max(16, n * 2);
                Array.Resize(ref _pushSessions, grown);
                Array.Resize(ref _pushViewpoints, grown);
                Array.Resize(ref _pushPlaced, grown);
                Array.Resize(ref _pushProfiles, grown);
                Array.Resize(ref _pushRadius, grown);
                Array.Resize(ref _pushFollow, grown);
                Array.Resize(ref _pushWorld, grown);
                Array.Resize(ref _pushRegion, grown);
                Array.Resize(ref _pushDivisor, grown);
                Array.Resize(ref _pushReplication, grown);
            }

            _pushWorld[n] = world;
            _pushRegion[n] = Profiles.RegionOf(profile);
            _pushDivisor[n] = divisor;
            _pushFollow[n] = follow;
            _pushReplication[n] = replication;
            switch (source)
            {
                case ViewpointSource.Fixed:
                    _pushPlaced[n] = true;
                    _pushViewpoints[n] = Profiles.PlacementOf(profile);
                    break;
                case ViewpointSource.Bound:
                case ViewpointSource.Controlled:
                    // Placed once its entity is read, in the session's chunk; a session that follows nothing yet is nowhere, as an unplaced one is.
                    _pushPlaced[n] = false;
                    break;
                default:
                    _pushPlaced[n] = _sessions.TryGetViewpoint(session, out var viewpoint);
                    _pushViewpoints[n] = viewpoint;
                    break;
            }

            _pushProfiles[n] = profile;

            // The radius this frame is gathered at: the session's run-time one when SetRadius gave it one, its profile's R′ otherwise (09 § 3–4).
            var radius = _sessions.Radius(session);
            _pushRadius[n] = radius > 0 ? radius : Profiles.RadiusOf(profile);
            _pushSessions[n++] = session;
            // Only a World session served this tick can fill: a rate class skips the others (the check the frame stage repeats below).
            if (world && (divisor <= 1 || ((_tick + session.Slot) % (uint)divisor) == 0))
            {
                replication.NoteWorldSession(session, state != null && state.PendingReset);
            }
        }

        _pushSessionCount = n;

        // Slots of sessions that closed or lost their profile since the last sweep go back, and so do their realm observations.
        hub.SweepUnplaced(tick);
        SweepObservers(tick);
        hub.SetOverloadStep(overload);

        // Every tick the track runs is indexed in every served realm, sessions bound or not: the index is the tick's log slot, and its cell changes are the
        // occupancy's only input (SUB-24). A tick left unindexed would leave the occupancy short of its spawns and crossings. After the index, whose finish
        // brought the occupancy to this tick: the occupied cells in order, for the World fills still under way.
        hub.BuildIndexes();

        // Events (09 § 11): this tick's emissions, encoded once — after projection, so their entities' netIds exist — into the event log.
        if (Events != null)
        {
            // The prologue holds no epoch of its own, and resolving an entity reads the EntityMap and the cluster layout through chunk accessors.
            using var epoch = EpochGuard.Enter(Engine.EpochManager);
            var netIds = new BoundViewpoint(Engine) { Push = Push };
            try
            {
                Events.EncodeTick((uint)_tick, ref netIds, Push.Hub);
            }
            finally
            {
                netIds.Dispose();
            }
        }

        // Distance LOD: the far flushes into the tick's log slot — folded by their stage, or here when the index was built here.
        if (n > 0)
        {
            hub.EndFarFolds();
        }

        // The LOD census, recounted before this tick's commits move it — only while a level is in use (09 § 10).
        hub.RecountLevels(_pushSessions, n);

        // Shadow oracle: every 50 ticks, up to eight sessions compared with the geometry. Serial, and before the frames: the anchors it reads are the
        // committed ones, which is what the shadows describe.
        if (Push.Shadow && _tick % 50 == 49)
        {
            // Sampled now, CHECKED at the start of the next tick's blocks step — after this tick's frames are published and before the next projection
            // moves any block. Checking here would compare this tick's projected positions against last tick's frames.
            var sample = Math.Min(n, 8);
            for (var i = 0; i < sample; i++)
            {
                var k = (int)((i * (long)n) / sample);
                _pushReplication[k].QueueShadowCheck(_pushSessions[k], in Profiles.SetOf(_pushProfiles[k]));
            }
        }

        // A diagnostic, and it allocates: only where phase timing was asked for.
        if (PhaseTimingEnabled && _tick % 500 == 0)
        {
            ReportPush();
        }
    }

    private void ReportPush()
    {
        var p = Push;
        var f = 1000d / Stopwatch.Frequency;
        Console.Error.WriteLine(
            $"  PUSH: {_pushSessionCount} sessions; slots pushed {p.Hub.SlotsPushed}, events {p.Events}; enters {p.Enters}, leaves {p.Leaves}, updates {p.Updates}; "
            + $"cells delivered {p.CellsDelivered}, sweeps {p.Sweeps} ({p.SweepSlots} slots); resets {p.Resets}; "
            + $"serial prepare {p.Hub.PrepareTicks * f:F0} ms, index {p.IndexTicks * f:F0} ms "
            + $"(sort {p.SortTicks * f:F0}, merge {p.MergeTicks * f:F0}, finish {p.FinishTicks * f:F0}), gather busy {p.GatherTicks * f:F0} ms (cumulative)");
        Console.Error.WriteLine(
            $"  PUSH CELLS: delivery {p.DeliverTicks * f:F0} ms, {p.DeliverDecoded} decoded for {p.DeliverEntered} entered; "
            + $"sweep {p.SweepTicks * f:F0} ms, {p.SweepDecoded} decoded for {p.SweepSlots} in the cell; empty skipped {p.EmptyCellsSkipped}");
        Console.Error.WriteLine($"  PUSH LOD: far fold phase {p.FarPhase} window {p.FarWindow}; updates withheld {p.UpdatesDeferred}, far flushes {p.FarFlushes}, crescent states {p.FarCrescentStates}, fold tail {p.FarEndTicks * f:F0} ms; levels raised {p.LevelRaises}, lowered {p.LevelLowers}; radius cells off {p.RadiusShrinks}, back {p.RadiusGrows}; overload step {p.OverloadStep}");
        Console.Error.WriteLine(
            $"  PUSH LOG: catch-ups {p.LogCatchUps} over {p.LogCatchUpTicks} missed ticks; resets: too old {p.LogTooOld}, ambiguous {p.LogAmbiguous}; "
            + $"gap re-pushes {p.GapRepushes}, "
            + $"occupancy recounts {p.OccupancyRecounts} ({p.RecountTicks * f:F0} ms)");
        if (p.Hub.ValidateClustersPerTick > 0)
        {
            var groups = new System.Text.StringBuilder();
            for (var a = 0; a < _plans.Length; a++)
            {
                var planGroups = _plans[a].Groups;
                for (var g = 0; planGroups != null && g < planGroups.Length; g++)
                {
                    var n = p.Hub.ForgottenGroups(a, g);
                    if (n > 0)
                    {
                        groups.Append($" {_plans[a].Name}.{planGroups[g].Name}={n}");
                    }
                }
            }

            Console.Error.WriteLine($"  PUSH VALIDATOR: {p.Hub.ValidatedSlots} slots checked, forgotten pushes {p.Hub.ForgottenPushes} (motion {p.Hub.ForgottenMotion});{groups}");
        }

        Console.Error.WriteLine("  PUSH MIGRATION: " + p.MigrationSummary() + $"orphans: release {p.OrphanRelease}, migrate {p.OrphanMigrate}, drain {p.OrphanDrain}");
        if (p.Shadow)
        {
            Console.Error.WriteLine(
                $"  PUSH SHADOW: {p.ShadowChecks} checks over {p.ShadowChecked} expected entities: illegal records {p.ShadowIllegal}, "
                + $"missing {p.ShadowMissing}, extra {p.ShadowExtra} (gone {p.ExtraGone}, outside {p.ExtraOutside} by "
                + $"{(p.ExtraOutside == 0 ? 0d : p.ExtraOutsideDistSum / p.ExtraOutside):F2} m, undelivered {p.ExtraUndelivered}; gone: in unoccupied slot {p.GoneInUnoccupiedSlot}, other {p.GoneOccupiedButNotProjected}, nowhere {p.GoneNowhere})");
        }
    }

    /// <summary>Parallel: this chunk's share of the push sessions, taken from a shared cursor.</summary>
    private void ExecutePushSessions(FrameWorkerScratch scratch, ref FrameCounters counters)
    {
        if (_pushSessionCount + _eventSessionCount == 0)
        {
            return;
        }

        long enters = 0, leaves = 0, updates = 0, lost = 0;

        // One reader for this worker's whole share: its accessors stay cached across the followed sessions it serves.
        var follow = new BoundViewpoint(Engine);
        try
        {
            while (true)
            {
                var i = Interlocked.Increment(ref _pushCursor) - 1;
                if (i >= _pushSessionCount)
                {
                    break;
                }

                var published = -1;
                AssemblePush(i, scratch, ref follow, ref lost, ref counters, ref enters, ref leaves, ref updates, ref published);

                // The budget loop, fed with every frame the session was given or had nothing for (09 § 10). World and ClientRegion sessions have no
                // LOD. The budget is read here, in the session's chunk, not in the serial prologue: a row per session there was a cache miss per session
                // in series.
                if (published >= 0 && !_pushWorld[i] && !_pushRegion[i])
                {
                    var session = _pushSessions[i];
                    _pushReplication[i].Pace(session, published, _sessions.BudgetOf(session), Volatile.Read(ref _tickSeconds));
                }
            }
        }
        finally
        {
            follow.Dispose();
        }

        if (lost > 0)
        {
            Interlocked.Add(ref BoundLost, lost);
        }

        if (_eventSessionCount > 0)
        {
            var locator = new BoundViewpoint(Engine);
            try
            {
                while (true)
                {
                    var i = Interlocked.Increment(ref _eventCursor) - 1;
                    if (i >= _eventSessionCount)
                    {
                        break;
                    }

                    AssembleEventsOnly(_eventSessions[i], scratch, ref locator, ref counters);
                }
            }
            finally
            {
                locator.Dispose();
            }
        }

        Interlocked.Add(ref Push.Enters, enters);
        Interlocked.Add(ref Push.Leaves, leaves);
        Interlocked.Add(ref Push.Updates, updates);
    }

    // ══ Sessions in realms (R4.3, 12-realms § 1) ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The engine holds more than one realm: a session is then in none until placed, entered or anchored (12-realms § 1.2).</summary>
    internal bool MultiRealm;

    // Per session slot: the entity a Bind or AroundControlled session followed at its last placement, the realm that entity was in, and for which session
    // generation — the anchor's realm, kept in step by the fence's realm moves rather than read per session per tick.
    private EntityId[] _anchorEntity = [];
    private int[] _anchorRealm = [];
    private ushort[] _anchorGeneration = [];

    // This tick: the fence moved some entity across realms; and every anchor must be read again (the first tick, or one after a tick the track missed).
    private bool _anyRealmMoves;
    private bool _anchorsStale;
    private long _realmTick;

    // Per session slot: the realm the session observes for the realm policy (RLM-03), for which generation, and the last tick it was seen doing so.
    private int[] _observedRealm = [];
    private ushort[] _observedGeneration = [];
    private uint[] _observedSeen = [];

    // Realm frames and kinds by realm id, built once per registered realm (the Realm object is the identity: an id reused after an Unregister is a new
    // frame).
    private RealmFrame[] _realmFrames = [];
    private int[] _realmKinds = [];
    private object[] _realmFrameOwners = [];

    /// <summary>What <see cref="RealmKindOf"/> answers for a registered realm no session may be in: no replication declared (12-realms § 2.1).</summary>
    private const int UnreplicatedRealm = -2;

    /// <summary>Sessions whose realm has no replication declared — a followed entity that entered one — and so are in none; per session per tick.</summary>
    public long RealmsUnreplicated;

    /// <summary>Sessions in a realm whose kind their profile excludes (<c>NotIn</c>), served nothing there; per session per tick.</summary>
    public long RealmsUnserved;

    private void EnsureRealmSlots()
    {
        if (_anchorEntity.Length == _states.Length)
        {
            return;
        }

        var n = _states.Length;
        _anchorEntity = new EntityId[n];
        _anchorRealm = new int[n];
        _anchorGeneration = new ushort[n];
        _observedRealm = new int[n];
        _observedGeneration = new ushort[n];
        _observedSeen = new uint[n];
        Array.Fill(_observedRealm, -1);
    }

    /// <summary>The realm the application placed a session in; unplaced, realm 0 on an engine with one realm and none on one with several.</summary>
    private int PlacedRealm(SessionId session)
    {
        var realm = _sessions.RealmOf(session);
        return realm >= 0 ? realm : MultiRealm ? RealmId.NoneValue : RealmId.Default.Value;
    }

    /// <summary>
    /// The fence's realm moves this tick (C4): a controlled entity that crossed takes its sessions along in this tick — one pass over the moves, through the
    /// Control map, never over the sessions. A tick the track did not run for lost its moves, so every anchor is read again.
    /// </summary>
    private void NoteRealmMoves()
    {
        EnsureRealmSlots();
        _anchorsStale = _realmTick == 0 || _tick != _realmTick + 1;
        _realmTick = _tick;
        _anyRealmMoves = false;
        var states = MultiRealm ? Engine?._stateByRouting : null;
        if (states == null)
        {
            return;
        }

        foreach (var engineState in states)
        {
            var clusters = engineState?.ClusterState;
            if (clusters == null || clusters.LastTickRealmChanges == 0)
            {
                continue;
            }

            _anyRealmMoves = true;
            foreach (var move in clusters.LastFenceRealmChanges)
            {
                var raw = (ulong)move.EntityId;
                for (var slot = Self?.FirstControlling(raw) ?? -1; slot >= 0; slot = Self.NextControlling(slot))
                {
                    if (_anchorEntity[slot].RawValue == raw)
                    {
                        _anchorRealm[slot] = move.ToRealm;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The realm of the entity a session follows: read when the session, its entity or <paramref name="stale"/> says so, kept in step by
    /// <see cref="NoteRealmMoves"/> otherwise. An entity never read is nowhere; one that is gone leaves the session in its last realm (09 § 6).
    /// </summary>
    private int AnchorRealm(SessionId session, EntityId entity, bool stale)
    {
        if (!MultiRealm)
        {
            return RealmId.Default.Value;
        }

        var slot = session.Slot;
        var fresh = _anchorGeneration[slot] != session.Generation || _anchorEntity[slot] != entity;
        if (fresh || stale || _anchorsStale)
        {
            var found = entity.IsNull ? -1 : ProbeRealm(entity);
            if (found >= 0)
            {
                _anchorRealm[slot] = found;
            }
            else if (fresh)
            {
                _anchorRealm[slot] = RealmId.NoneValue;
            }

            _anchorGeneration[slot] = session.Generation;
            _anchorEntity[slot] = entity;
        }

        return _anchorRealm[slot];
    }

    // One EntityMap probe: a session's entity changed, or the anchors are stale. Rare by construction, so the reader is made per probe.
    private int ProbeRealm(EntityId entity)
    {
        using var epoch = EpochGuard.Enter(Engine.EpochManager);
        var probe = new BoundViewpoint(Engine);
        try
        {
            return probe.TryRealm(entity, out var realm) ? realm : -1;
        }
        finally
        {
            probe.Dispose();
        }
    }

    /// <summary>
    /// The session's realm this tick, and what its next frame says about it: a switch away from a realm its client holds entities of forces a RESET now
    /// (SUB-29); one from nothing rides the first frame that has something to say. The session observes its realm for the realm policy.
    /// </summary>
    private void NoteRealm(SessionId session, SessionFrameState state, int realm)
    {
        if (state == null || Realm == null)
        {
            return;
        }

        state.PendingRealm = realm;
        state.PendingFrame = FrameOf(realm);
        var held = state.CommittedRealm;
        if (realm != held && held >= 0 && held != RealmId.NoneValue)
        {
            state.PendingReset = true;
        }

        Observe(session, realm);
    }

    /// <summary>
    /// Before a <c>RESET</c> that switches the session's realm is published: the transport's view of it (<see cref="SessionRealmView"/>), so a command the
    /// client builds in the new realm can never be decoded over the old one. A frame refused after this point leaves a view one switch early, which only
    /// refuses the rare command built in the old realm meanwhile.
    /// </summary>
    private void PublishRealmView(SessionId session, SessionFrameState state, bool reset)
    {
        if (!reset || Realm == null || state.PendingRealm == state.CommittedRealm)
        {
            return;
        }

        var ingress = Ingress;
        if (ingress != null)
        {
            var previous = ingress.RealmViewOf(session);
            ingress.PublishRealmView(new SessionRealmView(session, state.PendingRealm, state.PendingFrame, state.CommittedRealm, previous?.Frame, (uint)_tick));
        }
    }

    /// <summary>A published <c>RESET</c> carried the session's pending realm: it is what its client holds now.</summary>
    private void CommitRealm(SessionFrameState state)
    {
        if (Realm == null)
        {
            return;
        }

        if (state.CommittedRealm != state.PendingRealm)
        {
            state.PreviousRealm = state.CommittedRealm;
            state.RealmSwitchTick = (uint)_tick;
        }

        state.CommittedRealm = state.PendingRealm;
    }

    /// <summary>The <c>REALM</c> block's frame for <paramref name="realm"/>; <see langword="null"/> for none, which the block writes as <c>REALM(NONE)</c>.</summary>
    private RealmFrame FrameOf(int realm) => ResolveRealm(realm) >= 0 ? (realm == RealmId.Default.Value || !MultiRealm ? Realm : _realmFrames[realm]) : null;

    /// <summary>
    /// A realm's kind (its canonical index, which picks its sessions' profile variants): -1 for none or an unregistered realm, <see cref="UnreplicatedRealm"/>
    /// for one no session may be in.
    /// </summary>
    private int RealmKindOf(int realm) => ResolveRealm(realm);

    private int ResolveRealm(int realm)
    {
        if (realm < 0 || realm == RealmId.NoneValue)
        {
            return -1;
        }

        if (!MultiRealm)
        {
            return 0;
        }

        var owner = Engine?.RealmTable?.TryGet((ushort)realm);
        if (realm >= _realmFrames.Length)
        {
            Array.Resize(ref _realmFrames, Math.Max(realm + 1, Math.Max(8, _realmFrames.Length * 2)));
            Array.Resize(ref _realmKinds, _realmFrames.Length);
            Array.Resize(ref _realmFrameOwners, _realmFrames.Length);
        }

        if (!ReferenceEquals(_realmFrameOwners[realm], owner) || (_realmFrames[realm] == null && _realmKinds[realm] >= 0))
        {
            _realmFrameOwners[realm] = owner;
            var replication = owner?.Config?.Replication;
            if (owner == null)
            {
                _realmFrames[realm] = null;
                _realmKinds[realm] = -1;
            }
            else if (replication == null && realm != RealmId.Default.Value)
            {
                _realmFrames[realm] = null;
                _realmKinds[realm] = UnreplicatedRealm;
            }
            else if (Profiles.KindIndex(replication?.Kind ?? "") < 0)
            {
                // A kind the subscriptions never declared: nothing could say which variant serves it. Refused at Start for the realms registered by
                // then; one registered later is treated as a realm no session may be in, and counted.
                _realmFrames[realm] = null;
                _realmKinds[realm] = UnreplicatedRealm;
            }
            else
            {
                _realmKinds[realm] = Profiles.KindIndex(replication?.Kind ?? "");
                _realmFrames[realm] = realm == RealmId.Default.Value
                    ? Realm
                    : SubscriptionsRuntime.BuildRealmFrame(Engine, _options, (ushort)realm, _realmKinds[realm]);
            }
        }

        return _realmKinds[realm];
    }

    // A session observes the realm it is in (RLM-03): the realm policy keeps an observed realm active. Counted once per session, moved with it.
    private void Observe(SessionId session, int realm)
    {
        var table = MultiRealm ? Engine?.RealmTable : null;
        if (table == null)
        {
            return;
        }

        var slot = session.Slot;
        if (_observedGeneration[slot] != session.Generation)
        {
            Unobserve(table, slot);
            _observedGeneration[slot] = session.Generation;
        }

        _observedSeen[slot] = (uint)_tick;
        var target = realm == RealmId.NoneValue || !table.IsRegistered((ushort)realm) ? -1 : realm;
        if (target == _observedRealm[slot])
        {
            return;
        }

        Unobserve(table, slot);
        if (target >= 0)
        {
            table.AddObserver((ushort)target);
            _observedRealm[slot] = target;
        }
    }

    private void Unobserve(RealmTable table, int slot)
    {
        var observed = _observedRealm[slot];
        _observedRealm[slot] = -1;
        if (observed >= 0 && table.IsRegistered((ushort)observed))
        {
            table.RemoveObserver((ushort)observed);
        }
    }

    // Every 64 ticks: a session no longer seen (closed) stops observing. Serial, after the tick's sessions.
    private void SweepObservers(uint tick)
    {
        var table = MultiRealm ? Engine?.RealmTable : null;
        if (table == null || tick % 64 != 0)
        {
            return;
        }

        for (var slot = 0; slot < _observedRealm.Length; slot++)
        {
            if (_observedRealm[slot] >= 0 && _observedSeen[slot] != tick)
            {
                Unobserve(table, slot);
            }
        }
    }

    /// <summary>
    /// A followed session's viewpoint: its entity's position after this tick's fence (09 § 6), read here, in the session's parallel chunk. An entity that
    /// is gone leaves the session at the last position it was read at, and counts <see cref="BoundLost"/>; with none yet, the session is nowhere.
    /// </summary>
    private void ResolveFollowed(int index, SessionId session, ref BoundViewpoint follow, ref long lost)
    {
        var slot = session.Slot;
        if ((uint)slot >= (uint)_followed.Length)
        {
            return;
        }

        var entity = _pushFollow[index];
        if (follow.TryRead(entity, out var centre))
        {
            _followed[slot] = centre;
            _followedGeneration[slot] = session.Generation;
            _followedEntity[slot] = entity;
            _followedValid[slot] = true;
            _pushViewpoints[index] = centre;
            _pushPlaced[index] = true;
            return;
        }

        // Only the same entity's last position: a session switched to an entity that cannot be read yet is nowhere, not where the previous one was.
        if (_followedValid[slot] && _followedGeneration[slot] == session.Generation && _followedEntity[slot] == entity)
        {
            lost++;
            _pushViewpoints[index] = _followed[slot];
            _pushPlaced[index] = true;
        }
    }

    /// <summary>Tests only: the last position a session's followed entity was read at, and whether there is one.</summary>
    internal bool TryGetFollowed(SessionId session, out Vector3D position)
    {
        position = default;
        var slot = session.Slot;
        if ((uint)slot >= (uint)_followed.Length || !_followedValid[slot] || _followedGeneration[slot] != session.Generation)
        {
            return false;
        }

        position = _followed[slot];
        return true;
    }

    // published: the bytes the session's frame published, or zero when it had nothing to say; left as it was when no frame was made — not its tick, or a
    // frame refused (degraded, lagging, no slot, oversize, no pool): the budget loop reads the link's rate, and a refusal is congestion, not quiet.
    /// <summary>
    /// A session bound to no profile: a frame of the events routed to it — broadcasts and its own <c>EmitTo</c> — since its last one, and nothing else
    /// (09 § 11). No frame when none is owed; a frame not sent leaves the events owed, caught up from the log.
    /// </summary>
    private void AssembleEventsOnly(SessionId session, FrameWorkerScratch scratch, ref BoundViewpoint locator, ref FrameCounters counters)
    {
        var state = StateOf(session);
        var slot = session.Slot;
        if (state == null || state.Generation != session.Generation || (uint)slot >= (uint)_states.Length)
        {
            return;
        }

        var events = Events;
        var count = 0;
        var bytes = 0;
        var lost = 0L;
        if (events != null)
        {
            // No view, so no geometric route; a ToRealm announcement of its realm still reaches it (12-realms § 3).
            var nowhere = new NoEventGeometry(state.PendingRealm is >= 0 and < RealmId.NoneValue ? (ushort)state.PendingRealm : RealmId.NoneValue,
                Engine?.RealmTable);
            events.Collect(scratch.EventPicks, state.EventsCursor, (uint)_tick, EntityId.Null, session, ref nowhere, out count, out bytes, out lost);
            if (bytes > _maxFrameBytes / 2)
            {
                EventHub.Shed(scratch.EventPicks, ref count, ref bytes, ref lost);
            }
        }

        var reset = state.PendingReset;

        // A session's first published frame in a realm — its first, or after a switch — is a RESET carrying its REALM (typhon.3, SUB-29): decided before
        // SELF, which sends every owner group on a RESET.
        var first = Realm != null && state.PendingRealm != state.CommittedRealm;
        PrepareSelf(session, state, reset || first, scratch, ref locator, out var self);
        if (count == 0 && !reset && !self.Write && self.Acks == 0)
        {
            state.EventsCursor = (uint)_tick;
            CommitSelf(session, state, in self, -1, ref counters);
            return;
        }

        // ... and only once it has something to say: a frame for the REALM alone would be one more frame whose presence depends on when the session
        // connected. Nothing was published before it, so the store it clears is empty.
        reset |= first;

        var send = SendStateOf(slot);
        if (!SkipPolicy.ProducesOnTick(state.DegradeLevel, _tick) || SkipPolicy.AcknowledgementLag(send->ProducedTick, send->AckedTick) > _lagBoundTicks
            || !send->TryBeginFrame(out var sequence, out var recycled))
        {
            NoteSkip(state);
            return;
        }

        var buffer = scratch.Bytes(64 + RealmBlockBound + bytes + SelfBound(in self));
        var writer = new WireWriter(buffer);
        EntitiesEncoder.WriteHeader(ref writer, (uint)_tick, reset ? TickFlags.Reset : TickFlags.None, Realm != null, state.PendingFrame);
        WriteSelf(ref writer, in self, scratch);
        if (count > 0)
        {
            events.Write(ref writer, scratch.EventPicks, count, lost);
        }

        var length = writer.Position;
        var block = recycled;
        if (length > _maxFrameBytes || !Pool.TryRentOrKeep(length, ref block, out var previous))
        {
            ReturnIfValid(block);
            send->AbandonFrame(sequence);
            NoteSkip(state);
            return;
        }

        ReturnIfValid(previous);
        buffer[..length].CopyTo(new Span<byte>(block.Bytes, block.Capacity));
        PublishRealmView(session, state, reset);
        send->PublishFrame(sequence, block, length, _tick);
        events?.NoteDelivered(count, lost);
        CommitSelf(session, state, in self, -1, ref counters);
        state.EventsCursor = (uint)_tick;
        state.PendingReset = false;
        if (reset)
        {
            CommitRealm(state);
        }

        state.BytesPublished += length;
        state.FramesProduced++;
        state.FramesSinceDegrade++;
        scratch.AddReady(session);
        counters.FramesProduced++;
        counters.BytesEncoded += length;
    }

    /// <summary>
    /// The rows an <c>AGG</c> block carries, sorted by tile into the worker's scratch: in the region (every tile, or those meeting the radius around the
    /// anchor), and changed since the last AGG, or newly in the region, or — on a reset — not empty.
    /// </summary>
    private static int SelectAggregateRows(AggregateCounts counts, FrameWorkerScratch scratch, bool reset, uint last, double radius, Vector3D anchor,
        Vector3D lastAnchor)
    {
        var n = 0;
        var a = counts.ArchetypeCount;
        for (var r = 0; r < counts.Rows; r++)
        {
            var tile = counts.Tiles[r];
            if (radius > 0 && !counts.Meets(tile, anchor.X, anchor.Y, anchor.Z, radius))
            {
                continue;
            }

            bool send;
            if (reset)
            {
                send = false;
                for (var c = 0; c < a && !send; c++)
                {
                    send = counts.Counts[(r * a) + c] > 0;
                }
            }
            else
            {
                send = counts.Stamps[r] > last || (radius > 0 && !counts.Meets(tile, lastAnchor.X, lastAnchor.Y, lastAnchor.Z, radius));
            }

            if (!send)
            {
                continue;
            }

            if (n == scratch.AggRows.Length)
            {
                Array.Resize(ref scratch.AggRows, n * 2);
                Array.Resize(ref scratch.AggTiles, n * 2);
            }

            scratch.AggRows[n] = r;
            scratch.AggTiles[n++] = tile;
        }

        if (n > 1)
        {
            Array.Sort(scratch.AggTiles, scratch.AggRows, 0, n);
        }

        return n;
    }

    /// <summary>An <c>AGG</c> block (03 § 9): the grid, RESET, and each row's tile as a gap from the previous and its counts, one per grid archetype.</summary>
    private static void WriteAggregate(ref WireWriter w, AggregateCounts counts, FrameWorkerScratch scratch, int rows, bool reset)
    {
        var mark = TickWriter.BeginBlock(ref w, BlockTypes.Agg);
        w.WriteVaru((uint)counts.GridIdx);
        w.WriteU8(reset ? (byte)1 : (byte)0);
        w.WriteVaru((uint)rows);
        var prev = -1L;
        var a = counts.ArchetypeCount;
        for (var i = 0; i < rows; i++)
        {
            var tile = scratch.AggTiles[i];
            w.WriteVaru((uint)(tile - prev - 1));
            prev = tile;
            var r = scratch.AggRows[i];
            for (var c = 0; c < a; c++)
            {
                // Row −1: a tile that left a region's aggregate, sent as zero (SelectRegionAggregateRows).
                w.WriteVaru(r < 0 ? 0u : (uint)Math.Max(0, counts.Counts[(r * a) + c]));
            }
        }

        TickWriter.EndBlock(ref w, mark);
    }

    private void CommitAggregate(SessionId session, Vector3D anchor, FrameWorkerScratch region)
    {
        var slot = session.Slot;
        _aggLastTick[slot] = (uint)_tick;
        _aggAnchor[slot] = anchor;
        _aggGeneration[slot] = session.Generation;
        if (region != null)
        {
            var count = region.AggRegionCount;
            if (_aggRegion[slot] == null || _aggRegion[slot].Length < count)
            {
                _aggRegion[slot] = new uint[Math.Max(64, Math.Max(count, (_aggRegion[slot]?.Length ?? 0) * 2))];
            }

            Array.Copy(region.AggRegion, _aggRegion[slot], count);
            _aggRegionCount[slot] = count;
        }
    }

    /// <summary>
    /// A ClientRegion session's <c>AGG</c> rows (09 § 8): the tiles of its aggregate region — its hull less the cells its near tier delivered — that changed
    /// since its last AGG or that the region did not cover then; every non-empty one on a reset. The region's tiles are kept, sorted, for the next AGG.
    /// </summary>
    private int SelectRegionAggregateRows(PushReplication push, SessionId session, AggregateCounts counts, FrameWorkerScratch scratch, bool reset,
        uint last)
    {
        var slot = session.Slot;
        var previous = reset || _aggRegion[slot] == null ? [] : new ReadOnlySpan<uint>(_aggRegion[slot], 0, _aggRegionCount[slot]);
        var n = 0;
        var inRegion = 0;
        var a = counts.ArchetypeCount;
        for (var r = 0; r < counts.Rows; r++)
        {
            var tile = counts.Tiles[r];
            if (!push.RegionAggregates(session, counts, tile))
            {
                continue;
            }

            if (inRegion == scratch.AggRegion.Length)
            {
                Array.Resize(ref scratch.AggRegion, inRegion * 2);
            }

            scratch.AggRegion[inRegion++] = tile;
            bool send;
            if (reset)
            {
                send = false;
                for (var c = 0; c < a && !send; c++)
                {
                    send = counts.Counts[(r * a) + c] > 0;
                }
            }
            else
            {
                send = counts.Stamps[r] > last || previous.BinarySearch(tile) < 0;
            }

            if (!send)
            {
                continue;
            }

            if (n == scratch.AggRows.Length)
            {
                Array.Resize(ref scratch.AggRows, n * 2);
                Array.Resize(ref scratch.AggTiles, n * 2);
            }

            scratch.AggRows[n] = r;
            scratch.AggTiles[n++] = tile;
        }

        Array.Sort(scratch.AggRegion, 0, inRegion);
        scratch.AggRegionCount = inRegion;

        // A tile the region no longer covers gets a zero row: the client knows its hull but not which cells the near tier delivered, so a tile that left
        // because its last cell was delivered would otherwise keep its count beside the entities now held (09 § 8, a region's aggregate).
        var current = new ReadOnlySpan<uint>(scratch.AggRegion, 0, inRegion);
        foreach (var tile in previous)
        {
            if (current.BinarySearch(tile) >= 0)
            {
                continue;
            }

            if (n == scratch.AggRows.Length)
            {
                Array.Resize(ref scratch.AggRows, n * 2);
                Array.Resize(ref scratch.AggTiles, n * 2);
            }

            scratch.AggRows[n] = -1;
            scratch.AggTiles[n++] = tile;
        }

        if (n > 1)
        {
            Array.Sort(scratch.AggTiles, scratch.AggRows, 0, n);
        }

        return n;
    }

    /// <summary>A frame's enter budget (09 § 10): halved per LOD level, divided by the overload multiplier, never below one.</summary>
    private int EnterBudget(int level) => Math.Max(1, (_options.EnterBudgetPerFrame >> level) / Math.Max(1, Volatile.Read(ref _tickMultiplier)));

    private void AssemblePush(int index, FrameWorkerScratch scratch, ref BoundViewpoint follow, ref long lost, ref FrameCounters counters, ref long enters,
        ref long leaves, ref long updates, ref int published)
    {
        var session = _pushSessions[index];
        var state = StateOf(session);
        if (state == null || state.Generation != session.Generation)
        {
            return;
        }

        // The replication of the session's realm: its geometry, its aggregates and its grid (R4.3).
        var push = _pushReplication[index];

        // A rate class: not this session's tick. Not a skip — nothing was refused — and the log carries the union on its next one. Staggered by slot so a
        // profile's sessions do not all land on the same tick.
        var divisor = _pushDivisor[index];
        if (divisor > 1 && ((_tick + session.Slot) % (uint)divisor) != 0)
        {
            return;
        }

        var send = SendStateOf(session.Slot);
        if (!SkipPolicy.ProducesOnTick(state.DegradeLevel, _tick))
        {
            NoteSkip(state);
            push.NoteNotPublished(session);
            return;
        }

        if (SkipPolicy.AcknowledgementLag(send->ProducedTick, send->AckedTick) > _lagBoundTicks)
        {
            send->NoteSkipped();
            NoteSkip(state);
            push.NoteNotPublished(session);
            return;
        }

        if (!send->TryBeginFrame(out var sequence, out var recycled))
        {
            NoteSkip(state);
            push.NoteNotPublished(session);
            return;
        }

        var timing = PhaseTimingEnabled;
        var mark = timing ? Stopwatch.GetTimestamp() : 0L;

        scratch.BeginSession(_plans.Length);
        if (!_pushFollow[index].IsNull)
        {
            ResolveFollowed(index, session, ref follow, ref lost);
        }

        bool reset, complete;
        if (_pushRegion[index])
        {
            // The region the client last sent, read from its ingress row: the ingress drain wrote it earlier in this tick, and nothing writes it again
            // before the next one (SUB-05).
            var row = Ingress?.RowOf(session);
            var hasRegion = row is { HasRegion: true };
            var profile = _pushProfiles[index];
            var (nearBudget, nearCounts) = Profiles.NearOf(profile);
            reset = push.GatherRegion(session, hasRegion, in hasRegion ? ref row.Region : ref NoRegion, Profiles.MaxEdgeOf(profile), nearBudget, nearCounts,
                Volatile.Read(ref _tickSeconds), state.PendingReset, in Profiles.SetOf(profile), scratch, EnterBudget(0), ref enters, ref leaves,
                ref updates, out complete);
        }
        else
        {
            reset = _pushWorld[index]
                ? push.GatherWorld(session, state.PendingReset, in Profiles.SetOf(_pushProfiles[index]), scratch, EnterBudget(0), ref enters, ref leaves,
                    ref updates, out complete)
                : push.Gather(session, _pushPlaced[index], _pushViewpoints[index], _pushRadius[index], Profiles.BandsOf(_pushProfiles[index]),
                    state.PendingReset, in Profiles.SetOf(_pushProfiles[index]), scratch, _encodePlans, EnterBudget(push.TargetLevelOf(session)), ref enters,
                    ref leaves, ref updates, out complete);
        }

        if (timing)
        {
            var now = Stopwatch.GetTimestamp();
            counters.GatherTicks += now - mark;
            mark = now;
        }

        // A pending reset is owed whatever the gather found: a realm switch lands on a fresh realm-local state, whose gather has nothing of its own to reset,
        // while the client still holds the old realm's view (SUB-29).
        reset |= state.PendingReset;
        var flags = TickFlags.None;
        if (reset)
        {
            flags |= TickFlags.Reset;
            Interlocked.Increment(ref push.Resets);
        }

        if (complete)
        {
            flags |= TickFlags.ViewComplete;
        }

        var records = SortAndCount(scratch);
        if (timing)
        {
            var now = Stopwatch.GetTimestamp();
            counters.SortTicks += now - mark;
            mark = now;
        }

        // Events since the session's last frame (09 § 11): the log's, and a loss count for the ticks it no longer holds.
        var events = Events;
        var eventCount = 0;
        var eventBytes = 0;
        var eventsLost = 0L;
        if (events != null)
        {
            // The session's realm's filings only (SUB-28): a geometric route is filed in its point's realm, by that realm's cells.
            var geometry = new SessionEventGeometry(push, session, _pushWorld[index], Engine?.RealmTable);
            events.Collect(scratch.EventPicks, state.EventsCursor, (uint)_tick, _sessions.ControlledOf(session), session, ref geometry, out eventCount,
                out eventBytes, out eventsLost);

            // Events take at most half a frame: past it they are counted, not sent, so a burst cannot make every frame oversize and starve the session.
            if (eventBytes > _maxFrameBytes / 2)
            {
                EventHub.Shed(scratch.EventPicks, ref eventCount, ref eventBytes, ref eventsLost);
            }
        }

        // The aggregate tier (09 § 8): on the session's aggregate tick — at most its rate, staggered by slot — the tiles of its region that changed since its
        // last AGG, and the tiles its region newly covers; every non-empty tile, with RESET, on the first or after a reset.
        var (aggGrid, aggPeriod, aggRadius) = Profiles.AggregateOf(_pushProfiles[index]);
        var aggDue = false;
        var aggReset = false;
        var aggRows = 0;
        var aggAnchor = default(Vector3D);
        AggregateCounts aggCounts = null;
        if (aggGrid >= 0 && (uint)session.Slot < (uint)_aggLastTick.Length)
        {
            aggCounts = push.Aggregates[aggGrid];
            var sent = _aggGeneration[session.Slot] == session.Generation;
            aggDue = !sent || reset || ((_tick + session.Slot) % aggPeriod) == 0;
            if (aggDue)
            {
                aggReset = !sent || reset;
                aggAnchor = aggRadius > 0 ? push.PendingAnchorOf(session) : default;
                aggRows = _pushRegion[index]
                    ? SelectRegionAggregateRows(push, session, aggCounts, scratch, aggReset, sent ? _aggLastTick[session.Slot] : 0u)
                    : SelectAggregateRows(aggCounts, scratch, aggReset, sent ? _aggLastTick[session.Slot] : 0u, aggRadius, aggAnchor,
                        _aggAnchor[session.Slot]);
            }
        }

        var aggWrite = aggDue && (aggRows > 0 || aggReset);
        var stats = Stats;
        var emitStats = stats != null && stats.IsEmissionTick && (send->Caps & Capabilities.Stats) != 0;
        var newlyComplete = complete && !state.ViewComplete;

        // DEBUG (09 § 15), for a session granted the cap — which admission grants only under AllowDebug: the grid with its first frame and every RESET, its
        // push geometry whenever it changed. The geometry is the pending one, what the session holds once this frame is published.
        var debugGrid = false;
        var debugGeometry = 0;
        var debugHash = 0UL;
        if ((send->Caps & Capabilities.Debug) != 0 && (uint)session.Slot < (uint)_debugGeneration.Length)
        {
            var profile = _pushProfiles[index];
            var shape = _pushRegion[index] ? PushShape.Region : _pushWorld[index] ? PushShape.World : PushShape.Sphere;
            debugGeometry = push.WriteDebugGeometry(session, shape, Profiles.SlackOf(profile), Profiles.NearOf(profile).Budget, complete,
                scratch.DebugGeometry);
            var hash = CanonicalHashBuilder.Create();
            hash.AddBytes(scratch.DebugGeometry.AsSpan(0, debugGeometry));
            debugHash = hash.Value;
            debugGrid = reset || _debugGeneration[session.Slot] != session.Generation;
            if (!debugGrid && debugHash == _debugHash[session.Slot])
            {
                debugGeometry = 0;
            }
        }

        var debugWrite = debugGrid || debugGeometry > 0;

        // SELF and ACKS (11 § 2): the controlled entity's owner groups changed since the last published frame, lastSeq, and the rejections since then.
        // The first-frame RESET (below) decided before SELF, which sends every owner group on a RESET.
        var first = !reset && Realm != null && state.PendingRealm != state.CommittedRealm;
        PrepareSelf(session, state, reset || first, scratch, ref follow, out var self);
        if (records == 0 && eventCount == 0 && !aggWrite && !reset && !emitStats && !newlyComplete && !debugWrite && !self.Write && self.Acks == 0)
        {
            // Nothing to say. The anchor may still have moved and a cell with nothing in it may have been delivered; neither changes what the client holds,
            // so the pending state is committed even though no frame is.
            send->AbandonIdleFrame(sequence);
            ReturnIfValid(recycled);
            NoteSkip(state, counted: false);
            push.Commit(session);
            state.EventsCursor = (uint)_tick;
            CommitSelf(session, state, in self, _pushProfiles[index], ref counters);
            if (aggDue)
            {
                CommitAggregate(session, aggAnchor, _pushRegion[index] ? scratch : null);
            }

            published = 0;
            return;
        }

        // A session's first published frame is a RESET carrying its REALM (typhon.3), and only once it has something to say: a frame for the REALM alone
        // would be one more frame whose presence depends on when the session connected. Nothing was published before it, so the store it clears is empty
        // and nothing gathered above assumed otherwise — it is not a view reset, and is not counted as one.
        if (first)
        {
            reset = true;
            flags |= TickFlags.Reset;
        }

        var bound = UpperBound(scratch) + (emitStats ? stats.MaxBlockBytes : 0) + (eventCount > 0 ? eventBytes + 16 : 0)
            + (aggWrite ? 24 + (aggRows * 5 * (1 + aggCounts.ArchetypeCount)) : 0) + (debugWrite ? 16 + DebugGrid.MaxBytes + debugGeometry : 0)
            + SelfBound(in self) + RealmBlockBound;
        var buffer = scratch.Bytes(bound);
        var writer = new WireWriter(buffer);
        EntitiesEncoder.WriteHeader(ref writer, (uint)_tick, flags, Realm != null, state.PendingFrame);
        for (var a = 0; a < _plans.Length; a++)
        {
            if (scratch.Count(a, FrameListKind.Enter) == 0 && scratch.Count(a, FrameListKind.Segment) == 0 && scratch.Count(a, FrameListKind.State) == 0
                && scratch.Count(a, FrameListKind.Leave) == 0)
            {
                continue;
            }

            EntitiesEncoder.WriteEntities(ref writer, _encodePlans[a], scratch.List(a, FrameListKind.Enter), scratch.List(a, FrameListKind.Segment),
                scratch.List(a, FrameListKind.State), scratch.List(a, FrameListKind.Leave));
        }

        WriteSelf(ref writer, in self, scratch);
        if (eventCount > 0)
        {
            events.Write(ref writer, scratch.EventPicks, eventCount, eventsLost);
        }

        if (aggWrite)
        {
            WriteAggregate(ref writer, aggCounts, scratch, aggRows, aggReset);
        }

        if (emitStats)
        {
            stats.WriteBlock(ref writer, session, state, _tick);
        }

        if (debugWrite)
        {
            WriteDebug(ref writer, push, debugGrid, scratch.DebugGeometry.AsSpan(0, debugGeometry));
        }

        var length = writer.Position;
        if (timing)
        {
            var now = Stopwatch.GetTimestamp();
            counters.EncodeTicks += now - mark;
            mark = now;
        }

        if (length > _maxFrameBytes)
        {
            counters.OversizeSkips++;
            send->AbandonFrame(sequence);
            ReturnIfValid(recycled);
            NoteSkip(state);
            push.NoteNotPublished(session);
            return;
        }

        var block = recycled;
        if (!Pool.TryRentOrKeep(length, ref block, out var previous))
        {
            ReturnIfValid(block);
            send->AbandonFrame(sequence);
            NoteSkip(state);
            push.NoteNotPublished(session);
            return;
        }

        ReturnIfValid(previous);
        buffer[..length].CopyTo(new Span<byte>(block.Bytes, block.Capacity));
        PublishRealmView(session, state, reset);
        send->PublishFrame(sequence, block, length, _tick);
        published = length;
        if (eventCount > 0)
        {
            events.NoteDelivered(eventCount, eventsLost);
        }

        // COMMIT — the anchor and the delivered cells move with the frame that describes them, and so do the owner state and the acknowledgements.
        push.Commit(session);
        state.EventsCursor = (uint)_tick;
        CommitSelf(session, state, in self, _pushProfiles[index], ref counters);
        if (aggDue)
        {
            CommitAggregate(session, aggAnchor, _pushRegion[index] ? scratch : null);
        }

        if (push.Shadow)
        {
            push.ShadowApply(session, scratch, _plans.Length, reset);
        }
        if (newlyComplete)
        {
            state.ViewComplete = true;
        }
        else if (!complete)
        {
            state.ViewComplete = false;
        }

        if (timing)
        {
            counters.PublishTicks += Stopwatch.GetTimestamp() - mark;
        }

        if (emitStats)
        {
            StatsEncoder.NoteBlockPublished(state, _tick);
        }

        if (debugWrite)
        {
            _debugGeneration[session.Slot] = session.Generation;
            if (debugGeometry > 0)
            {
                _debugHash[session.Slot] = debugHash;
            }
        }

        state.BytesPublished += length;
        state.PendingReset = false;
        if (reset)
        {
            CommitRealm(state);
        }

        state.FramesProduced++;
        state.FramesSinceDegrade++;
        scratch.AddReady(session);

        counters.FramesProduced++;
        counters.BytesEncoded += length;
        counters.RecordsEncoded += records;
        for (var a = 0; a < _plans.Length; a++)
        {
            counters.EntersEmitted += scratch.Count(a, FrameListKind.Enter);
            counters.LeavesEmitted += scratch.Count(a, FrameListKind.Leave);
        }
    }

    /// <summary>Writes a <c>DEBUG</c> block (03 § 3, 09 § 15): the <c>GRID</c> sub-block when asked, then the <c>PUSH_GEOMETRY</c> payload when there is one.</summary>
    private static void WriteDebug(ref WireWriter w, PushReplication push, bool grid, ReadOnlySpan<byte> geometry)
    {
        var mark = TickWriter.BeginBlock(ref w, BlockTypes.Debug);
        if (grid)
        {
            Span<byte> payload = stackalloc byte[DebugGrid.MaxBytes];
            var g = new WireWriter(payload);
            push.DebugGrid.Write(ref g);
            w.WriteU8(DebugSubTypes.Grid);
            w.WriteVaru((uint)g.Position);
            w.WriteBytes(g.Written);
        }

        if (geometry.Length > 0)
        {
            w.WriteU8(DebugSubTypes.PushGeometry);
            w.WriteVaru((uint)geometry.Length);
            w.WriteBytes(geometry);
        }

        TickWriter.EndBlock(ref w, mark);
    }
}

/// <summary>A session's geometry for the events' geometric routes (09 § 11): the push step's own known-set test, committed or pending.</summary>
internal readonly ref struct SessionEventGeometry : IEventGeometry
{
    private readonly PushReplication _push;
    private readonly SessionId _session;
    private readonly RealmTable _realms;

    public SessionEventGeometry(PushReplication push, SessionId session, bool world, RealmTable realms = null)
    {
        _push = push;
        _session = session;
        _realms = realms;
        World = world;
    }

    public bool World { get; }

    public ushort Realm => _push.ServedRealm;

    public bool InRealm(ushort realm, bool subtree) => RealmTree.Reaches(_realms, _push.ServedRealm, realm, subtree);

    public void CellBox(out int minCx, out int maxCx, out int minCy, out int maxCy, out int minCz, out int maxCz) =>
        _push.SessionCellBox(_session, out minCx, out maxCx, out minCy, out maxCy, out minCz, out maxCz);

    public bool Sees(float x, float y, float z, float viewRadius) =>
        World ? _push.WorldSeesPoint(_session, x, y, z) : _push.SeesPoint(_session, x, y, z, viewRadius);
}

/// <summary>No geometry: a session with no profile sees no point, so the geometric routes never match it (09 § 11).</summary>
internal readonly ref struct NoEventGeometry : IEventGeometry
{
    private readonly ushort _realm;
    private readonly RealmTable _realms;

    public NoEventGeometry(ushort realm, RealmTable realms = null)
    {
        _realm = realm;
        _realms = realms;
    }

    public NoEventGeometry() => _realm = RealmId.NoneValue;

    public bool World => false;

    public ushort Realm => _realm;

    public bool InRealm(ushort realm, bool subtree) => RealmTree.Reaches(_realms, _realm, realm, subtree);

    public void CellBox(out int minCx, out int maxCx, out int minCy, out int maxCy, out int minCz, out int maxCz)
    {
        (minCx, minCy, minCz) = (0, 0, 0);
        (maxCx, maxCy, maxCz) = (-1, -1, -1);
    }

    public bool Sees(float x, float y, float z, float viewRadius) => false;
}

/// <summary>The realm parent tree, for <see cref="EventRouting.ToRealm"/> (12-realms § 3): routing only, never visibility.</summary>
internal static class RealmTree
{
    /// <summary>The deepest a parent chain is walked: a realm tree is shallow (galaxy → planet → interior).</summary>
    public const int MaxDepth = 8;

    /// <summary>Whether a session in <paramref name="session"/>'s realm hears an event addressed to <paramref name="target"/> (and its subtree).</summary>
    public static bool Reaches(RealmTable realms, ushort session, ushort target, bool subtree)
    {
        if (session == RealmId.NoneValue)
        {
            return false;
        }

        if (session == target)
        {
            return true;
        }

        if (!subtree || realms == null)
        {
            return false;
        }

        var at = session;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var parent = realms.TryGet(at)?.Config?.Parent ?? RealmId.None;
            if (parent.IsNone)
            {
                return false;
            }

            if (parent.Value == target)
            {
                return true;
            }

            at = parent.Value;
        }

        return false;
    }
}
