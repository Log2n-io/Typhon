using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// One of a session's <see cref="SessionSendState.K"/> frame slots: which block holds the frame, how much of it the session used, and the tick it describes.
/// </summary>
/// <remarks>
/// Written by the producer, read by the send loop, and ordered by nothing of its own: every field here is published by the release of
/// <c>SessionSendState.ReadySequence</c> and is stale for any reader that has not acquired it. That is the whole point of the counter — a slot needs no lock
/// and no seqlock because exactly one release/acquire pair governs when its contents become readable.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = SessionSendState.SlotBytes)]
internal struct FrameSlot
{
    /// <summary>First byte of the frame, or zero for an empty slot. Native memory from <see cref="FramePool"/>.</summary>
    public nint Bytes;

    /// <summary>The block's size class, carried so the block can be returned to the pool without a side table.</summary>
    public int Capacity;

    /// <summary>How many bytes the frame actually occupies. Never above <see cref="Capacity"/>.</summary>
    public int Length;

    /// <summary>The tick this frame describes. The send loop compares it against the published committed tick.</summary>
    public long Tick;
}

/// <summary>
/// What the send loop is handed for one frame: the bytes, their length, the tick they describe and the sequence to complete.
/// </summary>
internal readonly unsafe struct FrameView
{
    internal FrameView(byte* bytes, int length, long tick, long sequence)
    {
        Bytes = bytes;
        Length = length;
        Tick = tick;
        Sequence = sequence;
    }

    /// <summary>First byte of the frame. Native memory — wrap it with <see cref="NativeFrameMemoryManager"/> to reach a link.</summary>
    public byte* Bytes { get; }

    /// <summary>How many bytes to send.</summary>
    public int Length { get; }

    /// <summary>The tick this frame describes.</summary>
    public long Tick { get; }

    /// <summary>The sequence to pass to <c>SessionSendState.CompleteSend</c> once the link no longer needs the bytes.</summary>
    public long Sequence { get; }

    /// <summary>True when this names a real frame. A default instance does not.</summary>
    public bool IsValid => Bytes != null;
}

/// <summary>
/// The durability gate: the last tick whose unit of work has flushed, published once per tick by the tick driver and read by every send loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a type and not a field on the runtime.</b> It is one word with exactly one publication protocol — a release after the flush, an acquire before a
/// send — and SUB-04 names that protocol. Giving it a name means the release and the acquire are in one place and cannot drift apart when
/// <c>TyphonRuntime</c>'s publish block is edited for an unrelated reason.
/// </para>
/// <para>
/// <b>Padded.</b> Every send pump on every core acquires this word; a field sharing its line with anything the tick writes would bounce that line on every
/// write of the neighbour.
/// </para>
/// </remarks>
internal sealed class FramePublicationGate
{
    private CacheLinePaddedLong _committedTick;

    /// <summary>The newest tick whose frames may be sent. Zero until the first flush.</summary>
    /// <remarks>
    /// Acquire. Every load a send loop makes of a frame's bytes is ordered after this one, which is what makes "gated on the flush" a memory-ordering fact
    /// rather than a comment.
    /// </remarks>
    public long CommittedTick => Volatile.Read(ref _committedTick.Value);

    /// <summary>
    /// Publishes a tick as durable. Called by the tick driver after the unit of work has flushed, and never before.
    /// </summary>
    /// <param name="tick">The tick that has flushed.</param>
    /// <remarks>
    /// Release. Publishing a tick that has not flushed is the violation SUB-02 describes: a frame would tell a session a story the WAL does not carry.
    /// </remarks>
    public void Publish(long tick)
    {
        Debug.Assert(tick >= Volatile.Read(ref _committedTick.Value), "the committed tick never moves backwards");
        Volatile.Write(ref _committedTick.Value, tick);
    }
}

/// <summary>
/// One session's hand-off to the transport: four counters and <see cref="K"/> frame slots, laid out so a producer and a send pump never share a cache line.
/// </summary>
/// <remarks>
/// <para>
/// <b>The protocol, and the pair each counter forms</b> ([02-execution § 6]). Three counters live here; the fourth, the committed tick, is process-wide and
/// lives in <see cref="FramePublicationGate"/>.
/// </para>
/// <list type="table">
/// <item>
/// <term><see cref="NextSequence"/></term>
/// <description>Frames begun. Written and read by the producer alone, so plain loads and stores — it forms no pair, and one would be noise.</description>
/// </item>
/// <item>
/// <term><see cref="ReadySequence"/></term>
/// <description>Frames published. <see cref="PublishFrame"/> writes the frame's bytes and its slot, then releases this counter; <see cref="TryClaimFrame"/>
/// acquires it before it reads either. <b>Release → acquire: the bytes are visible before the sequence is.</b></description>
/// </item>
/// <item>
/// <term><see cref="SentSequence"/></term>
/// <description>Frames the link no longer needs. <see cref="CompleteSend"/> releases it once the send has completed; <see cref="TryBeginFrame"/> acquires it
/// before it hands a slot back for reuse. <b>Release → acquire: the link's reads happen before the producer's overwrite.</b></description>
/// </item>
/// <item>
/// <term><see cref="AckedTick"/></term>
/// <description>The newest tick the client reported applied, written by the link thread on every <c>PING</c> and read by the producer's lag skip. One number
/// written atomically, which is what puts it on SUB-05's allow-list.</description>
/// </item>
/// </list>
/// <para>
/// <b>Counting convention.</b> Each counter is a <i>count of frames past that stage</i>, not the index of the last one — so all three start at zero, "nothing
/// yet" needs no sentinel, and the three inequalities that define a correct state are plain arithmetic:
/// <c>SentSequence ≤ ReadySequence ≤ NextSequence ≤ SentSequence + K</c>, with <c>NextSequence − ReadySequence ∈ {0, 1}</c> because at most one frame
/// is under construction. [02-execution § 6] writes the release as <c>Volatile.Write(ReadySeq, seq)</c> with <c>seq</c> as the index; that is the same
/// protocol one apart, and the deviation is the convention only.
/// </para>
/// <para>
/// <b>K = 2, and skip-not-queue.</b> One frame in flight, one ready. When <c>NextSequence − SentSequence ≥ K</c> the producer does not encode this session
/// this tick and nothing moves: <see cref="TryBeginFrame"/> returns <see langword="false"/> and <see cref="SkipRun"/> advances. Queueing instead would grow
/// a backlog a slow client can never drain, and would be wasted work besides — records are absolute (SUB-03), so the next frame this session does receive
/// carries everything the skipped ones would have.
/// </para>
/// <para>
/// <b>Sequences, not tick parity.</b> Indexing the slots by <c>tick &amp; 1</c> aliases an unsent frame the moment a tick is skipped; <c>sequence % K</c>
/// never can, because the sequence only advances when a frame is actually produced.
/// </para>
/// <para>
/// <b>Why the padding is the design and not a tuning knob.</b> The producer writes <see cref="NextSequence"/> and <see cref="ReadySequence"/> every frame;
/// the send side writes <see cref="SentSequence"/> and <see cref="AckedTick"/> on every completion and every ping. Sharing one line would put those two
/// streams on the same cache line and make every completion invalidate the producer's line and vice versa, on a structure touched once per session per tick.
/// So the two writers get a line each, and the slots — written by the producer, read by the send side — get a third.
/// </para>
/// <para>
/// <b>Lives in native memory.</b> The struct is laid out explicitly and is meant to sit in an allocation the session table owns, addressed through a
/// <c>SessionSendState*</c>. Nothing here holds a managed reference, which is what lets the frame's bytes reach a socket with no pointer over GC memory
/// anywhere on the path.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = Bytes)]
internal unsafe struct SessionSendState
{
    /// <summary>Frames a session may have outstanding: one on the link, one ready behind it.</summary>
    internal const int K = 2;

    /// <summary>Size of one <see cref="FrameSlot"/>.</summary>
    internal const int SlotBytes = 24;

    /// <summary>Where the send side's line starts. Its own cache line, away from everything the producer writes.</summary>
    internal const int SendLineOffset = 64;

    /// <summary>Where the slots start. A third line: written by the producer, read by the send side.</summary>
    internal const int SlotsOffset = 128;

    /// <summary>Three cache lines.</summary>
    internal const int Bytes = 192;

    // --- the producer's line: written by the S2b chunk that owns this session, read by nobody else on the critical path ---

    [FieldOffset(0)] private long _nextSeq;
    [FieldOffset(8)] private long _readySeq;
    [FieldOffset(16)] private long _producedTick;
    [FieldOffset(24)] private int _skipRun;

    // --- the send side's line: written by the pump and the link thread, read by the producer ---

    [FieldOffset(SendLineOffset)] private long _sentSeq;
    [FieldOffset(SendLineOffset + 8)] private long _ackedTick;
    [FieldOffset(SendLineOffset + 16)] private long _pingTick;
    [FieldOffset(SendLineOffset + 24)] private int _caps;

    // --- the slots ---

    [FieldOffset(SlotsOffset)] private FrameSlot _slot0;
    [FieldOffset(SlotsOffset + SlotBytes)] private FrameSlot _slot1;

    /// <summary>Frames begun. Producer-private.</summary>
    public long NextSequence => Volatile.Read(ref _nextSeq);

    /// <summary>Frames published and visible to the send side.</summary>
    public long ReadySequence => Volatile.Read(ref _readySeq);

    /// <summary>Frames the link no longer needs.</summary>
    public long SentSequence => Volatile.Read(ref _sentSeq);

    /// <summary>The tick of the last frame published for this session.</summary>
    public long ProducedTick => Volatile.Read(ref _producedTick);

    /// <summary>Consecutive ticks this session has been skipped. Reset by a published frame; the skip policy reads it.</summary>
    public int SkipRun => Volatile.Read(ref _skipRun);

    /// <summary>Frames begun and not yet sent. Never above <see cref="K"/>.</summary>
    public long FramesInFlight => Volatile.Read(ref _nextSeq) - Volatile.Read(ref _sentSeq);

    /// <summary>
    /// True when nothing this session produced is still on a link. The session's row and its blocks may be freed only here.
    /// </summary>
    public bool IsDrained => Volatile.Read(ref _sentSeq) == Volatile.Read(ref _nextSeq);

    /// <summary>The newest tick the client has reported applied, from its <c>PING</c>. Zero until the first one arrives.</summary>
    public long AckedTick => Volatile.Read(ref _ackedTick);

    /// <summary>The server tick at which this session was last heard from. Seeded when the slot is bound, so silence is measured from the handshake.</summary>
    public long PingTick => Volatile.Read(ref _pingTick);

    /// <summary>
    /// What the handshake granted this session, so the producer can tell whether a capability-gated block — <c>STATS</c> today — belongs in its frames.
    /// </summary>
    /// <remarks>
    /// The grant is the connection's (<c>SubscriptionConnection.GrantCaps</c>) and is therefore computed on a transport thread, while the only reader is the
    /// frame producer on the tick. One number written atomically by one thread and read by another is exactly what SUB-05 puts on its allow-list, and it is
    /// the same shape as <see cref="AckedTick"/> and <see cref="PingTick"/>. It is written once per session, between the slot being bound and the first frame
    /// being produced for it, and the zero <see cref="Initialize"/> leaves is the honest answer for a session whose <c>HELLO</c> has not been answered yet.
    /// </remarks>
    public Capabilities Caps => (Capabilities)(uint)Volatile.Read(ref _caps);

    /// <summary>Publishes the capabilities the handshake granted. Called by the connection thread, once, at <c>HELLO</c>.</summary>
    /// <param name="caps">The granted set.</param>
    public void NoteCapsGranted(Capabilities caps) => Volatile.Write(ref _caps, (int)(uint)caps);

    /// <summary>Clears a state to its initial values. Call once, before any thread can reach it.</summary>
    /// <param name="state">The state to clear, in memory the caller owns.</param>
    public static void Initialize(SessionSendState* state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Debug.Assert(K == 2, "the slot index is computed as a parity; another K needs a modulo here and a wider slot array");
        Debug.Assert(sizeof(FrameSlot) == SlotBytes, "a frame slot must match the layout the offsets above assume");
        NativeMemory.Clear(state, (nuint)sizeof(SessionSendState));
    }

    /// <summary>
    /// Claims the next sequence for this session, or reports that it must be skipped.
    /// </summary>
    /// <param name="sequence">The claimed sequence, or <c>-1</c> when the session is skipped.</param>
    /// <param name="recycled">
    /// The block that was in the claimed slot, to be returned to <see cref="FramePool"/> or reused in place. Invalid when the slot was empty.
    /// </param>
    /// <returns><see langword="false"/> when <c>NextSequence − SentSequence ≥ K</c> — skip, do not queue.</returns>
    /// <remarks>
    /// <para>
    /// <b>The acquire is what makes the slot reusable.</b> <c>Volatile.Read(ref _sentSeq)</c> pairs with <see cref="CompleteSend"/>'s release: the link's
    /// reads of the previous frame happen before that store, which happens before this load, which happens before every store this method and
    /// <see cref="PublishFrame"/> then make into the slot. Ordering matters on arm64 and costs nothing on x64 — an acquire load is <c>ldar</c> there and a
    /// plain <c>mov</c> here — and an acquire load orders every later load <i>and store</i> after itself, which is precisely the guarantee the slot needs.
    /// </para>
    /// <para>
    /// The slot is emptied here rather than in <see cref="PublishFrame"/> so that an encode which is abandoned leaves no block the session could return
    /// twice: the block is the caller's from this moment on, whatever happens next.
    /// </para>
    /// </remarks>
    public bool TryBeginFrame(out long sequence, out FrameBlock recycled)
    {
        var next = _nextSeq;

        // ACQUIRE — pairs with CompleteSend's release. Everything below is ordered after it.
        var sent = Volatile.Read(ref _sentSeq);

        if (next - sent >= K)
        {
            _skipRun++;
            sequence = -1;
            recycled = default;
            return false;
        }

        ref var slot = ref SlotFor(next);
        recycled = new FrameBlock((byte*)slot.Bytes, slot.Capacity);
        slot.Bytes = 0;
        slot.Capacity = 0;
        slot.Length = 0;
        slot.Tick = 0;

        _nextSeq = next + 1;
        sequence = next;
        return true;
    }

    /// <summary>
    /// Gives back a sequence claimed by <see cref="TryBeginFrame"/> without publishing a frame for it.
    /// </summary>
    /// <param name="sequence">The sequence returned by the matching <see cref="TryBeginFrame"/>.</param>
    /// <remarks>
    /// For an encode that produced nothing, or that could not rent a block. The block handed back by <see cref="TryBeginFrame"/> is still the caller's to
    /// return; this only rolls the producer-private counter back so the slot is claimed again next tick.
    /// </remarks>
    public void AbandonFrame(long sequence)
    {
        Debug.Assert(_nextSeq == sequence + 1, "only the sequence just claimed can be abandoned");
        Debug.Assert(Volatile.Read(ref _readySeq) <= sequence, "a published frame cannot be abandoned");

        _nextSeq = sequence;
        _skipRun++;
    }

    /// <summary>
    /// Publishes a frame to the send side.
    /// </summary>
    /// <param name="sequence">The sequence claimed by <see cref="TryBeginFrame"/>.</param>
    /// <param name="block">The block the frame was encoded into.</param>
    /// <param name="length">How many bytes of it the frame occupies.</param>
    /// <param name="tick">The tick the frame describes. The send loop will not send it before that tick is committed.</param>
    /// <remarks>
    /// <b>Every byte of the frame must already be written.</b> The release below is the only thing that makes those bytes visible to the send loop: a store
    /// to the slot or to the frame that happened before it is guaranteed to be observed by any thread that acquires <see cref="ReadySequence"/>, and a store
    /// made after it is guaranteed to be observed by nothing at all. Writing a byte after this call is the SUB-04 violation, and it is invisible on x64 —
    /// total-store-order hides it — while arm64 will happily deliver a half-written frame.
    /// </remarks>
    public void PublishFrame(long sequence, in FrameBlock block, int length, long tick)
    {
        Debug.Assert(sequence == _nextSeq - 1, "frames are published in the order they were claimed");
        Debug.Assert(Volatile.Read(ref _readySeq) == sequence, "a sequence is published exactly once");
        Debug.Assert(block.IsValid, "a published frame needs a block");
        Debug.Assert(length >= 0 && length <= block.Capacity, "a frame cannot be longer than the block holding it");

        ref var slot = ref SlotFor(sequence);
        slot.Bytes = (nint)block.Bytes;
        slot.Capacity = block.Capacity;
        slot.Length = length;
        slot.Tick = tick;

        _producedTick = tick;
        _skipRun = 0;

        // RELEASE — pairs with TryClaimFrame's acquire. The frame's bytes and the slot above are visible to whoever reads the value this publishes.
        Volatile.Write(ref _readySeq, sequence + 1);
    }

    /// <summary>
    /// Records a skip the producer decided for a reason of its own — an exhausted frame pool, a lagging client, a degraded rate class.
    /// </summary>
    /// <remarks>
    /// The in-flight skip is counted by <see cref="TryBeginFrame"/> itself; this is for the others, so <see cref="SkipRun"/> means one thing.
    /// </remarks>
    public void NoteSkipped() => _skipRun++;

    /// <summary>
    /// Takes the next frame to send, if there is one and its tick is committed.
    /// </summary>
    /// <param name="committedTick">The value read from <see cref="FramePublicationGate.CommittedTick"/>.</param>
    /// <param name="frame">The frame to hand to the link, or an invalid view.</param>
    /// <returns><see langword="false"/> when nothing is ready, or when the newest ready frame describes a tick that is not yet durable.</returns>
    /// <remarks>
    /// <para>
    /// <b>Three reads in one order, and the order is the rule.</b> The acquire of <see cref="ReadySequence"/> comes first, so that every load after it — the
    /// slot's fields, and the frame's bytes the caller then reads — is ordered after the producer's release and therefore sees a complete frame. The load of
    /// <see cref="SentSequence"/> that follows is plain because this side is its only writer.
    /// </para>
    /// <para>
    /// <b>The tick gate is the durability one.</b> A frame produced for tick T is not sendable until T's unit of work has flushed, because publication is
    /// gated on the flush and not merely ordered after it (SUB-02). Refusing here rather than waiting is deliberate: the pump moves on to another session and
    /// this one's frame goes out on the next pass, which costs a fraction of a tick and never parks a thread.
    /// </para>
    /// </remarks>
    public bool TryClaimFrame(long committedTick, out FrameView frame)
    {
        // ACQUIRE — pairs with PublishFrame's release. Every load below is ordered after it.
        var ready = Volatile.Read(ref _readySeq);
        var sent = _sentSeq;

        if (sent >= ready)
        {
            frame = default;
            return false;
        }

        ref var slot = ref SlotFor(sent);
        var tick = slot.Tick;
        if (tick > committedTick)
        {
            frame = default;
            return false;
        }

        frame = new FrameView((byte*)slot.Bytes, slot.Length, tick, sent);
        return true;
    }

    /// <summary>
    /// Releases the slot a frame occupied, once the link no longer needs its bytes.
    /// </summary>
    /// <param name="sequence">The sequence from the <see cref="FrameView"/> that was sent.</param>
    /// <remarks>
    /// <b>Call it after the send has completed, never before.</b> The release below is what permits the producer to overwrite the slot, so a call made while
    /// a socket still holds the bytes hands one buffer to two writers. A release store orders every earlier load and store before itself, which is what makes
    /// "the link's reads happen before the overwrite" true rather than merely likely.
    /// </remarks>
    public void CompleteSend(long sequence)
    {
        Debug.Assert(sequence == _sentSeq, "frames complete in the order they were claimed");
        Debug.Assert(sequence < Volatile.Read(ref _readySeq), "a frame that was never published cannot complete");

        // RELEASE — pairs with TryBeginFrame's acquire.
        Volatile.Write(ref _sentSeq, sequence + 1);
    }

    /// <summary>
    /// Records the newest tick the client says it has applied. Called by the link thread from <c>PING</c>.
    /// </summary>
    /// <param name="tick">The tick the client reported.</param>
    /// <remarks>
    /// Monotonic, so a <c>PING</c> that overtakes an older one cannot move the acknowledgement backwards and make the lag skip fire on a healthy client. One
    /// number written atomically by one thread, which is the whole of what SUB-05 allows a transport thread to publish here.
    /// </remarks>
    public void ReportAppliedTick(long tick)
    {
        if (tick > Volatile.Read(ref _ackedTick))
        {
            Volatile.Write(ref _ackedTick, tick);
        }
    }

    /// <summary>
    /// Records that the session was heard from on this tick. Called by the link thread from <c>PING</c>, and once by the tick when the slot is bound.
    /// </summary>
    /// <param name="tick">The server tick at the moment the message arrived.</param>
    /// <remarks>
    /// Monotonic, one word, written atomically by one thread at a time — the same shape as <see cref="ReportAppliedTick"/> and on SUB-05's allow-list for the
    /// same reason. The tick reads it to close a session that has stopped talking (4001), which needs a last-heard-from mark that survives a session producing
    /// no frames at all: <see cref="AckedTick"/> cannot serve, because a client with nothing to apply never moves it.
    /// </remarks>
    public void NotePing(long tick)
    {
        if (tick > Volatile.Read(ref _pingTick))
        {
            Volatile.Write(ref _pingTick, tick);
        }
    }

    /// <summary>
    /// The blocks still held in the slots, so a closing session can return them. Valid only once <see cref="IsDrained"/> is true.
    /// </summary>
    /// <param name="slotIndex">0 or 1.</param>
    /// <returns>The block in that slot, or an invalid one.</returns>
    public FrameBlock BlockInSlot(int slotIndex)
    {
        ref var slot = ref SlotFor(slotIndex);
        return new FrameBlock((byte*)slot.Bytes, slot.Capacity);
    }

    /// <summary>Empties a slot after its block has been returned, so nothing can return it twice.</summary>
    /// <param name="slotIndex">0 or 1.</param>
    public void ClearSlot(int slotIndex)
    {
        ref var slot = ref SlotFor(slotIndex);
        slot.Bytes = 0;
        slot.Capacity = 0;
        slot.Length = 0;
        slot.Tick = 0;
    }

    /// <summary>The slot a sequence lands in. K is 2, so the index is the sequence's parity.</summary>
    /// <remarks>
    /// <c>[UnscopedRef]</c> because the ref names a field of a state that lives in native memory for as long as its session does, not a field of a temporary
    /// copy. Every caller here consumes it inside the same method; none of them lets it escape.
    /// </remarks>
    [UnscopedRef]
    private ref FrameSlot SlotFor(long sequence) => ref ((sequence & 1) == 0 ? ref _slot0 : ref _slot1);
}
