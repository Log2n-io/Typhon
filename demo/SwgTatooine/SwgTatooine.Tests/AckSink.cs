using System;
using System.Collections.Generic;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A tick sink that keeps only the <c>ACKS</c> reasons a session was sent.
/// </summary>
/// <remarks>
/// <b>Shared, because a reason code is the only part of a refusal a client can act on</b> and more than one fixture needs
/// to read it. Asserting that a counter went up says the server refused something; asserting the reason says it refused
/// it for the reason the client's own table will render, which is the half that can be wrong without any counter
/// noticing. <c>ViewRadiusChecks</c> keeps its own richer sink, which also tracks what the session holds.
/// </remarks>
internal struct AckSink : ITickSink
{
    /// <summary>Every reason, in arrival order.</summary>
    /// <remarks>
    /// Created on first use rather than by the caller: a <c>default(AckSink)</c> is a legal thing for a fixture to
    /// write, and it used to throw a <see cref="NullReferenceException"/> inside the first frame it read — a failure
    /// several layers away from the line that left the list null.
    /// </remarks>
    public List<byte> Reasons;

    public void Ack(ushort seq, byte reason) => (Reasons ??= []).Add(reason);

    public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
    {
    }

    public void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
    {
    }

    public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
    {
    }

    public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
    {
    }

    public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
    {
    }

    public void BeginTick(uint tick, TickFlags flags, uint periodUs)
    {
    }

    public void Realm(RealmFrame frame)
    {
    }

    public void BeginEntities(ArchetypePlan archetype)
    {
    }

    public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
    }

    public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
    }

    public void State(uint netId, byte groupMask)
    {
    }

    public void Leave(uint netId)
    {
    }

    public void Event(MessagePlan type)
    {
    }

    public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask)
    {
    }

    public void Source(ushort requestId, byte status, ushort code)
    {
    }

    public void BeginAggregate(CatalogGrid grid, bool reset)
    {
    }

    public void Aggregate(int channel, int tile, double value)
    {
    }

    public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts)
    {
    }

    public void Metric(MetricPlan metric, int instance, double value)
    {
    }

    public void Debug(byte kind, scoped ReadOnlySpan<byte> payload)
    {
    }

    public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload)
    {
    }

    public void UnknownBlock(byte blockType)
    {
    }

    public void EndTick()
    {
    }
}
