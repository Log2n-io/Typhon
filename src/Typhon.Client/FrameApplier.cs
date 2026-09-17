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
/// <b>Apply order (03-wire-protocol § 5).</b> Enters, segments and state records apply as they are decoded; <c>SOURCES</c>, <c>SELF</c>, <c>ACKS</c>,
/// <c>EVENTS</c> and <c>AGG</c> as they come; leaves are collected and applied when the frame ends, so an event naming an entity that leaves in the same
/// frame still finds it. A <c>RESET</c> frame clears the store before anything in it applies.
/// </para>
/// <para>
/// <b>No allocation in steady state.</b> The applier is the decoder's sink, so decoded numbers land in the store's arrays without an intermediate value; the
/// only allocations are text and bytes fields that change, and store growth.
/// </para>
/// </remarks>
public sealed class FrameApplier : ITickSink
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
    private int _leaveCount;
    private uint[] _enteredThisFrame = new uint[64];
    private int _enteredThisFrameCount;
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
    public void Apply(ReadOnlySpan<byte> message)
    {
        var self = this;
        TickReader.Read(message, _store.Plan, ref self);
    }

    /// <inheritdoc />
    public void BeginTick(uint tick, TickFlags flags, uint periodUs)
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
        _enteredThisFrameCount = 0;
        _target = Target.None;
    }

    /// <inheritdoc />
    public void BeginEntities(ArchetypePlan archetype)
    {
        _archetype = _store.Archetypes[archetype.Idx];
        _target = Target.None;
    }

    /// <inheritdoc />
    public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
        if (_store.TryLocate(netId, out var oldArchetype, out var oldSlot))
        {
            // The server reused a live identity: the enter replaces the old entity, and a leave for it later in this frame belongs to the replaced one.
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

        Remember(ref _enteredThisFrame, ref _enteredThisFrameCount, netId);
        _slot = slot;
        _target = Target.Entity;
    }

    /// <inheritdoc />
    public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
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

    /// <inheritdoc />
    public void State(uint netId, byte groupMask)
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

    /// <inheritdoc />
    public void Leave(uint netId)
    {
        _target = Target.None;
        Remember(ref _leaves, ref _leaveCount, netId);
    }

    /// <inheritdoc />
    public void Event(MessagePlan type)
    {
        _target = Target.Event;
        _events?.Event(type);
    }

    /// <inheritdoc />
    public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask)
    {
        _store.Self.Receive(archetype, netId, lastSeq, ownerMask);
        _target = Target.Self;
    }

    /// <inheritdoc />
    public void Ack(ushort seq, byte reason) => _store.Acks.Add((seq, reason));

    /// <inheritdoc />
    public void Source(ushort requestId, byte status, ushort code) => _store.Sources.Add((requestId, status, code));

    /// <inheritdoc />
    public void BeginAggregate(CatalogGrid grid, bool reset)
    {
        _target = Target.None;
        if (reset)
        {
            _store.Aggregates[grid.Idx].Clear();
        }

        _aggregate = _store.Aggregates[grid.Idx];
    }

    /// <inheritdoc />
    public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts)
    {
        if (cell >= (uint)_aggregate.CellCount)
        {
            _store.Anomalies++;
            return;
        }

        _aggregate.Set(cell, counts);
    }

    /// <inheritdoc />
    public void Metric(MetricPlan metric, int valueIndex, double value)
    {
        var list = metric.Session ? _store.Plan.SessionMetrics : _store.Plan.ServerMetrics;
        var values = metric.Session ? _store.SessionMetricValues : _store.ServerMetricValues;
        values[Array.IndexOf(list, metric)][valueIndex] = value;
    }

    /// <inheritdoc />
    public void Debug(byte subType, scoped ReadOnlySpan<byte> payload) => _target = Target.None;

    /// <inheritdoc />
    public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload) => _target = Target.None;

    /// <inheritdoc />
    public void UnknownBlock(byte blockType) => _target = Target.None;

    /// <inheritdoc />
    public void EndTick()
    {
        for (var i = 0; i < _leaveCount; i++)
        {
            var netId = _leaves[i];
            if (!_store.TryLocate(netId, out var archetype, out var slot))
            {
                // Replaced by an enter in this same frame, or never held.
                if (Array.IndexOf(_enteredThisFrame, netId, 0, _enteredThisFrameCount) < 0)
                {
                    _store.Anomalies++;
                }

                continue;
            }

            if (Array.IndexOf(_enteredThisFrame, netId, 0, _enteredThisFrameCount) >= 0)
            {
                // The netId's live holder entered this frame; the leave belongs to the identity it replaced.
                continue;
            }

            _store.Archetypes[archetype].Release(slot, immediate: false);
            _store.Unmap(netId);
        }

        _target = Target.None;
        _store.Frames++;
    }

    /// <inheritdoc />
    public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
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

    /// <inheritdoc />
    public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
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

    /// <inheritdoc />
    public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
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

    /// <inheritdoc />
    public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
    {
        // Lists are event and command fields only (the catalog validator refuses them on archetypes).
        if (_target == Target.Event)
        {
            _events?.List(field, count, components);
        }
    }

    private static void Remember(ref uint[] buffer, ref int count, uint netId)
    {
        if (count == buffer.Length)
        {
            Array.Resize(ref buffer, buffer.Length * 2);
        }

        buffer[count++] = netId;
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
