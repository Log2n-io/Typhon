using System;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>
/// Receives a frame's events as they are decoded: the type, then its fields through the <see cref="IFieldSink"/> members. Called while the frame is being
/// applied, before its leaves, so an event can still resolve an entity that leaves in the same frame.
/// </summary>
public interface IEventHandler : IFieldSink
{
    /// <summary>An event begins.</summary>
    /// <param name="type">The event type.</param>
    void Event(MessagePlan type);
}

/// <summary>
/// Decodes <c>TICK</c> messages straight into a <see cref="WorldStore"/>: one copy, from the wire into the store's columns (05-sdks § 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Apply order (03-wire-protocol § 5), whatever order the blocks travel in.</b> A first pass applies every block but <c>EVENTS</c> — enters, segments
/// and state records as they are decoded, then <c>SOURCES</c>, <c>SELF</c>, <c>ACKS</c> and <c>AGG</c> as they come — and collects the leaves; a second
/// pass reads the <c>EVENTS</c> blocks alone; the leaves apply last. An event therefore sees every enter and update of its frame, and every entity that
/// leaves in it (and this frame's aggregates, which § 5 orders after the leaves but nothing can observe in between). A <c>RESET</c> frame clears the store
/// before anything in it applies.
/// </para>
/// <para>
/// <b>netId reuse (03 § 10).</b> A frame never carries an enter and a leave for one netId, so an enter for a live netId replaces its holder and counts as
/// an anomaly, and a leave applies to whatever holds its netId once the frame's enters are in — provided the holder belongs to the leave's archetype, as a
/// segment or a state record must; otherwise the leave is an anomaly and changes nothing.
/// </para>
/// <para>
/// <b>No allocation in steady state.</b> The applier is the decoder's sink, so decoded numbers land in the store's arrays without an intermediate value; the
/// only allocations are text and bytes fields that change, and store growth.
/// </para>
/// </remarks>
public sealed class FrameApplier
{
    private enum Target
    {
        None,
        Entity,
        Self,
        Event,
    }

    private readonly WorldStore _store;
    private readonly IEventHandler _events;
    private uint[] _leaves = new uint[64];
    private int[] _leaveArchetypes = new int[64];
    private int _leaveCount;
    private Target _target;
    private ArchetypeStore _archetype;
    private AggregateGrid _aggregate;
    private int _slot;

    /// <summary>Creates an applier.</summary>
    /// <param name="store">The store frames are applied to.</param>
    /// <param name="events">Receives decoded events, or <see langword="null"/> to drop them.</param>
    public FrameApplier(WorldStore store, IEventHandler events = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _events = events;
    }

    /// <summary>Decodes and applies one <c>TICK</c> message.</summary>
    /// <param name="message">The whole message, type byte included.</param>
    /// <exception cref="WireFormatException">The message is malformed; the store may hold part of the frame and the session must be closed.</exception>
    /// <remarks>An exception thrown by the event handler propagates the same way: the frame is partly applied, and the session must be closed.</remarks>
    public void Apply(ReadOnlySpan<byte> message)
    {
        var pass = new Pass(this, events: false);
        TickReader.Read(message, _store.Plan, ref pass, TickBlocks.AllButEvents);
        pass = new Pass(this, events: true);
        TickReader.Read(message, _store.Plan, ref pass, TickBlocks.Events);
        ApplyLeaves();
    }

    private void BeginTick(uint tick, TickFlags flags, uint periodUs)
    {
        _store.BeginFrame();
        if ((flags & TickFlags.Reset) != 0)
        {
            _store.Reset();
        }

        _store.Tick = tick;
        _store.Flags = flags;
        _store.PeriodUs = periodUs;
        _leaveCount = 0;
        _target = Target.None;
    }

    private void BeginEntities(ArchetypePlan archetype)
    {
        _archetype = _store.Archetypes[archetype.Idx];
        _target = Target.None;
    }

    private void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
        if (_store.TryLocate(netId, out var oldArchetype, out var oldSlot))
        {
            // A frame never carries a netId's leave and its reuse (03 § 10), so this holder was never released: the enter replaces it.
            _store.Archetypes[oldArchetype].Release(oldSlot, immediate: false);
            _store.Unmap(netId);
            _store.Anomalies++;
        }

        if (netId == 0 || netId > _store.MaxNetId)
        {
            _store.Anomalies++;
            _target = Target.None;
            return;
        }

        var slot = _archetype.Allocate(netId);
        _store.TryMap(netId, _archetype.Plan.Idx, slot);

        if (_archetype.Dims > 0)
        {
            _archetype.WriteSegment(slot, position, velocity, t0, epoch);
        }

        _slot = slot;
        _target = Target.Entity;
    }

    private void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
        _target = Target.None;
        if (!_store.TryLocate(netId, out var archetype, out var slot) || archetype != _archetype.Plan.Idx)
        {
            _store.Anomalies++;
            return;
        }

        _archetype.WriteSegment(slot, position, velocity, t0, epoch);
        _archetype.MarkUpdated(slot, 0, moved: true);
    }

    private void State(uint netId, byte groupMask)
    {
        if (!_store.TryLocate(netId, out var archetype, out var slot) || archetype != _archetype.Plan.Idx)
        {
            _store.Anomalies++;
            _target = Target.None;
            return;
        }

        _archetype.MarkUpdated(slot, groupMask, moved: false);
        _slot = slot;
        _target = Target.Entity;
    }

    private void Leave(uint netId)
    {
        _target = Target.None;
        if (_leaveCount == _leaves.Length)
        {
            Array.Resize(ref _leaves, _leaves.Length * 2);
            Array.Resize(ref _leaveArchetypes, _leaves.Length);
        }

        _leaves[_leaveCount] = netId;
        _leaveArchetypes[_leaveCount++] = _archetype.Plan.Idx;
    }

    private void Event(MessagePlan type)
    {
        _target = Target.Event;
        _events?.Event(type);
    }

    private void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask)
    {
        _store.Self.Receive(archetype, netId, lastSeq, ownerMask);
        _target = Target.Self;
    }

    private void Ack(ushort seq, byte reason) => _store.Acks.Add((seq, reason));

    private void Source(ushort requestId, byte status, ushort code) => _store.Sources.Add((requestId, status, code));

    private void BeginAggregate(CatalogGrid grid, bool reset)
    {
        _target = Target.None;
        if (reset)
        {
            _store.Aggregates[grid.Idx].Clear();
        }

        _aggregate = _store.Aggregates[grid.Idx];
    }

    private void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts)
    {
        if (cell >= (uint)_aggregate.CellCount)
        {
            _store.Anomalies++;
            return;
        }

        _aggregate.Set(cell, counts);
    }

    private void Metric(MetricPlan metric, int valueIndex, double value)
    {
        var list = metric.Session ? _store.Plan.SessionMetrics : _store.Plan.ServerMetrics;
        var values = metric.Session ? _store.SessionMetricValues : _store.ServerMetricValues;
        values[Array.IndexOf(list, metric)][valueIndex] = value;
    }

    private void Debug(byte subType, scoped ReadOnlySpan<byte> payload) => _target = Target.None;

    private void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload) => _target = Target.None;

    private void UnknownBlock(byte blockType) => _target = Target.None;

    private void ApplyLeaves()
    {
        for (var i = 0; i < _leaveCount; i++)
        {
            var netId = _leaves[i];
            if (!_store.TryLocate(netId, out var archetype, out var slot) || archetype != _leaveArchetypes[i])
            {
                _store.Anomalies++;
                continue;
            }

            _store.Archetypes[archetype].Release(slot, immediate: false);
            _store.Unmap(netId);
        }

        _target = Target.None;
        _store.Frames++;
    }

    private void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
    {
        switch (_target)
        {
            case Target.Entity:
                components.CopyTo(_archetype.Numbers[field.Ordinal].AsSpan(_slot * field.Components, field.Components));
                break;
            case Target.Self:
                components.CopyTo(_store.Self.Numbers[field.Ordinal] ??= new double[field.Components]);
                break;
            case Target.Event:
                _events?.Number(field, components);
                break;
        }
    }

    private void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
    {
        switch (_target)
        {
            case Target.Entity:
                _archetype.Texts[field.Ordinal][_slot] = Utf8(utf8);
                break;
            case Target.Self:
                _store.Self.Texts[field.Ordinal] = Utf8(utf8);
                break;
            case Target.Event:
                _events?.Text(field, utf8);
                break;
        }
    }

    private void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
    {
        switch (_target)
        {
            case Target.Entity:
                _archetype.BytesColumns[field.Ordinal][_slot] = Copy(bytes, _archetype.BytesColumns[field.Ordinal][_slot]);
                break;
            case Target.Self:
                _store.Self.Bytes[field.Ordinal] = Copy(bytes, _store.Self.Bytes[field.Ordinal]);
                break;
            case Target.Event:
                _events?.Bytes(field, bytes);
                break;
        }
    }

    private void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
    {
        // Lists are event and command fields only (the catalog validator refuses them on archetypes).
        if (_target == Target.Event)
        {
            _events?.List(field, count, components);
        }
    }

    /// <summary>
    /// The sink of one read: every block but <c>EVENTS</c>, or <c>EVENTS</c> alone. A struct, so the reader's calls stay direct and nothing allocates; and
    /// private, so no caller can read a frame into the store without the second pass and the leaves.
    /// </summary>
    private readonly struct Pass : ITickSink
    {
        private readonly FrameApplier _applier;
        private readonly bool _events;

        public Pass(FrameApplier applier, bool events)
        {
            _applier = applier;
            _events = events;
        }

        public void BeginTick(uint tick, TickFlags flags, uint periodUs)
        {
            if (_events)
            {
                _applier._target = Target.None;
            }
            else
            {
                _applier.BeginTick(tick, flags, periodUs);
            }
        }

        public void BeginEntities(ArchetypePlan archetype) => _applier.BeginEntities(archetype);

        public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) =>
            _applier.Enter(netId, position, velocity, t0, epoch);

        public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) =>
            _applier.Segment(netId, position, velocity, t0, epoch);

        public void State(uint netId, byte groupMask) => _applier.State(netId, groupMask);

        public void Leave(uint netId) => _applier.Leave(netId);

        public void Event(MessagePlan type) => _applier.Event(type);

        public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask) => _applier.Self(archetype, netId, lastSeq, ownerMask);

        public void Ack(ushort seq, byte reason) => _applier.Ack(seq, reason);

        public void Source(ushort requestId, byte status, ushort code) => _applier.Source(requestId, status, code);

        public void BeginAggregate(CatalogGrid grid, bool reset) => _applier.BeginAggregate(grid, reset);

        public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts) => _applier.AggregateCell(cell, counts);

        public void Metric(MetricPlan metric, int valueIndex, double value) => _applier.Metric(metric, valueIndex, value);

        public void Debug(byte subType, scoped ReadOnlySpan<byte> payload) => _applier.Debug(subType, payload);

        public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload) => _applier.Ext(appTypeId, payload);

        public void UnknownBlock(byte blockType) => _applier.UnknownBlock(blockType);

        public void EndTick() => _applier._target = Target.None;

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components) => _applier.Number(field, components);

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8) => _applier.Text(field, utf8);

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes) => _applier.Bytes(field, bytes);

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components) => _applier.List(field, count, components);
    }

    // A text field that changes allocates its new string: the one allocation a decode makes, and only for a field that travelled. An empty text reuses
    // the interned empty string.
    private static string Utf8(ReadOnlySpan<byte> utf8) =>
        utf8.IsEmpty ? string.Empty : System.Text.Encoding.UTF8.GetString(utf8);

    private static byte[] Copy(ReadOnlySpan<byte> bytes, byte[] previous)
    {
        if (previous != null && previous.AsSpan().SequenceEqual(bytes))
        {
            return previous;
        }

        return bytes.ToArray();
    }
}
