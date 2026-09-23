using System;
using System.Diagnostics;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>PROTOTYPE — the frame stage's push half: sessions bound to a push profile, served from <see cref="PushReplication"/>'s cell index.</summary>
/// <remarks>
/// <para>
/// <b>Same wire, same hand-off, no known-set.</b> A push session's records are gathered by <see cref="PushReplication.Gather"/> into the same per-archetype
/// sub-lists the pull path fills, encoded by the same <see cref="EntitiesEncoder"/>, and published through the same send state and frame pool — so a client
/// cannot tell which path served it. What it does not have is a known-set, an interest view or an owed debt: what it knows is geometry.
/// </para>
/// <para>
/// <b>A frame that is not published costs a RESET.</b> The events of a skipped tick are gone (the push log that would replay them is step 4 of the build
/// order and is not built), so the next frame clears the client and re-delivers its view cell by cell. Measured by <see cref="PushReplication.Resets"/>.
/// </para>
/// </remarks>
internal sealed unsafe partial class FrameAssembler
{
    /// <summary>The push path, when some profile is push-served.</summary>
    internal PushReplication Push;

    private SessionId[] _pushSessions = [];
    private Vector3D[] _pushViewpoints = [];
    private bool[] _pushPlaced = [];
    private ulong[] _pushMasks = [];
    private int _pushSessionCount;
    private int _pushCursor;

    /// <summary>How many sessions the push path serves this tick.</summary>
    internal int PushSessionCount => _pushSessionCount;

    /// <summary>Serial: collects this tick's push sessions and their viewpoints, prepares their rows, and indexes the push events.</summary>
    private void BeginPushTick()
    {
        _pushSessionCount = 0;
        _pushCursor = 0;
        if (Push == null || _interest == null)
        {
            return;
        }

        var n = 0;
        foreach (var session in _sessions)
        {
            if (!_interest.TryGetPushProfile(session, out _, out var archetypes))
            {
                continue;
            }

            if (n == _pushSessions.Length)
            {
                var grown = Math.Max(16, n * 2);
                Array.Resize(ref _pushSessions, grown);
                Array.Resize(ref _pushViewpoints, grown);
                Array.Resize(ref _pushPlaced, grown);
                Array.Resize(ref _pushMasks, grown);
            }

            _pushPlaced[n] = _sessions.TryGetViewpoint(session, out var viewpoint);
            _pushViewpoints[n] = viewpoint;
            var mask = 0UL;
            foreach (var a in archetypes)
            {
                mask |= 1UL << a;
            }

            _pushMasks[n] = mask;
            _pushSessions[n++] = session;
            PrepareSession(session);
        }

        _pushSessionCount = n;
        if (n > 0)
        {
            Push.BuildIndex();
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
                Push.QueueShadowCheck(_pushSessions[k], _pushMasks[k]);
            }
        }

        if (_tick % 500 == 0)
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
            + $"serial prepare {p.PrepareTicks * f:F0} ms, index {p.IndexTicks * f:F0} ms, gather busy {p.GatherTicks * f:F0} ms (cumulative)");
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

        long enters = 0, leaves = 0, updates = 0;
        while (true)
        {
            var i = Interlocked.Increment(ref _pushCursor) - 1;
            if (i >= _pushSessionCount)
            {
                break;
            }

            AssemblePush(i, scratch, ref counters, ref enters, ref leaves, ref updates);
        }

        Interlocked.Add(ref Push.Enters, enters);
        Interlocked.Add(ref Push.Leaves, leaves);
        Interlocked.Add(ref Push.Updates, updates);
    }

    private void AssemblePush(int index, FrameWorkerScratch scratch, ref FrameCounters counters, ref long enters, ref long leaves, ref long updates)
    {
        var session = _pushSessions[index];
        var state = StateOf(session);
        if (state == null || state.Generation != session.Generation)
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
        var reset = Push.Gather(session, _pushPlaced[index], _pushViewpoints[index], state.PendingReset, _pushMasks[index], scratch, _encodePlans,
            Math.Max(1, _options.EnterBudgetPerFrame), ref enters, ref leaves, ref updates, out var complete);

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
                scratch.List(a, FrameListKind.State), scratch.List(a, FrameListKind.Leave), default, default);
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
        state.Baseline = _tick;
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
        state.DeferredEnters = 0;
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
