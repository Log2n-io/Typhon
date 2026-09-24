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

    /// <summary>The declared profiles, which name each session's observer.</summary>
    internal SubscriptionProfiles Profiles;

    /// <summary>The engine, whose EntityMap and clusters a followed entity's position is read from (09 § 6).</summary>
    internal DatabaseEngine Engine;

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
    private int[] _pushDivisor = [];
    private int _pushSessionCount;
    private int _pushCursor;

    /// <summary>Serial: collects this tick's push sessions and their viewpoints, prepares their rows, and indexes the push events.</summary>
    private void BeginPushTick()
    {
        _pushSessionCount = 0;
        _pushCursor = 0;
        if (Push == null || Profiles == null)
        {
            return;
        }

        var n = 0;
        foreach (var session in _sessions)
        {
            if (!Profiles.TryGetProfile(session, out var profile, out var world, out var divisor))
            {
                continue;
            }

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
                Array.Resize(ref _pushDivisor, grown);
            }

            _pushWorld[n] = world;
            _pushDivisor[n] = divisor;
            _pushFollow[n] = EntityId.Null;
            switch (Profiles.SourceOf(profile))
            {
                case ViewpointSource.Fixed:
                    _pushPlaced[n] = true;
                    _pushViewpoints[n] = Profiles.PlacementOf(profile);
                    break;
                case ViewpointSource.Bound:
                    _pushPlaced[n] = false;
                    _pushFollow[n] = Profiles.BoundEntityOf(profile);
                    break;
                case ViewpointSource.Controlled:
                    // A session that controls nothing yet is nowhere, as an unplaced one is.
                    _pushPlaced[n] = false;
                    _pushFollow[n] = _sessions.ControlledOf(session);
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
            PrepareSession(session);
            // Only a World session served this tick can fill: a rate class skips the others (the check the frame stage repeats below).
            if (world && (divisor <= 1 || ((_tick + session.Slot) % (uint)divisor) == 0))
            {
                var sessionState = StateOf(session);
                Push.NoteWorldSession(session, sessionState != null && sessionState.PendingReset);
            }
        }

        _pushSessionCount = n;

        // Every tick the track runs is indexed, sessions bound or not: the index is the tick's log slot, and its cell changes are the occupancy's only
        // input (SUB-24). A tick left unindexed would leave the occupancy short of its spawns and crossings.
        if (!Push.Indexed)
        {
            Push.BuildIndex();
        }

        // After the index, whose finish brought the occupancy to this tick: the occupied cells in order, for the World fills still under way.
        Push.PrepareWorldOrder();

        // Distance LOD: the far flushes into the tick's log slot — folded by their stage, or here when the index was built here.
        if (n > 0)
        {
            Push.EndFarFold();
        }

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
                Push.QueueShadowCheck(_pushSessions[k], in Profiles.SetOf(_pushProfiles[k]));
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
            $"  PUSH: {_pushSessionCount} sessions; slots pushed {p.SlotsPushed}, events {p.Events}; enters {p.Enters}, leaves {p.Leaves}, updates {p.Updates}; "
            + $"cells delivered {p.CellsDelivered}, sweeps {p.Sweeps} ({p.SweepSlots} slots); resets {p.Resets}; "
            + $"serial prepare {p.PrepareTicks * f:F0} ms, index {p.IndexTicks * f:F0} ms "
            + $"(sort {p.SortTicks * f:F0}, merge {p.MergeTicks * f:F0}, finish {p.FinishTicks * f:F0}), gather busy {p.GatherTicks * f:F0} ms (cumulative)");
        Console.Error.WriteLine(
            $"  PUSH CELLS: delivery {p.DeliverTicks * f:F0} ms, {p.DeliverDecoded} decoded for {p.DeliverEntered} entered; "
            + $"sweep {p.SweepTicks * f:F0} ms, {p.SweepDecoded} decoded for {p.SweepSlots} in the cell; empty skipped {p.EmptyCellsSkipped}");
        Console.Error.WriteLine($"  PUSH LOD: far fold phase {p.FarPhase} window {p.FarWindow}; updates withheld {p.UpdatesDeferred}, far flushes {p.FarFlushes}, crescent states {p.FarCrescentStates}, fold tail {p.FarEndTicks * f:F0} ms");
        Console.Error.WriteLine(
            $"  PUSH LOG: catch-ups {p.LogCatchUps} over {p.LogCatchUpTicks} missed ticks; resets: too old {p.LogTooOld}, ambiguous {p.LogAmbiguous}; "
            + $"gap re-pushes {p.GapRepushes}, "
            + $"occupancy recounts {p.OccupancyRecounts} ({p.RecountTicks * f:F0} ms)");
        if (p.ValidateClustersPerTick > 0)
        {
            var groups = new System.Text.StringBuilder();
            for (var a = 0; a < _plans.Length; a++)
            {
                var planGroups = _plans[a].Groups;
                for (var g = 0; planGroups != null && g < planGroups.Length; g++)
                {
                    var n = p.ForgottenGroups(a, g);
                    if (n > 0)
                    {
                        groups.Append($" {_plans[a].Name}.{planGroups[g].Name}={n}");
                    }
                }
            }

            Console.Error.WriteLine($"  PUSH VALIDATOR: {p.ValidatedSlots} slots checked, forgotten pushes {p.ForgottenPushes} (motion {p.ForgottenMotion});{groups}");
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
        if (_pushSessionCount == 0)
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

                AssemblePush(i, scratch, ref follow, ref lost, ref counters, ref enters, ref leaves, ref updates);
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

        Interlocked.Add(ref Push.Enters, enters);
        Interlocked.Add(ref Push.Leaves, leaves);
        Interlocked.Add(ref Push.Updates, updates);
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

    private void AssemblePush(int index, FrameWorkerScratch scratch, ref BoundViewpoint follow, ref long lost, ref FrameCounters counters, ref long enters,
        ref long leaves, ref long updates)
    {
        var session = _pushSessions[index];
        var state = StateOf(session);
        if (state == null || state.Generation != session.Generation)
        {
            return;
        }

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
            Push.NoteNotPublished(session);
            return;
        }

        if (SkipPolicy.AcknowledgementLag(send->ProducedTick, send->AckedTick) > _lagBoundTicks)
        {
            send->NoteSkipped();
            NoteSkip(state);
            Push.NoteNotPublished(session);
            return;
        }

        if (!send->TryBeginFrame(out var sequence, out var recycled))
        {
            NoteSkip(state);
            Push.NoteNotPublished(session);
            return;
        }

        var timing = PhaseTimingEnabled;
        var mark = timing ? Stopwatch.GetTimestamp() : 0L;

        scratch.BeginSession(_plans.Length);
        if (!_pushFollow[index].IsNull)
        {
            ResolveFollowed(index, session, ref follow, ref lost);
        }

        var reset = _pushWorld[index]
            ? Push.GatherWorld(session, state.PendingReset, in Profiles.SetOf(_pushProfiles[index]), scratch, Math.Max(1, _options.EnterBudgetPerFrame), ref enters, ref leaves,
                ref updates, out var complete)
            : Push.Gather(session, _pushPlaced[index], _pushViewpoints[index], _pushRadius[index], Profiles.BandsOf(_pushProfiles[index]), state.PendingReset,
                in Profiles.SetOf(_pushProfiles[index]), scratch,
                _encodePlans,
                Math.Max(1, _options.EnterBudgetPerFrame), ref enters, ref leaves, ref updates, out complete);

        if (timing)
        {
            var now = Stopwatch.GetTimestamp();
            counters.GatherTicks += now - mark;
            mark = now;
        }

        var flags = TickFlags.None;
        if (reset)
        {
            flags |= TickFlags.Reset;
            Interlocked.Increment(ref Push.Resets);
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

        var stats = Stats;
        var emitStats = stats != null && stats.IsEmissionTick && (send->Caps & Capabilities.Stats) != 0;
        var newlyComplete = complete && !state.ViewComplete;
        if (records == 0 && !reset && !emitStats && !newlyComplete)
        {
            // Nothing to say. The anchor may still have moved and a cell with nothing in it may have been delivered; neither changes what the client holds,
            // so the pending state is committed even though no frame is.
            send->AbandonIdleFrame(sequence);
            ReturnIfValid(recycled);
            NoteSkip(state, counted: false);
            Push.Commit(session);
            return;
        }

        var bound = UpperBound(scratch) + (emitStats ? stats.MaxBlockBytes : 0);
        var buffer = scratch.Bytes(bound);
        var writer = new WireWriter(buffer);
        EntitiesEncoder.WriteHeader(ref writer, (uint)_tick, flags);
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

        if (emitStats)
        {
            stats.WriteBlock(ref writer, session, state, _tick);
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
            Push.NoteNotPublished(session);
            return;
        }

        var block = recycled;
        if (!Pool.TryRentOrKeep(length, ref block, out var previous))
        {
            ReturnIfValid(block);
            send->AbandonFrame(sequence);
            NoteSkip(state);
            Push.NoteNotPublished(session);
            return;
        }

        ReturnIfValid(previous);
        buffer[..length].CopyTo(new Span<byte>(block.Bytes, block.Capacity));
        send->PublishFrame(sequence, block, length, _tick);

        // COMMIT — the anchor and the delivered cells move with the frame that describes them.
        Push.Commit(session);
        if (Push.Shadow)
        {
            Push.ShadowApply(session, scratch, _plans.Length, reset);
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

        state.BytesPublished += length;
        state.PendingReset = false;
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
}
