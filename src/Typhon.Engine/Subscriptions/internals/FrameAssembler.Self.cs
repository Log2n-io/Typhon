using System;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>What one session's frame owes it in <c>SELF</c> and <c>ACKS</c> this tick (design/Subscriptions/11 § 2), decided before it is sized.</summary>
internal struct SelfDue
{
    /// <summary>Whether the frame carries a <c>SELF</c> block.</summary>
    public bool Write;

    /// <summary>Whether that block names no entity (W17′): netId 0, an acknowledgement or the news that the controlled entity is gone.</summary>
    public bool None;

    /// <summary>The session's controlled entity as the frame read it.</summary>
    public EntityId Controlled;

    /// <summary>The controlled entity's plan index, block and netId, when it was located.</summary>
    public int Archetype;

    public nint Block;
    public int Slot;
    public uint NetId;

    /// <summary>The owner groups carried.</summary>
    public byte Mask;

    /// <summary>The session's highest drained command sequence, when it has one.</summary>
    public ushort LastSeq;

    public bool HasSeq;

    /// <summary>Acknowledgements collected into the worker's scratch.</summary>
    public int Acks;

    /// <summary>Acknowledgements that did not fit the frame's share; counted if the frame is published.</summary>
    public int AcksOverflowed;

    /// <summary>Whether the session's window reached past the history; counted if the frame is published.</summary>
    public bool AckWindowLost;
}

internal sealed unsafe partial class FrameAssembler
{
    /// <summary>The most acknowledgements one frame carries; past it they are counted, not sent (11 § 2.3).</summary>
    internal const int MaxAcksPerFrame = 256;

    /// <summary>The owner routing, when an archetype declares owner fields (11 § 2.2); <see langword="null"/> otherwise.</summary>
    internal SelfTracker Self;

    private readonly AckHistory _ackHistory;

    /// <summary><c>SELF</c> blocks published, over the assembler's life.</summary>
    public long SelfBlocks;

    /// <summary>Acknowledgement records published.</summary>
    public long AcksWritten;

    /// <summary>Acknowledgements a published frame had no room for.</summary>
    public long AcksOverflowed;

    /// <summary>Published frames whose acknowledgement window reached past the history, where rejections may have been lost.</summary>
    public long AckWindowsLost;

    /// <summary>The frame prologue's share: this tick's rejections into the history. Serial.</summary>
    private void RecordAcks()
    {
        var buffers = Ingress?.Buffers;
        _ackHistory.Record((uint)_tick, buffers != null ? buffers.Acks.AsSpan() : []);
    }

    /// <summary>
    /// Decides what <paramref name="session"/>'s frame owes it in <c>SELF</c> and <c>ACKS</c>. Reads, commits nothing: <see cref="CommitSelf"/> does, once the
    /// frame is published (SUB-03).
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="state">Its frame state.</param>
    /// <param name="reset">Whether the frame is a <c>RESET</c>, which clears the client's owner state and its <c>lastSeq</c>.</param>
    /// <param name="scratch">The worker's scratch, whose acknowledgement buffer is filled.</param>
    /// <param name="locator">A reader for the controlled entity's location.</param>
    /// <param name="due">What is owed.</param>
    /// <remarks>
    /// The controlled entity is located only when owner groups are due — a Control change, a RESET, or a routed change (the pending mask, which a Control
    /// change sets whole, <see cref="SelfTracker.Refresh"/>). An acknowledgement alone names the netId the last SELF named, from the frame state.
    /// </remarks>
    private void PrepareSelf(SessionId session, SessionFrameState state, bool reset, FrameWorkerScratch scratch, ref BoundViewpoint locator, out SelfDue due)
    {
        due = default;
        due.Archetype = -1;
        var row = Ingress?.RowOf(session);
        if (row is { HasLastSeq: true })
        {
            due.LastSeq = row.LastSeq;
            due.HasSeq = true;
        }

        _ackHistory.Collect(session, state.AckTick, (uint)_tick, scratch.Acks, out due.Acks, out due.AcksOverflowed, out due.AckWindowLost);

        var seqDue = due.HasSeq && (reset || !state.SentSeqValid || state.SentSeq != due.LastSeq);
        var controlled = _sessions.ControlledOf(session);
        due.Controlled = controlled;
        if (controlled.IsNull)
        {
            // No entity: an acknowledgement when lastSeq moved, and once, the news that the entity a SELF named is no longer this session's (W17′).
            due.Write = due.None = seqDue || state.SelfNetId != 0;
            return;
        }

        // SUB-11: every owner group after a Control change and on a RESET (the client cleared its owner state); otherwise the groups routed to it.
        var force = reset || controlled != state.SelfEntity;
        var pending = Self?.Pending(session.Slot) ?? 0;
        if (!force && pending == 0)
        {
            if (seqDue)
            {
                // An acknowledgement: the netId the last SELF named, or none.
                due.Write = true;
                due.None = state.SelfNetId == 0;
                due.Archetype = state.SelfArchetype;
                due.NetId = state.SelfNetId;
            }

            return;
        }

        if (!locator.TryLocate(controlled, out var clusters, out var chunk, out var slot)
            || !Push.TryReplicaAt(clusters, chunk, slot, controlled, out var archetype, out var block, out var netId))
        {
            // Not replicated (yet, or any more): the client hears what is settled and that it holds no entity's owner state. The frame state takes the
            // entity anyway, so this is not retried every frame: when it is projected, its first projection routes every owner group (the pending mask).
            due.Write = due.None = seqDue || state.SelfNetId != 0;
            return;
        }

        var plan = _encodePlans[archetype];
        due.Write = true;
        due.Archetype = archetype;
        due.Block = block;
        due.Slot = slot;
        due.NetId = netId;
        due.Mask = (byte)((force ? plan.OwnerAllMask : pending) & plan.OwnerAllMask);
    }

    /// <summary>The bytes <paramref name="due"/> can need.</summary>
    private int SelfBound(in SelfDue due) =>
        (due.Write ? (due.None ? 16 : _encodePlans[due.Archetype].MaxSelfBytes) : 0) + (due.Acks > 0 ? EntitiesEncoder.MaxAcksBytes(due.Acks) : 0);

    /// <summary>Writes the <c>SELF</c> and <c>ACKS</c> blocks <paramref name="due"/> owes.</summary>
    private void WriteSelf(ref WireWriter w, in SelfDue due, FrameWorkerScratch scratch)
    {
        if (due.Write)
        {
            var lastSeq = due.HasSeq ? due.LastSeq : (ushort)0;
            if (due.None)
            {
                TickWriter.WriteSelfNone(ref w, lastSeq);
            }
            else
            {
                var plan = _encodePlans[due.Archetype];
                EntitiesEncoder.WriteSelf(ref w, plan, due.NetId, lastSeq, due.Mask, due.Mask != 0 ? plan.Owner(due.Block, due.Slot) : null);
            }
        }

        if (due.Acks > 0)
        {
            EntitiesEncoder.WriteAcks(ref w, scratch.Acks.AsSpan(0, due.Acks));
        }
    }

    /// <summary>
    /// The frame that carried <paramref name="due"/> — or had nothing to carry — was published: what the client now holds of its owner state, its
    /// <c>lastSeq</c> and its acknowledgements advances with it (SUB-03's clause).
    /// </summary>
    private void CommitSelf(SessionId session, SessionFrameState state, in SelfDue due, int profile, ref FrameCounters counters)
    {
        state.AckTick = (uint)_tick;
        state.CommittedProfile = profile;
        Self?.Clear(session.Slot);
        counters.AcksWritten += due.Acks;
        counters.AcksOverflowed += due.AcksOverflowed;
        counters.AckWindowsLost += due.AckWindowLost ? 1 : 0;
        state.SelfEntity = due.Controlled;
        if (!due.Write)
        {
            return;
        }

        counters.SelfBlocks++;
        state.SelfNetId = due.None ? 0u : due.NetId;
        state.SelfArchetype = due.None ? -1 : due.Archetype;
        if (due.HasSeq)
        {
            state.SentSeq = due.LastSeq;
            state.SentSeqValid = true;
        }
    }
}
