using System;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// What carries a published frame from the engine to a link: one pump per session, started by the tick driver after the flush and running on the thread pool.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the driver starts it and the pool runs it.</b> Publication is gated on the flush (SUB-02), and the flush is the tick driver's — so the moment a
/// frame becomes sendable is a moment only the driver knows. Sending on the driver, though, would put a socket write on the thread that owns the next tick's
/// deadline. The driver therefore does the one cheap thing (release the committed tick, walk the workers' ready lists) and hands each session to the pool.
/// </para>
/// <para>
/// <b>One in flight per session, and the flag is the proof.</b> <see cref="ISubscriptionLink"/> promises a link it will never be asked to send twice at once,
/// and that promise is kept here: a session's pump is started only by the transition of <c>_pumping[slot]</c> from 0 to 1, and it clears the flag only when it
/// has nothing left to send. Everything between is one loop on one thread. The re-check after clearing is what closes the lost-wakeup window — a frame
/// published between the last claim and the clear would otherwise sit until the next tick woke the session again.
/// </para>
/// <para>
/// <b>It never returns a block.</b> The producer recycles its own blocks: <c>SessionSendState.TryBeginFrame</c> hands the outgoing block back to the session
/// that claimed the slot, which returns it to the pool or encodes into it again. A pump that returned blocks would be a second owner of the same memory.
/// </para>
/// <para>
/// <b>Pointers, and what they point at.</b> A frame's bytes are native pool memory, reached through <see cref="NativeFrameMemoryManager"/> — one per slot,
/// reset per send — so a <see cref="ReadOnlyMemory{T}"/> reaches a socket with no pointer over GC memory anywhere on the path.
/// </para>
/// <para>
/// <b>What it may touch on the session table.</b> Only <see cref="SessionTable.BeginSend"/>, <see cref="SessionTable.EndSend"/> and
/// <see cref="SessionTable.RequestClose"/> — the interlocked words SUB-05 puts on a transport thread's allow-list. Every other field of a row belongs to the
/// tick.
/// </para>
/// </remarks>
internal sealed class SendPump : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits for the pumps to come back before giving up on them.</summary>
    /// <remarks>
    /// Generous for a send that is merely in flight, short enough that a transport which never returns from a write does not hang the process's shutdown. The
    /// links are closed first, so a well-behaved transport is back in microseconds.
    /// </remarks>
    private const int QuiesceTimeoutMs = 5000;

    private readonly SessionTable _sessions;
    private readonly FrameAssembler _frames;
    private readonly ISubscriptionLink[] _links;
    private readonly NativeFrameMemoryManager[] _buffers;
    private readonly int[] _pumping;
    private readonly SessionPumpWorkItem[] _workItems;
    private readonly CancellationTokenSource _stopping = new();

    private long _framesSent;
    private long _bytesSent;
    private long _sendFailures;
    private int _activePumps;
    private int _disposed;

    /// <summary>Builds the pump table for a runtime's session capacity.</summary>
    /// <param name="sessions">The session table, for the in-flight counter and the close request.</param>
    /// <param name="frames">The assembler, for the publication gate, the per-slot hand-off state and the ready lists.</param>
    /// <param name="maxSessions">How many slots the table has.</param>
    public SendPump(SessionTable sessions, FrameAssembler frames, int maxSessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessions);

        _sessions = sessions;
        _frames = frames;
        _links = new ISubscriptionLink[maxSessions];
        _buffers = new NativeFrameMemoryManager[maxSessions];
        _pumping = new int[maxSessions];
        _workItems = new SessionPumpWorkItem[maxSessions];
    }

    /// <summary>Frames handed to a link and completed.</summary>
    public long FramesSent => Volatile.Read(ref _framesSent);

    /// <summary>Bytes of frame handed to a link.</summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);

    /// <summary>Sends that faulted, each of which closed its session with 1011.</summary>
    public long SendFailures => Volatile.Read(ref _sendFailures);

    /// <summary>Pumps that had not come back when <see cref="Dispose"/> gave up waiting. Non-zero is a transport that does not honour a close.</summary>
    public int PumpsStillRunningAtDispose { get; private set; }

    /// <summary>Sessions whose pump is running right now. Zero means every frame published so far has left or been abandoned.</summary>
    public int ActivePumps => Volatile.Read(ref _activePumps);

    /// <summary>Whether the pump has been torn down. Acquire, because a pump thread reads it to decide whether the frame pool is still there.</summary>
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Binds a session's slot to the link its frames go out on.
    /// </summary>
    /// <param name="session">The session, just admitted.</param>
    /// <param name="link">Its link.</param>
    /// <remarks>
    /// Called by the connection once the row is published, so the pump can never find a link for a slot whose session is not yet open. The store is a release
    /// for the same reason the row's is: a pump that sees the link must see everything the admitting thread wrote before it.
    /// </remarks>
    public void AttachLink(SessionId session, ISubscriptionLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (session.IsValid && (uint)session.Slot < (uint)_links.Length)
        {
            Volatile.Write(ref _links[session.Slot], link);
        }
    }

    /// <summary>
    /// Unbinds a slot's link, after which nothing more is sent for it.
    /// </summary>
    /// <param name="session">The session that closed.</param>
    /// <remarks>
    /// A pump already inside a send keeps its own reference for that one message and completes normally; the next claim finds no link and stops. The frame's
    /// bytes stay valid throughout, because the table only re-leases a row once every send it counted has ended.
    /// </remarks>
    public void DetachLink(SessionId session)
    {
        if (session.IsValid && (uint)session.Slot < (uint)_links.Length)
        {
            Volatile.Write(ref _links[session.Slot], null);
        }
    }

    /// <summary>
    /// Publishes a tick as durable and wakes the pump of every session that produced a frame for it.
    /// </summary>
    /// <param name="tick">The tick whose unit of work has flushed.</param>
    /// <remarks>
    /// <b>The order is the rule.</b> The committed tick is released first, so a pump woken by the walk below already sees the value that makes its frame
    /// sendable. Waking first and publishing after would let a pump run, find the tick uncommitted, clear its flag and stop — with no further wakeup until the
    /// next tick produced a frame for that session.
    /// </remarks>
    public void PublishAndWake(long tick)
    {
        if (IsDisposed)
        {
            // Cleared even here. The lists are appended to by the producer and emptied only by this method and by DiscardProduced, so returning without
            // clearing would grow them by one entry per session per tick for as long as a disposed runtime kept ticking.
            _frames.ClearReady();
            return;
        }

        _frames.Gate.Publish(tick);

        for (var worker = 0; worker < _frames.WorkerCount; worker++)
        {
            var ready = _frames.ReadyOf(worker);
            for (var i = 0; i < ready.Length; i++)
            {
                Wake(ready[i]);
            }
        }

        _frames.ClearReady();
    }

    /// <summary>
    /// Discards what the tick produced without publishing it, because the tick did not earn publication.
    /// </summary>
    /// <returns>How many sessions had a frame that will now never be sent.</returns>
    /// <remarks>
    /// <b>SUB-02's fourth clause.</b> An aborted tick, a failed fence, a faulted replication stage or a flush that threw all leave frames sitting in slots
    /// describing a tick that is not durable. Those frames can never be sent — the gate will refuse them forever — and the sessions holding them would fill
    /// their K slots and stall. Closing every session that produced one is the only correct outcome: its client's baseline would otherwise be ahead of what
    /// the server can prove it wrote.
    /// </remarks>
    public int DiscardProduced()
    {
        if (IsDisposed)
        {
            _frames.ClearReady();
            return 0;
        }

        var closed = 0;
        for (var worker = 0; worker < _frames.WorkerCount; worker++)
        {
            var ready = _frames.ReadyOf(worker);
            for (var i = 0; i < ready.Length; i++)
            {
                // The identity the frame was produced for, not whoever holds the slot now. A row can close, drain and be re-leased between the publication
                // and this walk, and closing the new occupant for its predecessor's frame would end a session that had done nothing wrong; RequestClose
                // validates the generation, so a stale identity is refused rather than misapplied.
                if (_sessions.RequestClose(ready[i], SessionCloseReason.InternalError, CloseCodes.InternalError))
                {
                    closed++;
                }
            }
        }

        _frames.ClearReady();
        return closed;
    }

    /// <summary>Starts a session's pump if it is not already running.</summary>
    /// <param name="session">The session a frame was published for.</param>
    private void Wake(SessionId session)
    {
        var slot = session.Slot;
        if (!session.IsValid || (uint)slot >= (uint)_pumping.Length)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _pumping[slot], 1, 0) != 0)
        {
            return;
        }

        // Enrol, THEN re-read the latch. The other order has a window: this thread passes the check, Dispose sets the latch and spins, observes zero active
        // pumps and frees the pool — and then this pump starts reading the frame bytes out of freed memory. Enrolling first makes the two orders exclusive,
        // because Dispose's spin cannot observe zero once this increment has happened and this pump cannot proceed once the latch is set.
        Interlocked.Increment(ref _activePumps);
        if (IsDisposed)
        {
            Interlocked.Decrement(ref _activePumps);
            Volatile.Write(ref _pumping[slot], 0);
            return;
        }


        // One reusable work item per slot, so waking a session allocates nothing. The item is only ever queued by the thread that won the flag above, and it
        // is not queued again until that pump has cleared it, so one instance can never be in two queues.
        var item = _workItems[slot] ??= new SessionPumpWorkItem(this, slot);
        ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false);
    }

    /// <summary>
    /// One session's send loop: claim, send, complete, repeat until nothing is claimable.
    /// </summary>
    /// <param name="slot">The session table row.</param>
    /// <returns>The loop.</returns>
    internal async Task PumpAsync(int slot)
    {
        try
        {
            while (true)
            {
                while (await TrySendOneAsync(slot).ConfigureAwait(false))
                {
                }

                // Clear, then look again. A frame published between the failed claim above and this store would otherwise wait for the next tick that
                // produced for this session — which, for a session that is now idle, may never come.
                Volatile.Write(ref _pumping[slot], 0);

                if (!Claimable(slot) || Interlocked.CompareExchange(ref _pumping[slot], 1, 0) != 0)
                {
                    return;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activePumps);
        }
    }

    /// <summary>
    /// Whether another pass would send anything for this slot.
    /// </summary>
    /// <param name="slot">The session table row.</param>
    /// <returns><see langword="true"/> when a claim would succeed AND there is still a link to send it on.</returns>
    /// <remarks>
    /// <b>It must agree with <see cref="TrySendOneAsync"/> on every reason to stop, or the loop never stops.</b> Testing only the claim was a live-lock: a
    /// session whose link is detached while a committed frame is still in its slot — an ordinary disconnect racing a tick that produced for it — would send
    /// nothing, clear the flag, find the frame still claimable, re-arm, and spin a pool thread at full tilt until the runtime was disposed.
    /// </remarks>
    private unsafe bool Claimable(int slot)
    {
        if (IsDisposed || Volatile.Read(ref _links[slot]) == null)
        {
            return false;
        }

        var session = _sessions.IdAt(slot);
        return session.IsValid && _frames.SendStateOf(slot)->TryClaimFrame(_frames.Gate.CommittedTick, out _);
    }

    /// <summary>Claims a slot's next sendable frame, as values an async method may hold across an await.</summary>
    /// <param name="slot">The session table row.</param>
    /// <param name="bytes">The frame's first byte.</param>
    /// <param name="length">Its length.</param>
    /// <param name="sequence">The sequence to complete once the link is done with it.</param>
    /// <returns><see langword="false"/> when nothing is claimable.</returns>
    /// <remarks>
    /// The pointer leaves as an <see cref="nint"/> because an async method cannot be compiled in an unsafe context, and because a raw pointer must not be a
    /// field of a state machine the compiler may place on the heap. It is turned back into a pointer only inside <see cref="ViewOf"/>, on the stack.
    /// </remarks>
    private unsafe bool TryClaim(int slot, out nint bytes, out int length, out long sequence)
    {
        if (!_frames.SendStateOf(slot)->TryClaimFrame(_frames.Gate.CommittedTick, out var frame))
        {
            bytes = 0;
            length = 0;
            sequence = 0;
            return false;
        }

        bytes = (nint)frame.Bytes;
        length = frame.Length;
        sequence = frame.Sequence;
        return true;
    }

    /// <summary>Points this slot's reusable view at a claimed frame, creating it on the session's first send.</summary>
    /// <param name="slot">The session table row.</param>
    /// <param name="bytes">The frame's first byte.</param>
    /// <param name="length">Its length.</param>
    /// <returns>The view.</returns>
    private unsafe NativeFrameMemoryManager ViewOf(int slot, nint bytes, int length)
    {
        var buffer = _buffers[slot];
        if (buffer == null)
        {
            buffer = new NativeFrameMemoryManager((byte*)bytes, length);
            _buffers[slot] = buffer;
            return buffer;
        }

        buffer.Reset((byte*)bytes, length);
        return buffer;
    }

    /// <summary>Releases the slot a frame occupied. The release inside it is what lets the producer overwrite the bytes (SUB-04).</summary>
    /// <param name="slot">The session table row.</param>
    /// <param name="sequence">The sequence that was sent.</param>
    private unsafe void Complete(int slot, long sequence) => _frames.SendStateOf(slot)->CompleteSend(sequence);

    /// <summary>
    /// Sends at most one frame for a session.
    /// </summary>
    /// <param name="slot">The session table row.</param>
    /// <returns><see langword="false"/> when nothing was claimable, the link is gone, or the send failed.</returns>
    private async ValueTask<bool> TrySendOneAsync(int slot)
    {
        if (IsDisposed)
        {
            return false;
        }

        var link = Volatile.Read(ref _links[slot]);
        if (link == null)
        {
            return false;
        }

        if (!TryClaim(slot, out var bytes, out var length, out var sequence))
        {
            return false;
        }

        // The identity is read AFTER the frame is claimed, so a row recycled in between is caught by BeginSend refusing an identity it no longer names —
        // rather than by this pump sending one session's bytes under another's send count.
        var session = _sessions.IdAt(slot);
        if (!session.IsValid || !_sessions.BeginSend(session))
        {
            // The session is gone or closing. The frame is left claimed: the row cannot be re-leased until it drains, and the close path is what drains it.
            return false;
        }

        // Built on the session's first send and re-pointed thereafter, which is what keeps a per-frame allocation off the path. Re-pointing is safe here
        // precisely because of the one-in-flight rule: every Memory<byte> this manager handed out belongs to a send that has already completed.
        var buffer = ViewOf(slot, bytes, length);

        try
        {
            await link.SendAsync(buffer.Memory, _stopping.Token).ConfigureAwait(false);

            // Only now may the producer overwrite the slot: the release inside CompleteSend is what orders the link's reads of these bytes ahead of the
            // next frame's writes into them (SUB-04).
            Complete(slot, sequence);
            Interlocked.Increment(ref _framesSent);
            Interlocked.Add(ref _bytesSent, length);
            return true;
        }
        catch (Exception)
        {
            // A faulted send means the link no longer carries this session's bytes in order, and ordering is the whole of what the protocol rests on. The
            // frame still completes — the bytes are no longer in use either way — and the session is closed.
            Complete(slot, sequence);
            Interlocked.Increment(ref _sendFailures);
            _sessions.RequestClose(session, SessionCloseReason.LinkLost, CloseCodes.InternalError);
            link.Close(CloseCodes.InternalError, "send failed");
            return false;
        }
        finally
        {
            // The view is left pointing at the block it just sent rather than cleared: Reset refuses a null, and nothing reads a manager between sends. The
            // block itself is the producer's to recycle, and it does not move.
            _sessions.EndSend(session);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The exchange above is the release every pump's acquire pairs with, and it also makes a second Dispose a no-op rather than a second teardown.
        _stopping.Cancel();

        // Close every link before waiting. A pump parked inside a socket write to a peer that has stopped reading would otherwise hold this thread — which is
        // the one disposing the runtime — for as long as that peer liked; closing the link is what makes the write return.
        for (var i = 0; i < _links.Length; i++)
        {
            var link = Volatile.Read(ref _links[i]);
            Volatile.Write(ref _links[i], null);
            try
            {
                link?.Close(CloseCodes.GoingAway, "server stopping");
            }
            catch (Exception)
            {
                // A transport whose close throws must not stop the teardown of the others, and there is nobody left to report it to.
            }
        }

        // Quiesce before anything a running pump reads is freed. A pump holds a pointer into pool memory for the duration of one send, and the pool is the
        // assembler's to dispose — so returning from here with a pump still inside SendAsync would free the bytes under a socket.
        var spin = new SpinWait();
        var deadline = Environment.TickCount64 + QuiesceTimeoutMs;
        while (Volatile.Read(ref _activePumps) > 0 && Environment.TickCount64 < deadline)
        {
            spin.SpinOnce();
        }

        // Bounded, and the overrun is recorded rather than waited out: a link that never returns from a write is a transport defect, and blocking the
        // runtime's disposal for it forever turns that defect into a hang with no diagnosis. What remains is a pump holding a pointer into a pool the
        // assembler is about to free, so the count is worth surfacing to whoever reads the teardown.
        PumpsStillRunningAtDispose = Volatile.Read(ref _activePumps);

        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i]?.Release();
            _buffers[i] = null;
        }

        _stopping.Dispose();
    }

    /// <summary>One session's reusable thread-pool work item: the pump and the slot, and nothing else.</summary>
    /// <remarks>
    /// <see cref="IThreadPoolWorkItem"/> rather than a <see cref="WaitCallback"/> because a callback would box the slot on every wake — one allocation per
    /// session per tick, on the path that exists to keep allocations off the tick.
    /// </remarks>
    private sealed class SessionPumpWorkItem : IThreadPoolWorkItem
    {
        private readonly SendPump _pump;
        private readonly int _slot;

        public SessionPumpWorkItem(SendPump pump, int slot)
        {
            _pump = pump;
            _slot = slot;
        }

        /// <inheritdoc />
        public void Execute() => _ = _pump.PumpAsync(_slot);
    }
}
