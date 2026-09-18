using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Client;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// One tick of the whole Engine-Subscriptions track, driven by hand: interest, the blocks step, projection, then frame assembly — followed by the send
/// side's half of the hand-off, so a test reads the bytes a client would.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the track's order, not an approximation of it.</b> S2b's inputs are the interest pass's per-session runs and the per-entity state S1 wrote into
/// the replication blocks, so a fixture that synthesized either would be asserting against a world the engine does not produce. The two steps this harness
/// re-implements rather than calls — the blocks step's gather, and the projection dispatch — are `private static` members of the stage classes; both are a
/// dozen lines of partitioning with no behaviour of their own, and <c>ProjectionPassTests</c> already drives the pass the same way.
/// </para>
/// <para>
/// <b>The send side is real.</b> A published frame is claimed through <see cref="SessionSendState.TryClaimFrame"/> against the publication gate and released
/// through <see cref="SessionSendState.CompleteSend"/>, exactly as <c>FrameHandoffTests.RunHandoffs</c> drives it. A test that wants a session SKIPPED simply
/// does not drain it: two frames later the producer finds no free slot, which is the engine's own skip and not a flag a fixture set.
/// </para>
/// </remarks>
sealed unsafe class FrameHarness : IDisposable
{
    private readonly InterestHarness _interest;
    private readonly Dictionary<uint, SessionReplica> _replicas = [];

    private FrameHarness(InterestHarness interest)
    {
        _interest = interest;
        CatalogPlan = CatalogPlan.Compile(interest.Subscriptions.Catalog.Canonical);
    }

    /// <summary>The engine whose clusters are projected.</summary>
    public DatabaseEngine Engine => _interest.Engine;

    /// <summary>The interest half of the harness, for a fixture that has to read the cluster occupancy the track ran against.</summary>
    public InterestHarness Interest => _interest;

    /// <summary>Everything replication owns.</summary>
    public SubscriptionsRuntime Subscriptions => _interest.Subscriptions;

    /// <summary>The assembler under test.</summary>
    public FrameAssembler Assembler => Subscriptions.Frames;

    /// <summary>The session table.</summary>
    public SessionTable Sessions => Subscriptions.Sessions;

    /// <summary>The compiled catalog a client decodes against — the engine's own, exactly as <c>WELCOME</c> would carry it.</summary>
    public CatalogPlan CatalogPlan { get; }

    /// <summary>The tick the harness last ran.</summary>
    public long Tick { get; private set; }

    /// <summary>Builds the harness over an engine whose archetypes are already initialized.</summary>
    /// <param name="engine">The engine.</param>
    /// <param name="declare">Writes the projections and profiles.</param>
    /// <param name="name">A name for the resource registry.</param>
    /// <param name="options">Replication options; the defaults are used for anything not set.</param>
    /// <returns>The harness.</returns>
    public static FrameHarness Create(DatabaseEngine engine, Action<SubscriptionsRegistry> declare, string name, SubscriptionsOptions options = null)
    {
        var interest = InterestHarness.Create(engine, declare, name, options);
        try
        {
            return new FrameHarness(interest);
        }
        catch
        {
            interest.Dispose();
            throw;
        }
    }

    /// <summary>Admits and opens sessions bound to a profile, and gives each one a replica to apply its frames to.</summary>
    /// <param name="count">How many.</param>
    /// <param name="profile">The declared profile.</param>
    /// <returns>The identities.</returns>
    public SessionId[] OpenSessions(int count, string profile)
    {
        var sessions = _interest.OpenSessions(count, profile);
        foreach (var session in sessions)
        {
            _replicas[session.Value] = new SessionReplica(CatalogPlan);
        }

        return sessions;
    }

    /// <summary>The client-side replica a session's frames have been applied to.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The replica.</returns>
    public SessionReplica Replica(SessionId session) => _replicas[session.Value];

    /// <summary>The plan index of a replicated archetype, by wire name.</summary>
    /// <param name="name">The archetype's wire name.</param>
    /// <returns>The index.</returns>
    public int PlanIndex(string name) => _interest.PlanIndex(name);

    /// <summary>Runs one whole tick of the track: interest, blocks, projection, frames, then the durability gate.</summary>
    /// <param name="tick">The tick number, which must advance.</param>
    /// <param name="workers">Worker-pool width for the two partitioned stages.</param>
    public void RunTick(long tick, int workers = 1)
    {
        Tick = tick;
        Sessions.BeginTick();
        RunInterest(tick, workers);
        RunProject(tick);
        RunFrames(tick, workers);

        // The publication gate, which the tick driver moves once the unit of work has flushed (SUB-02). Nothing a test reads is sendable before it.
        Assembler.Gate.Publish(tick);
    }

    /// <summary>
    /// Runs the tick that gives every hit cluster its replication block, which produces no frame at all.
    /// </summary>
    /// <param name="tick">The tick number, normally 1.</param>
    /// <remarks>
    /// <b>The one-tick lag is the track's, not the harness's.</b> The interest stage is the only writer of a watched bit and it only ever sets one on a block
    /// that already exists; a cluster hit for the first time is put on the blocks step's new-block list, gets its block there, and is marked on the FOLLOWING
    /// tick (<c>SubscriptionsProjectExecSystem.CreateNewBlocks</c>). So a session's first tick legitimately has hits and no records — and the assembler
    /// counts those hits as owed rather than producing an empty frame that claims a complete view.
    /// </remarks>
    public void PrimeBlocks(long tick = 1)
    {
        RunTick(tick);
        Assert.That(HasFrameForAnyone(), Is.False, "the priming tick describes nothing, so it produces no frame");
    }

    private bool HasFrameForAnyone()
    {
        foreach (var session in _replicas.Keys)
        {
            if (HasFrame(new SessionId((ushort)session, (ushort)(session >> 16))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs a tick's interest, blocks and projection steps but not the frame stage — what an allocation measurement brackets.</summary>
    /// <param name="tick">The tick number.</param>
    /// <param name="workers">Worker-pool width.</param>
    public void RunUpToFrames(long tick, int workers = 1)
    {
        Tick = tick;
        Sessions.BeginTick();
        RunInterest(tick, workers);
        RunProject(tick);
    }

    /// <summary>Runs the frame stage alone, for a tick whose earlier steps <see cref="RunUpToFrames"/> already ran.</summary>
    /// <param name="tick">The tick number.</param>
    /// <param name="workers">Worker-pool width.</param>
    public void RunFramesOnly(long tick, int workers = 1) => RunFrames(tick, workers);

    /// <summary>Claims and applies every frame ready for a session, releasing each slot as the send side would.</summary>
    /// <param name="session">The session.</param>
    /// <returns>How many frames were delivered.</returns>
    public int Deliver(SessionId session)
    {
        var replica = _replicas[session.Value];
        var send = Assembler.SendStateOf(session.Slot);
        var delivered = 0;

        while (send->TryClaimFrame(Assembler.Gate.CommittedTick, out var frame))
        {
            var bytes = new ReadOnlySpan<byte>(frame.Bytes, frame.Length);
            replica.Apply(bytes);
            send->CompleteSend(frame.Sequence);
            delivered++;
        }

        return delivered;
    }

    /// <summary>Claims and copies out every frame ready for a session without applying it — what the golden vector is built from.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The frames, in order.</returns>
    public List<byte[]> Collect(SessionId session)
    {
        var frames = new List<byte[]>();
        var send = Assembler.SendStateOf(session.Slot);
        while (send->TryClaimFrame(Assembler.Gate.CommittedTick, out var frame))
        {
            frames.Add(new ReadOnlySpan<byte>(frame.Bytes, frame.Length).ToArray());
            send->CompleteSend(frame.Sequence);
        }

        return frames;
    }

    /// <summary>Claims one frame, decodes it into a call log and releases it.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The log, or <see langword="null"/> when no frame was ready.</returns>
    public FrameLog Read(SessionId session)
    {
        var send = Assembler.SendStateOf(session.Slot);
        if (!send->TryClaimFrame(Assembler.Gate.CommittedTick, out var frame))
        {
            return null;
        }

        var log = new FrameLog();
        var bytes = new ReadOnlySpan<byte>(frame.Bytes, frame.Length);
        log.Decode(bytes, CatalogPlan);
        send->CompleteSend(frame.Sequence);
        return log;
    }

    /// <summary>Whether a frame is waiting for a session.</summary>
    /// <param name="session">The session.</param>
    /// <returns><see langword="true"/> when one is ready.</returns>
    public bool HasFrame(SessionId session)
    {
        var send = Assembler.SendStateOf(session.Slot);
        return send->ReadySequence > send->SentSequence;
    }

    /// <summary>The per-session frame state, for a test that has to set up a condition the engine reaches rarely.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The state.</returns>
    public SessionFrameState StateOf(SessionId session) => Assembler.StateOf(session);

    /// <inheritdoc />
    public void Dispose() => _interest.Dispose();

    private void RunInterest(long tick, int workers)
    {
        var chunks = Subscriptions.Interest.BeginTick(tick, workers);
        for (var c = 0; c < chunks; c++)
        {
            using (EpochGuard.Enter(Engine.EpochManager))
            {
                Subscriptions.Interest.ExecuteChunk(c, chunks);
            }
        }
    }

    /// <summary>The blocks step and S1, single-threaded: create the blocks the hits asked for, gather the watched lists, then project each block.</summary>
    private void RunProject(long tick)
    {
        var interest = Subscriptions.Interest;
        var states = Subscriptions.ReplicationStates;
        var plans = Subscriptions.Plans;
        var stamp = (uint)tick;

        _interest.CreateRequestedBlocks();

        for (var a = 0; a < states.Length; a++)
        {
            states[a].BeginWatchedBlocks(stamp);
        }

        for (var w = 0; w < interest.ArenaCount; w++)
        {
            var watched = interest.Arena(w).WatchedBlocks;
            for (var i = 0; i < watched.Count; i++)
            {
                var block = (ReplicationBlockHeader*)watched[i];
                if (block->ChunkId < 0)
                {
                    continue;
                }

                for (var a = 0; a < states.Length; a++)
                {
                    if (states[a].Directory.TryGetBlock(block->ChunkId, out var found) && found == block)
                    {
                        states[a].WatchedBlocks.Add(block);
                        break;
                    }
                }
            }
        }

        for (var a = 0; a < states.Length; a++)
        {
            states[a].BeginProjectTick(workers: 1);
        }

        using var guard = EpochGuard.Enter(Engine.EpochManager);
        for (var a = 0; a < plans.Length; a++)
        {
            var state = states[a];
            var clusterState = state.ClusterState;
            if (clusterState?.ClusterSegment == null || state.WatchedBlocks.Count == 0)
            {
                continue;
            }

            using var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
            var transient = clusterState.TransientSegment;
            var transientAccessor = transient != null ? transient.CreateChunkAccessor() : default;
            try
            {
                for (var i = 0; i < state.WatchedBlocks.Count; i++)
                {
                    var block = state.WatchedBlocks[i];
                    var transientBase = transient != null ? transientAccessor.GetChunkAddress(block->ChunkId) : null;
                    ProjectionPass.ProjectBlock(plans[a], a, state, 0, block, accessor.GetChunkAddress(block->ChunkId), transientBase, stamp);
                }
            }
            finally
            {
                transientAccessor.Dispose();
            }
        }
    }

    private void RunFrames(long tick, int workers)
    {
        var chunks = Assembler.BeginTick(Subscriptions.Interest, tick, workers);
        for (var c = 0; c < chunks; c++)
        {
            using (EpochGuard.Enter(Engine.EpochManager))
            {
                Assembler.ExecuteChunk(c, chunks);
            }
        }
    }
}

/// <summary>One session's client-side replica: the store <c>Typhon.Client</c> builds, and the applier that fills it.</summary>
sealed class SessionReplica
{
    private readonly FrameApplier _applier;

    /// <summary>Creates a replica against the engine's own catalog.</summary>
    /// <param name="plan">The compiled catalog.</param>
    public SessionReplica(CatalogPlan plan)
    {
        Store = new WorldStore(plan);
        _applier = new FrameApplier(Store);
    }

    /// <summary>The replica.</summary>
    public WorldStore Store { get; }

    /// <summary>Applies one <c>TICK</c> message.</summary>
    /// <param name="frame">The message.</param>
    public void Apply(ReadOnlySpan<byte> frame) => _applier.Apply(frame);

    /// <summary>The netIds the replica holds for an archetype, ascending.</summary>
    /// <param name="archetype">The archetype's wire index.</param>
    /// <returns>The identities.</returns>
    public uint[] NetIds(int archetype)
    {
        var store = Store.Archetypes[archetype];
        var ids = new List<uint>();
        for (var i = 0; i < store.LiveCount; i++)
        {
            ids.Add(store.NetIds[store.Live[i]]);
        }

        ids.Sort();
        return ids.ToArray();
    }

    /// <summary>The head segment's position for an entity the replica holds.</summary>
    /// <param name="archetype">The archetype's wire index.</param>
    /// <param name="netId">The entity.</param>
    /// <returns>The position, one value per axis.</returns>
    public double[] Position(int archetype, uint netId)
    {
        Assert.That(Store.TryLocate(netId, out var located, out var slot), Is.True, $"the replica does not hold netId {netId}");
        Assert.That(located, Is.EqualTo(archetype));
        return Store.Archetypes[archetype].HeadPosition(slot).ToArray();
    }

    /// <summary>One field's first component for an entity the replica holds, or <see langword="null"/> when it holds no such entity.</summary>
    /// <param name="archetype">The archetype's wire index.</param>
    /// <param name="netId">The entity.</param>
    /// <param name="field">The field's wire name.</param>
    /// <returns>The value.</returns>
    public double? Value(int archetype, uint netId, string field)
    {
        if (!Store.TryLocate(netId, out var located, out var slot) || located != archetype)
        {
            return null;
        }

        var store = Store.Archetypes[archetype];
        foreach (var plan in store.Plan.Fields)
        {
            if (plan.Name == field)
            {
                return store.Numbers[plan.Ordinal][slot * plan.Components];
            }
        }

        return null;
    }
}

/// <summary>Records every call one decoded frame makes into its sink, so a test can assert the ORDER a decoder sees and not only the state it ends in.</summary>
sealed class FrameLog : ITickSink
{
    /// <summary>Every call, in stream order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The archetypes an <c>ENTITIES</c> block was opened for, in stream order — a repeat is the 1007 the reader refuses.</summary>
    public List<string> Blocks { get; } = [];

    /// <summary>Enters, in stream order.</summary>
    public List<uint> Enters { get; } = [];

    /// <summary>Segments, in stream order.</summary>
    public List<uint> Segments { get; } = [];

    /// <summary>State records, in stream order, with their masks.</summary>
    public List<(uint NetId, byte Mask)> States { get; } = [];

    /// <summary>Leaves, in stream order.</summary>
    public List<uint> Leaves { get; } = [];

    /// <summary>The frame's tick.</summary>
    public uint TickNumber { get; private set; }

    /// <summary>The frame's flags.</summary>
    public TickFlags Flags { get; private set; }

    /// <summary>Decodes one <c>TICK</c> message into this log.</summary>
    /// <param name="frame">The message.</param>
    /// <param name="plan">The compiled catalog.</param>
    public void Decode(ReadOnlySpan<byte> frame, CatalogPlan plan)
    {
        var sink = new Sink(this);
        TickReader.Read(frame, plan, ref sink);
    }

    /// <inheritdoc />
    public void BeginTick(uint tick, TickFlags flags, uint periodUs)
    {
        TickNumber = tick;
        Flags = flags;
        Calls.Add($"beginTick {tick}");
    }

    /// <inheritdoc />
    public void BeginEntities(ArchetypePlan archetype)
    {
        Blocks.Add(archetype.Name);
        Calls.Add($"beginEntities {archetype.Name}");
    }

    /// <inheritdoc />
    public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
        Enters.Add(netId);
        Calls.Add($"enter {netId}");
    }

    /// <inheritdoc />
    public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
        Segments.Add(netId);
        Calls.Add($"segment {netId}");
    }

    /// <inheritdoc />
    public void State(uint netId, byte groupMask)
    {
        States.Add((netId, groupMask));
        Calls.Add($"state {netId} 0x{groupMask:x2}");
    }

    /// <inheritdoc />
    public void Leave(uint netId)
    {
        Leaves.Add(netId);
        Calls.Add($"leave {netId}");
    }

    /// <inheritdoc />
    public void Event(MessagePlan type) => Calls.Add($"event {type.Name}");

    /// <inheritdoc />
    public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask) => Calls.Add($"self {netId}");

    /// <inheritdoc />
    public void Ack(ushort seq, byte reason) => Calls.Add($"ack {seq}");

    /// <inheritdoc />
    public void Source(ushort requestId, byte status, ushort code) => Calls.Add($"source {requestId}");

    /// <inheritdoc />
    public void BeginAggregate(CatalogGrid grid, bool reset) => Calls.Add($"beginAggregate {grid.Idx}");

    /// <inheritdoc />
    public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts) => Calls.Add($"aggregateCell {cell}");

    /// <inheritdoc />
    public void Metric(MetricPlan metric, int valueIndex, double value) => Calls.Add($"metric {metric.Name}");

    /// <inheritdoc />
    public void Debug(byte subType, scoped ReadOnlySpan<byte> payload) => Calls.Add($"debug {subType}");

    /// <inheritdoc />
    public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload) => Calls.Add($"ext {appTypeId}");

    /// <inheritdoc />
    public void UnknownBlock(byte blockType) => Calls.Add($"unknownBlock {blockType}");

    /// <inheritdoc />
    public void EndTick() => Calls.Add("endTick");

    /// <inheritdoc />
    public void Number(FieldPlan field, scoped ReadOnlySpan<double> components) { }

    /// <inheritdoc />
    public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8) { }

    /// <inheritdoc />
    public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes) { }

    /// <inheritdoc />
    public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components) { }

    /// <summary>The reader's sink, a struct so the decode stays direct; it forwards to the log.</summary>
    private readonly struct Sink : ITickSink
    {
        private readonly FrameLog _log;

        public Sink(FrameLog log) => _log = log;

        public void BeginTick(uint tick, TickFlags flags, uint periodUs) => _log.BeginTick(tick, flags, periodUs);

        public void BeginEntities(ArchetypePlan archetype) => _log.BeginEntities(archetype);

        public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) =>
            _log.Enter(netId, position, velocity, t0, epoch);

        public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) =>
            _log.Segment(netId, position, velocity, t0, epoch);

        public void State(uint netId, byte groupMask) => _log.State(netId, groupMask);

        public void Leave(uint netId) => _log.Leave(netId);

        public void Event(MessagePlan type) => _log.Event(type);

        public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask) => _log.Self(archetype, netId, lastSeq, ownerMask);

        public void Ack(ushort seq, byte reason) => _log.Ack(seq, reason);

        public void Source(ushort requestId, byte status, ushort code) => _log.Source(requestId, status, code);

        public void BeginAggregate(CatalogGrid grid, bool reset) => _log.BeginAggregate(grid, reset);

        public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts) => _log.AggregateCell(cell, counts);

        public void Metric(MetricPlan metric, int valueIndex, double value) => _log.Metric(metric, valueIndex, value);

        public void Debug(byte subType, scoped ReadOnlySpan<byte> payload) => _log.Debug(subType, payload);

        public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload) => _log.Ext(appTypeId, payload);

        public void UnknownBlock(byte blockType) => _log.UnknownBlock(blockType);

        public void EndTick() => _log.EndTick();

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components) { }

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8) { }

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes) { }

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components) { }
    }
}
