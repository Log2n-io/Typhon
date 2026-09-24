using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Client;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// One tick of the whole Engine-Subscriptions track, driven by hand: the blocks step, projection, the push index and far fold, then frame assembly —
/// followed by the send side's half of the hand-off, so a test reads the bytes a client would.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the track's order, not an approximation of it.</b> The frame stage's inputs are the push index and the per-entity state S1 wrote into the
/// replication blocks, so a fixture that synthesized either would be asserting against a world the engine does not produce. The blocks step and the
/// projection dispatch are re-implemented here rather than called: both are a dozen lines of partitioning with no behaviour of their own.
/// </para>
/// <para>
/// <b>The send side is real.</b> A published frame is claimed through <see cref="SessionSendState.TryClaimFrame"/> against the publication gate and released
/// through <see cref="SessionSendState.CompleteSend"/>, exactly as <c>FrameHandoffTests.RunHandoffs</c> drives it. A test that wants a session SKIPPED simply
/// does not drain it: two frames later the producer finds no free slot, which is the engine's own skip and not a flag a fixture set.
/// </para>
/// </remarks>
sealed unsafe class FrameHarness : IDisposable
{
    private readonly ReplicationHarness _replication;
    private readonly Dictionary<uint, SessionReplica> _replicas = [];

    private FrameHarness(ReplicationHarness replication)
    {
        _replication = replication;
        CatalogPlan = CatalogPlan.Compile(replication.Subscriptions.Catalog.Canonical);
    }

    /// <summary>The engine whose clusters are projected.</summary>
    public DatabaseEngine Engine => _replication.Engine;

    /// <summary>The runtime half of the harness, for a fixture that has to read the cluster occupancy the track ran against.</summary>
    public ReplicationHarness Replication => _replication;

    /// <summary>Everything replication owns.</summary>
    public SubscriptionsRuntime Subscriptions => _replication.Subscriptions;

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
    /// <param name="replicationCellM">The cell side the defaults declare, as <see cref="ReplicationHarness.Create"/>.</param>
    /// <returns>The harness.</returns>
    public static FrameHarness Create(DatabaseEngine engine, Action<SubscriptionsRegistry> declare, string name, SubscriptionsOptions options = null,
        double replicationCellM = 0)
    {
        var replication = ReplicationHarness.Create(engine, declare, name, options, replicationCellM);
        try
        {
            return new FrameHarness(replication);
        }
        catch
        {
            replication.Dispose();
            throw;
        }
    }

    /// <summary>Admits and opens sessions bound to a profile, and gives each one a replica to apply its frames to.</summary>
    /// <param name="count">How many.</param>
    /// <param name="profile">The declared profile.</param>
    /// <returns>The identities.</returns>
    public SessionId[] OpenSessions(int count, string profile)
    {
        var sessions = _replication.OpenSessions(count, profile);
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

    /// <summary>Drops a closed session's decoded replica, so a fixture that churns sessions does not retain one per cycle.</summary>
    /// <param name="session">The session that has ended.</param>
    /// <remarks>A session id is a slot plus a GENERATION, so a recycled slot never reuses its key and nothing here is overwritten by the next occupant.</remarks>
    public void Forget(SessionId session) => _replicas.Remove(session.Value);

    /// <summary>The plan index of a replicated archetype, by wire name.</summary>
    /// <param name="name">The archetype's wire name.</param>
    /// <returns>The index.</returns>
    public int PlanIndex(string name) => _replication.PlanIndex(name);

    /// <summary>
    /// Whether each tick runs the engine's tick fence first, as the runtime does. The fence publishes the structure signal, and every path that rests on
    /// it — stationary retention above all — silently falls back to the walk without it.
    /// </summary>
    public bool RunFence { get; set; }

    /// <summary>
    /// How many worker lists the projection spreads its blocks over, round-robin; the push index's merge chunks then run concurrently, as the stage
    /// runs them. One by default: a single list and a single merge chunk.
    /// </summary>
    public int ProjectionWorkers { get; set; } = 1;

    /// <summary>
    /// Whether the index is built serially in the frame prologue instead of sorted by the projection and merged by the index stage: the path the
    /// collapsed shape and <c>TYPHON_PUSH_PARALLEL_INDEX=0</c> take.
    /// </summary>
    public bool SerialIndex { get; set; }

    /// <summary>Runs one whole tick of the track: blocks, projection, the push index, frames, then the durability gate.</summary>
    /// <param name="tick">The tick number, which must advance.</param>
    /// <param name="workers">Worker-pool width for the frame stage; the projection runs as one chunk.</param>
    /// <remarks>Ticks must be consecutive: a gap is a missed tick to the push log, and a session that seems to have missed one is caught up or reset.</remarks>
    public void RunTick(long tick, int workers = 1)
    {
        Assert.That(Tick == 0 || tick == Tick + 1, Is.True, $"tick {tick} follows tick {Tick}: the harness runs ticks back to back");
        Tick = tick;
        if (RunFence)
        {
            Engine.WriteTickFence(tick);
        }

        Sessions.BeginTick();
        RunProject(tick);
        RunFrames(tick, workers);

        // The publication gate, which the tick driver moves once the unit of work has flushed (SUB-02). Nothing a test reads is sendable before it.
        Assembler.Gate.Publish(tick);
    }

    /// <summary>Advances to <paramref name="tick"/> without running the track: a tick its gate skipped (no session connected, an aborted tick).</summary>
    /// <param name="tick">The tick number, which must advance.</param>
    public void SkipTick(long tick)
    {
        Assert.That(Tick == 0 || tick == Tick + 1, Is.True, $"tick {tick} follows tick {Tick}: the harness runs ticks back to back");
        Tick = tick;
        if (RunFence)
        {
            Engine.WriteTickFence(tick);
        }
    }

    /// <summary>
    /// A tick whose track ran up to the index merge and stopped: the projection and the merge ran, the index was never finished and no frame was built —
    /// what a stage fault between the projection and the frames leaves behind.
    /// </summary>
    /// <param name="tick">The tick number, which must advance.</param>
    public void RunTickWithoutIndex(long tick)
    {
        Assert.That(Tick == 0 || tick == Tick + 1, Is.True, $"tick {tick} follows tick {Tick}: the harness runs ticks back to back");
        Tick = tick;
        if (RunFence)
        {
            Engine.WriteTickFence(tick);
        }

        Sessions.BeginTick();
        RunProject(tick, finish: false);
    }

    /// <summary>Runs a tick's blocks and projection steps but not the frame stage — what an allocation measurement brackets.</summary>
    /// <param name="tick">The tick number.</param>
    /// <param name="workers">Worker-pool width.</param>
    public void RunUpToFrames(long tick, int workers = 1)
    {
        Assert.That(Tick == 0 || tick == Tick + 1, Is.True, $"tick {tick} follows tick {Tick}: the harness runs ticks back to back");
        Tick = tick;
        if (RunFence)
        {
            Engine.WriteTickFence(tick);
        }

        Sessions.BeginTick();
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
            if (DigestFrames)
            {
                FoldDigest(session, bytes);
            }

            replica.Apply(bytes);
            send->CompleteSend(frame.Sequence);
            delivered++;
        }

        return delivered;
    }

    /// <summary>
    /// Whether <see cref="Deliver"/> folds every frame it applies into <see cref="Digest"/>: the session and everything the decoded frame hands a client —
    /// records with their positions, velocities, segment times and epochs, field values, events — but no metric value, so the digest is a function of
    /// what was replicated only.
    /// </summary>
    public bool DigestFrames { get; set; }

    /// <summary>FNV-1a 64 over every frame <see cref="Deliver"/> applied while <see cref="DigestFrames"/> was set.</summary>
    public ulong Digest { get; private set; } = 14695981039346656037UL;

    private void FoldDigest(SessionId session, ReadOnlySpan<byte> frame)
    {
        var sink = new DigestSink(Digest);
        sink.Fold($"s{session.Value}");
        TickReader.Read(frame, CatalogPlan, ref sink);
        Digest = sink.Hash;
        var fill = _fillFrames.GetValueOrDefault(session.Value);
        if (fill.CompleteAt == 0)
        {
            fill.Frames++;
            fill.CompleteAt = (sink.Flags & TickFlags.ViewComplete) != 0 ? fill.Frames : 0;
            _fillFrames[session.Value] = fill;
        }
    }

    private readonly System.Collections.Generic.Dictionary<uint, (int Frames, int CompleteAt)> _fillFrames = [];

    /// <summary>
    /// While <see cref="DigestFrames"/> is set: the frames a session was delivered up to and including its first <c>VIEW_COMPLETE</c>; 0 before.
    /// </summary>
    public int FramesToComplete(SessionId session) => _fillFrames.GetValueOrDefault(session.Value).CompleteAt;

    /// <summary>
    /// Folds everything a decoded frame hands a client — records with their positions, velocities, segment times and epochs, field values, events —
    /// except metric values, which are measurements rather than replication.
    /// </summary>
    private struct DigestSink : ITickSink
    {
        public ulong Hash;
        public TickFlags Flags;

        public DigestSink(ulong seed)
        {
            Hash = seed;
            Flags = TickFlags.None;
        }

        public void Fold(string s)
        {
            foreach (var ch in s)
            {
                Mix(ch);
            }

            Mix('|');
        }

        private void Mix(ulong v) => Hash = (Hash ^ v) * 1099511628211UL;

        private void Mix(scoped ReadOnlySpan<double> values)
        {
            foreach (var v in values)
            {
                Mix((ulong)BitConverter.DoubleToInt64Bits(v));
            }
        }

        private void Mix(scoped ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                Mix(b);
            }
        }

        public void BeginTick(uint tick, TickFlags flags, uint periodUs)
        {
            Flags = flags;
            Mix(1);
            Mix(tick);
            Mix((ulong)flags);
        }

        public void BeginEntities(ArchetypePlan archetype) => Fold("e" + archetype.Name);

        public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
        {
            Mix(2);
            Mix(netId);
            Mix(position);
            Mix(velocity);
            Mix(t0);
            Mix(epoch);
        }

        public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
        {
            Mix(3);
            Mix(netId);
            Mix(position);
            Mix(velocity);
            Mix(t0);
            Mix(epoch);
        }

        public void State(uint netId, byte groupMask)
        {
            Mix(4);
            Mix(netId);
            Mix(groupMask);
        }

        public void Leave(uint netId)
        {
            Mix(5);
            Mix(netId);
        }

        public void Event(MessagePlan type) => Fold("v" + type.Name);

        public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask)
        {
            Mix(6);
            Mix(netId);
            Mix(lastSeq);
            Mix(ownerMask);
        }

        public void Ack(ushort seq, byte reason)
        {
            Mix(7);
            Mix(seq);
            Mix(reason);
        }

        public void Source(ushort requestId, byte status, ushort code)
        {
            Mix(8);
            Mix(requestId);
            Mix(status);
            Mix(code);
        }

        public void BeginAggregate(CatalogGrid grid, bool reset)
        {
            Mix(9);
            Mix((ulong)grid.Idx);
        }

        public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts)
        {
            Mix(cell);
            foreach (var c in counts)
            {
                Mix(c);
            }
        }

        public void Metric(MetricPlan metric, int valueIndex, double value) => Fold("m" + metric.Name);

        public void Debug(byte subType, scoped ReadOnlySpan<byte> payload) => Mix(subType);

        public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload)
        {
            Mix(appTypeId);
            Mix(payload);
        }

        public void UnknownBlock(byte blockType) => Mix(blockType);

        public void EndTick() => Mix(10);

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components) => Mix(components);

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8) => Mix(utf8);

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes) => Mix(bytes);

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
        {
            Mix((ulong)count);
            Mix(components);
        }
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
    public void Dispose() => _replication.Dispose();

    /// <summary>The blocks step and S1, single-threaded: the push set and its blocks, the parked drain, the marks, then each block's projection.</summary>
    private void RunProject(long tick, bool finish = true)
    {
        var push = Subscriptions.Push;
        if (push == null)
        {
            return;
        }

        var states = Subscriptions.ReplicationStates;
        var plans = Subscriptions.Plans;
        var stamp = (uint)tick;

        // The runtime's order (SubscriptionsProjectExecSystem.BlocksStep): the push set and a block for every cluster in it, before the parked drain, so an
        // entity that migrated into a cluster with no block lands in one this tick.
        push.PrepareBlocks(stamp);
        for (var a = 0; a < states.Length; a++)
        {
            states[a].DrainParkedEntries();
        }

        for (var a = 0; a < states.Length; a++)
        {
            states[a].BeginWatchedBlocks(stamp);
        }

        // The projection sorts its events for the parallel index, which the push index stage then merges (SubscriptionsPushIndexExecSystem).
        var lists = Math.Max(1, ProjectionWorkers);
        push.MarkPushed(workers: lists, countInProject: !SerialIndex);

        for (var a = 0; a < states.Length; a++)
        {
            states[a].BeginProjectTick(workers: lists);
        }

        var projected = 0;

        using (EpochGuard.Enter(Engine.EpochManager))
        {
            for (var a = 0; a < plans.Length; a++)
            {
                var state = states[a];
                var clusterState = state.ClusterState;
                if (clusterState == null || state.WatchedBlocks.Count == 0)
                {
                    continue;
                }

                // Both stores, as SubscriptionsProjectExecSystem.ProjectOne reads them: a transient-only archetype has no persistent segment at all.
                var persistent = clusterState.ClusterSegment;
                var transient = clusterState.TransientSegment;
                var persistentAccessor = persistent != null ? persistent.CreateChunkAccessor() : default;
                var transientAccessor = transient != null ? transient.CreateChunkAccessor() : default;
                try
                {
                    for (var i = 0; i < state.WatchedBlocks.Count; i++)
                    {
                        var block = state.WatchedBlocks[i];
                        var chunkId = block->ChunkId;
                        if (chunkId < 0)
                        {
                            continue;
                        }

                        var clusterBase = persistent != null ? persistentAccessor.GetChunkAddress(chunkId) : transientAccessor.GetChunkAddress(chunkId);
                        var transientBase = persistent != null && transient != null ? transientAccessor.GetChunkAddress(chunkId) : null;
                        ProjectionPass.ProjectBlock(plans[a], a, state, projected++ % lists, block, clusterBase, transientBase, stamp);
                    }
                }
                finally
                {
                    persistentAccessor.Dispose();
                    transientAccessor.Dispose();
                }
            }
        }

        // The push index stage: sorted by the projection above, split into key ranges, then merged one range at a time.
        for (var w = 0; w < lists; w++)
        {
            push.CountWorker(w);
        }

        var ranges = push.BeginParallelIndex();
        if (ranges > 1)
        {
            System.Threading.Tasks.Parallel.For(0, ranges, push.PlaceWorker);
        }
        else
        {
            for (var r = 0; r < ranges; r++)
            {
                push.PlaceWorker(r);
            }
        }

        // The far-flush stage (SubscriptionsPushFarExecSystem): the index's serial tail, then the fold in two chunks so a chunk boundary is crossed — and,
        // as there, only with a session open.
        if (!finish)
        {
            return;
        }

        push.FinishIndex();
        var chunks = Sessions.OpenCount > 0 ? push.BeginFarFold(2) : 0;
        for (var c = 0; c < chunks; c++)
        {
            push.FoldFarChunk(c);
        }
    }

    private void RunFrames(long tick, int workers)
    {
        var chunks = Assembler.BeginTick(tick, workers);
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
