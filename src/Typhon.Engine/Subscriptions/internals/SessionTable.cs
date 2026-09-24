using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Where a session lives while it is connected: one native row per slot, an identity that survives recycling, and a lifecycle the tick can see.
/// </summary>
/// <remarks>
/// <para>
/// <b>One writer, and the API is what enforces it.</b> A published row is written by the tick side alone. The members reachable from a transport thread touch
/// exactly one word of a row — <see cref="SessionRow.Gate"/> — and they touch it with a compare-exchange that validates the identity in the same step:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="TryLease"/> — pops a pre-made identity off the free stack and marks the row leased. <see cref="ReleaseLease"/> gives an
/// unopened lease back, which is what a handshake that fails between the lease and <see cref="Open"/> owes the table.</description></item>
/// <item><description><see cref="Open"/> — fills the row of a slot this thread alone holds, then publishes it by exchanging the lease mark for the identity.
/// Before that store the row is unreachable: no identity for it is visible anywhere else, and the tick's iteration is driven by delivered events rather than by
/// scanning.</description></item>
/// <item><description><see cref="RequestClose"/> — one compare-exchange that carries the identity, the reason and the code together. It takes no reason string,
/// deliberately: a close code is a number a transport thread can publish atomically, while a string is a managed write the tick would read torn.</description>
/// </item>
/// <item><description><see cref="BeginSend"/> / <see cref="EndSend"/> — the same compare-exchange, moving the send counter. It is what keeps a slot from being
/// recycled underneath a frame that is still on a socket.</description></item>
/// </list>
/// <para>
/// <b>Identity and counter move together, or the slot leaks.</b> Every one of those is a CAS on the gate, never a test followed by an independent
/// read-modify-write. A checked-then-incremented counter loses its race the moment the tick recycles the slot in between: the increment lands on the next
/// occupant, the mismatch decrements it below zero, and a counter that can no longer reach zero is a slot no tick will ever recycle. The gate makes that
/// interleaving unrepresentable — a recycle that races an in-flight <see cref="BeginSend"/> loses its own CAS and simply retries next tick.
/// </para>
/// <para>
/// Everything else — <see cref="Close"/>, <see cref="BeginTick"/>, <see cref="ApplyPendingCloses"/>, every field mutator the request log calls — is tick-side
/// only and carries the same debug re-entrancy guard the rest of the replication structures use. That guard catches two callers at once, which is the failure
/// that corrupts state; it deliberately does not check thread identity, because the tick legitimately runs on the driver thread one tick and a pool worker the
/// next (see <see cref="ReplicationThreadAffinity"/>).
/// </para>
/// <para>
/// <b>Rows are native memory.</b> One aligned allocation holds the free-stack header, every row and the stack's entries; nothing here is a GC array behind a
/// pointer. A row is exactly one cache line, so two sessions never share one: the send counter a transport thread moves and the fields the tick reads are on
/// the same line by design — they belong to the same session — but no other session pays for that traffic. The stack's head has a line of its own at the front
/// of the block rather than a field on this object, because a field would share its line with the counters the tick writes every <see cref="BeginTick"/> and
/// every transport-side CAS would then pull a line another core had just dirtied.
/// </para>
/// <para>
/// <b>A slot comes back only after its <c>Closed</c> event was delivered and its last send finished.</b> Closing marks the row and queues the event; the tick
/// that delivers that event marks the row delivered; the next tick recycles it, and only then if no send is in flight. Recycling bumps the generation, so
/// every identity that ever named the old session fails its lookup rather than naming the new one.
/// </para>
/// <para>
/// <b>Disposal quiesces before it frees.</b> The transport-callable members run on threads this table does not own, so freeing the block while one of them is
/// inside would be a use-after-free on native memory. Every member that dereferences the block passes through a caller gate: <see cref="Dispose"/> latches it
/// closed, waits for the callers already inside to leave, and only then frees. A call that arrives after the latch answers "gone" rather than touching
/// anything, which is the same answer it would get for a recycled slot.
/// </para>
/// </remarks>
internal sealed unsafe class SessionTable : IDisposable
{
    /// <summary>The structural ceiling: a slot is a <see cref="ushort"/>, and 65 535 leaves the top value free as a sentinel.</summary>
    internal const int MaxSlots = ushort.MaxValue;

    /// <summary>Rows are cache-line aligned and cache-line sized, so no two sessions share a line.</summary>
    internal const int RowBytes = 64;

    /// <summary>The block's first line: the free stack's head and its push lock, kept off every line the tick writes.</summary>
    internal const int HeaderBytes = 64;

    /// <summary>The most frames one session may have on a link at once. The gate carries the count in eight bits; the design's own ceiling is two.</summary>
    internal const int MaxSendsInFlight = 255;

    private readonly SubscriptionsOptions _options;
    private readonly SessionEvents _events;
    private readonly int _capacity;

    // Nulled by Dispose, after the caller gate has drained: a late caller must find "gone" rather than a pointer into freed memory.
    private PinnedMemoryBlock _memory;
    private SessionRow* _rows;
    private uint* _freeIds;
    private long* _freeHead;
    private int* _pushLock;

    // Side data a row cannot hold: managed references, kept in slot-indexed arrays. They are written by the admitting thread before the row is published and
    // read by the tick afterwards, which the release/acquire pair on SessionRow.Gate orders. Readers re-validate the identity after the read, because the
    // arrays are keyed by SLOT and the slot outlives its occupant.
    private readonly string[] _sessionKinds;
    private readonly string[] _profileNames;

    // The index of each slot's profile in the compiled profiles, or -1: tick side only, written with the name and read by the frame stage's prologue so it
    // never hashes a profile name per session per tick. The resolver is bound at Start, when the profiles are compiled.
    private readonly int[] _profileIndices;
    private Func<string, int> _profileResolver;
    private readonly string[] _closeReasons;
    private readonly object[] _appData;
    private readonly SessionViewpoint[] _viewpoints;

    // Each slot's run-time Sphere radius (SetRadius), or 0 for its profile's own. Tick side, like the viewpoint; cleared when the slot opens and when its
    // profile changes, because a radius is valid only within the profile it was checked against.
    private readonly double[] _radii;
    private readonly SessionLimits[] _declaredLimits;

    // Tick-side bookkeeping. Every one of these is driven by delivered events, never by scanning the table: the cost of a tick follows the sessions that
    // changed, not the size of the table an operator configured.
    private readonly List<int> _openSlots = [];
    private readonly int[] _openIndexBySlot;
    private readonly List<int> _closedLastTick = [];
    private readonly List<int> _awaitingSends = [];

    /// <summary>Callers inside a member that dereferences the block, plus the disposing latch in the sign bit. Padded: transport threads move it.</summary>
    private CacheLinePaddedInt _callers;

    private ReplicationThreadAffinity _affinity;
    private int _openCount;

    /// <summary>
    /// Creates the table and commits its rows.
    /// </summary>
    /// <param name="id">Stable resource id for the backing allocation.</param>
    /// <param name="parent">Resource-graph parent the allocation registers under.</param>
    /// <param name="allocator">Engine allocator.</param>
    /// <param name="options">Operator configuration; <see cref="SubscriptionsOptions.MaxSessions"/> fixes the table's size for its lifetime.</param>
    /// <param name="events">The lifecycle stream this table produces into.</param>
    /// <remarks>
    /// Unlike the block and ring pools, the table is committed up front rather than grown: it is one allocation of
    /// <c>64 + MaxSessions × (64 + 4)</c> bytes — 544 KiB at the default — and its whole purpose is an O(1) lookup from a slot number, which a growing
    /// structure cannot give without a level of indirection on every session touch of every tick. Nothing is committed until a runtime with subscriptions
    /// configured starts, so a database with no replication still pays zero.
    /// </remarks>
    public SessionTable(string id, IResource parent, IMemoryAllocator allocator, SubscriptionsOptions options, SessionEvents events)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(events);

        if (options.MaxSessions is < 1 or > MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxSessions,
                $"{nameof(SubscriptionsOptions.MaxSessions)} is 1-{MaxSlots}. The ceiling is structural: a {nameof(SessionId)} packs a 16-bit slot and a " +
                "16-bit generation into 32 bits, so a slot number wider than a ushort does not exist.");
        }

        if (options.ObserversPerSession < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ObserversPerSession,
                $"{nameof(SubscriptionsOptions.ObserversPerSession)} is the operator's ceiling on a session's observers and must be at least 1. An observer " +
                "is a query per tick, so this is the rail that bounds a session's query cost; zero would admit sessions that can never be served.");
        }

        Debug.Assert(sizeof(SessionRow) == RowBytes, "a session row must be exactly one cache line");

        _options = options;
        _events = events;
        _capacity = options.MaxSessions;

        var rowBytes = _capacity * RowBytes;
        var stackBytes = _capacity * sizeof(uint);
        _memory = allocator.AllocatePinned(id, parent, HeaderBytes + rowBytes + stackBytes, zeroed: true, alignment: RowBytes);
        _freeHead = (long*)_memory.DataAsPointer;
        _pushLock = (int*)(_memory.DataAsPointer + sizeof(long));
        _rows = (SessionRow*)(_memory.DataAsPointer + HeaderBytes);
        _freeIds = (uint*)(_memory.DataAsPointer + HeaderBytes + rowBytes);

        _viewpoints = new SessionViewpoint[_capacity];
        _radii = new double[_capacity];
        _sessionKinds = new string[_capacity];
        _profileNames = new string[_capacity];
        _profileIndices = new int[_capacity];
        Array.Fill(_profileIndices, -1);
        _closeReasons = new string[_capacity];
        _appData = new object[_capacity];
        _declaredLimits = new SessionLimits[_capacity];
        _openIndexBySlot = new int[_capacity];

        // Pushed in reverse so the first lease takes slot 0: a table walked in slot order then matches the order sessions connected in, which makes a dump of
        // it readable. Generation starts at 1 — zero is never issued, so a default SessionId cannot name a live session.
        for (var slot = _capacity - 1; slot >= 0; slot--)
        {
            _freeIds[_capacity - 1 - slot] = new SessionId((ushort)slot, 1).Value;
            _openIndexBySlot[slot] = -1;
        }

        *_freeHead = _capacity;
    }

    /// <summary>How many sessions may be connected at once.</summary>
    public int Capacity => _capacity;

    /// <summary>Sessions currently open, as the tick sees them.</summary>
    public int OpenCount => _openCount;

    /// <summary>Slots available to lease right now. Below <see cref="Capacity"/> whenever sessions are open, closing, leased or waiting on a send.</summary>
    public int FreeCount
    {
        get
        {
            if (!TryEnter())
            {
                return 0;
            }

            try
            {
                return (int)(uint)Volatile.Read(ref *_freeHead);
            }
            finally
            {
                Exit();
            }
        }
    }

    /// <summary>The lifecycle stream this table produces into.</summary>
    public SessionEvents Events => _events;

    /// <summary>This tick's open sessions, in slot order. Valid until the next <see cref="BeginTick"/>.</summary>
    public OpenSessionEnumerator GetEnumerator() => new(this);

    /// <summary>
    /// Takes a slot for a client that is being admitted. Callable from any thread.
    /// </summary>
    /// <param name="session">The identity, unique for the life of that slot's occupancy.</param>
    /// <returns><see langword="false"/> when every slot is taken; the caller answers <see cref="CloseCodes.TryAgainLater"/>.</returns>
    /// <remarks>
    /// The identity is not minted here: the free stack holds identities that were stamped with their next generation when the slot was recycled, so a lease
    /// reads one out rather than inventing one. The row is marked leased in the same gate the identity will later be published in, which is what makes
    /// <see cref="ReleaseLease"/> able to tell an abandoned lease from an opened session without a second field to keep in step.
    /// </remarks>
    public bool TryLease(out SessionId session)
    {
        session = SessionId.None;
        if (!TryEnter())
        {
            return false;
        }

        try
        {
            while (true)
            {
                var packed = Volatile.Read(ref *_freeHead);
                var top = (int)(uint)packed;
                if (top == 0)
                {
                    return false;
                }

                var candidate = Volatile.Read(ref _freeIds[top - 1]);
                var next = Pack((ulong)packed >> 32, top - 1);
                if (Interlocked.CompareExchange(ref *_freeHead, next, packed) != packed)
                {
                    continue;
                }

                var leased = SessionId.FromValue(candidate);

                // The row is free — nothing else holds this identity, so the exchange cannot lose. It marks the row taken so a release can be told apart from
                // a double release, and so a lookup on the leased identity answers "not a session yet" rather than reading a half-written row.
                var previous = Interlocked.Exchange(ref (_rows + leased.Slot)->Gate, candidate | GateLeasedBit);
                Debug.Assert(previous == 0, "a leased slot's gate must have been free");

                session = leased;
                return true;
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Gives back a slot that was leased and never opened. Callable from any thread.
    /// </summary>
    /// <param name="session">The identity <see cref="TryLease"/> handed out.</param>
    /// <returns><see langword="false"/> when the identity was already opened, already released, or never leased — so a double release is a no-op.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is what a failed handshake owes the table.</b> Between the lease and <see cref="Open"/> a connection can still fail: a <c>HELLO</c> that does
    /// not decode, a link that drops, an allocation that throws while copying the payload. Without this the slot is gone for the process's lifetime — it is on
    /// no free stack, it belongs to no session, and no tick will ever recycle it because nothing ever closes it.
    /// </para>
    /// <para>
    /// The identity comes back with its generation already bumped, exactly as a recycle stamps it. An identity that was handed out once is never handed out
    /// again, whether or not it became a session, so a reference captured from a failed admission cannot name the client that takes the slot next.
    /// </para>
    /// </remarks>
    public bool ReleaseLease(SessionId session)
    {
        if (!session.IsValid || session.Slot >= _capacity)
        {
            return false;
        }

        if (!TryEnter())
        {
            return false;
        }

        try
        {
            var row = _rows + session.Slot;
            var leased = session.Value | GateLeasedBit;

            // One CAS decides it: only the gate value a lease leaves behind may be released, so the second caller of a double release finds the row free and
            // answers false rather than pushing the identity twice — which would hand one slot to two connections at once.
            if (Interlocked.CompareExchange(ref row->Gate, 0, leased) != leased)
            {
                return false;
            }

            Push(new SessionId(session.Slot, NextGeneration(session.Generation)));
            return true;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Fills a leased slot and publishes it, which is what turns a lease into a session and queues the <see cref="SessionEventKind.Opened"/> event.
    /// </summary>
    /// <param name="session">The leased identity.</param>
    /// <param name="admission">What the application's hook answered.</param>
    /// <param name="sessionKind">The kind the client presented.</param>
    /// <param name="helloPayload">The client's payload; copied, because the transport's buffer is reused.</param>
    /// <exception cref="InvalidOperationException">The identity does not hold a lease — it was never leased, was already opened, or was released.</exception>
    /// <remarks>
    /// Called by the thread that leased the slot, exactly once, and never again — after the release store at the end, the row belongs to the tick. The payload
    /// is copied before anything is written, so an allocation failure leaves the row untouched and the lease still releasable.
    /// </remarks>
    public void Open(SessionId session, in Admission admission, string sessionKind, ReadOnlySpan<byte> helloPayload)
    {
        var slot = session.Slot;
        if (slot >= _capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(session), session.Slot, "Session slot is outside the table.");
        }

        ObjectDisposedException.ThrowIf(!TryEnter(), this);

        try
        {
            var row = _rows + slot;
            var leased = session.Value | GateLeasedBit;
            if (Volatile.Read(ref row->Gate) != leased)
            {
                throw new InvalidOperationException(
                    $"{session} does not hold a lease on its slot. A session is opened exactly once, by the thread that leased it; opening a released or " +
                    "already-open identity would write over a row another connection owns.");
            }

            // Before any row write: a throw here must leave the lease intact and releasable rather than half a session behind.
            byte[] payload = null;
            if (!helloPayload.IsEmpty)
            {
                payload = helloPayload.ToArray();
            }

            var limits = admission.Limits ?? SessionLimits.Default;

            row->State = (int)SessionSlotState.Admitted;
            row->Role = (byte)admission.Role;
            row->Flags = limits.AllowDebug ? SessionRowFlags.AllowDebug : (byte)0;
            row->CloseCode = 0;
            row->CloseReason = 0;
            row->Resumable = 0;
            row->BytesPerSecond = limits.BytesPerSecond;
            row->MaxObservers = Resolve(limits.MaxObservers, _options.ObserversPerSession);
            row->FrameBytes = Resolve(limits.FrameBytes, _options.FrameBytes);
            row->ClientMessageBytes = Resolve(limits.ClientMessageBytes, _options.ClientMessageBytes);
            row->Controlled = EntityId.Null;
            _viewpoints[session.Slot] = default;
            _radii[session.Slot] = 0d;

            _sessionKinds[slot] = sessionKind;
            _profileNames[slot] = null;
            _profileIndices[slot] = -1;
            _closeReasons[slot] = null;
            _appData[slot] = admission.AppData;
            _declaredLimits[slot] = limits;

            // Release: everything above must be visible to the tick before the gate that makes the row reachable. Dropping the lease mark in the same store is
            // what makes "leased" and "open" one state rather than two fields a reader could catch out of step. On arm64 this is the store that costs
            // something, and it is the only one on this path that does.
            if (Interlocked.CompareExchange(ref row->Gate, session.Value, leased) != leased)
            {
                throw new InvalidOperationException(
                    $"{session} lost its lease while it was being opened; a lease is released by its own thread or not at all.");
            }

            _events.Append(new SessionEvent(SessionEventKind.Opened, session, admission.Role, limits, admission.AppData, sessionKind, payload, null,
                SessionId.None, default, 0, false));
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Runs the whole admission sequence for one connecting client: the kind check, the application's hook, then a slot.
    /// </summary>
    /// <param name="sessions">The declaration surface holding the declared kinds and the hook.</param>
    /// <param name="request">What the client presented.</param>
    /// <param name="session">The new session, when it was admitted.</param>
    /// <param name="closeCode">The code to answer a refused client with.</param>
    /// <param name="closeReason">Why, when there is something to say.</param>
    /// <returns><see langword="true"/> when a session now exists.</returns>
    /// <remarks>
    /// <b>The order is deliberate.</b> The kind is checked first, so an unknown name never reaches application code. The hook runs next, so a refusal costs no
    /// slot. Capacity is checked last, because "who may connect" and "how many fit" are different questions and running them the other way round would have the
    /// application answer for the operator's ceiling. A throw out of <see cref="Open"/> gives the lease back before it propagates, because a half-admitted
    /// client that loses its slot for ever is a worse failure than the one that caused it.
    /// </remarks>
    public bool TryAdmit(SubscriptionsSessions sessions, in AdmissionRequest request, out SessionId session, out ushort closeCode, out string closeReason)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var admission = sessions.Decide(request);
        if (!admission.IsAccepted)
        {
            session = SessionId.None;
            closeCode = admission.RejectCode;
            closeReason = admission.RejectReason;
            return false;
        }

        if (!TryLease(out session))
        {
            closeCode = CloseCodes.TryAgainLater;
            closeReason = "server full";
            return false;
        }

        try
        {
            Open(session, admission, request.Kind, request.HelloPayload);
        }
        catch
        {
            ReleaseLease(session);
            session = SessionId.None;
            throw;
        }

        closeCode = 0;
        closeReason = null;
        return true;
    }

    /// <summary>Whether this identity names a session the tick can act on.</summary>
    /// <param name="session">The identity.</param>
    /// <returns><see langword="false"/> for a stale identity, a closing session or an empty slot.</returns>
    public bool IsOpen(SessionId session)
    {
        if (!TryEnter())
        {
            return false;
        }

        try
        {
            return TryGetRow(session, out var row) && Volatile.Read(ref row->State) == (int)SessionSlotState.Open;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Whether this identity still names its row at all, open or closing.</summary>
    /// <param name="session">The identity.</param>
    /// <returns><see langword="false"/> once the slot has been recycled.</returns>
    public bool IsLive(SessionId session)
    {
        if (!TryEnter())
        {
            return false;
        }

        try
        {
            return TryGetRow(session, out _);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Reads a session's row.
    /// </summary>
    /// <param name="session">The identity.</param>
    /// <returns>A copy of the row, taken while the identity was verified.</returns>
    /// <exception cref="InvalidOperationException">The identity does not name a live session — a stale generation, or a recycled slot.</exception>
    /// <remarks>
    /// <b>A copy, not a reference into the table.</b> A <c>ref readonly SessionRow</c> would outlive the identity that justified it: the row is recyclable
    /// native memory, so the reference stays valid as memory and stops describing the caller's session the moment a tick hands the slot to somebody else —
    /// with nothing in the type system marking the moment it changed meaning. The copy is one cache line, taken in the window where the identity is known
    /// good, and it cannot silently become another client's data afterwards.
    /// </remarks>
    public SessionRowView Row(SessionId session)
    {
        ObjectDisposedException.ThrowIf(!TryEnter(), this);

        try
        {
            if (!TryGetRow(session, out var row))
            {
                throw new InvalidOperationException($"{session} is not a live session; its slot has been recycled or it never existed.");
            }

            var view = new SessionRowView(row);

            // Re-validated after the copy: the fields were read one at a time, so an identity that changed during the read would have handed back a mix of two
            // occupants rather than either one.
            if (!TryGetRow(session, out _))
            {
                throw new InvalidOperationException($"{session} was recycled while its row was being read; the slot now belongs to another client.");
            }

            return view;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>The application data admission attached to a session, or <see langword="null"/>.</summary>
    /// <param name="session">The identity.</param>
    /// <returns>The data.</returns>
    public object AppData(SessionId session) => SlotReference(session, _appData);

    /// <summary>The kind the client presented, or <see langword="null"/>.</summary>
    /// <param name="session">The identity.</param>
    /// <returns>The kind.</returns>
    public string SessionKind(SessionId session) => (string)SlotReference(session, _sessionKinds);

    /// <summary>The profile a request bound this session to, or <see langword="null"/>.</summary>
    /// <param name="session">The identity.</param>
    /// <returns>The profile's name.</returns>
    public string ProfileName(SessionId session) => (string)SlotReference(session, _profileNames);

    /// <summary>
    /// The index of the profile a session is bound to among the compiled profiles, or -1 when it has none, names no declared profile, or no profiles are
    /// bound yet. Tick side, like <see cref="TryGetViewpoint"/>: a profile is set only from the request log, on the tick.
    /// </summary>
    /// <param name="session">The identity; its slot is read without a generation check, as the tick's session enumeration hands out open ones.</param>
    /// <returns>The index.</returns>
    public int ProfileIndex(SessionId session) => session.Slot < (uint)_capacity ? _profileIndices[session.Slot] : -1;

    /// <summary>
    /// Binds the name → index map of the compiled profiles, and resolves the names already set. Once, at <c>Start</c>, before the first tick.
    /// </summary>
    /// <param name="resolve">The index of a profile name, or -1 for a name no profile declares.</param>
    public void BindProfiles(Func<string, int> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _profileResolver = resolve;
        for (var slot = 0; slot < _capacity; slot++)
        {
            var name = _profileNames[slot];
            _profileIndices[slot] = name == null ? -1 : resolve(name);
        }
    }

    /// <summary>The reason text a close carried, for the <c>KICK</c> the transport sends. <see langword="null"/> when there was none.</summary>
    /// <param name="session">The identity.</param>
    /// <returns>The reason.</returns>
    public string CloseDetail(SessionId session) => (string)SlotReference(session, _closeReasons);

    /// <summary>
    /// Reads one of the slot-keyed managed arrays for a session, with the identity checked on both sides of the read.
    /// </summary>
    /// <param name="session">The identity.</param>
    /// <param name="bySlot">The array, indexed by slot.</param>
    /// <returns>The reference this session put there, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The arrays are keyed by SLOT, and a slot outlives its occupant. Checking the identity only before the read leaves the caller holding whatever the next
    /// client put there — or <see langword="null"/>, from a recycle that ran in between. The second check is what makes the answer belong to the identity that
    /// was asked about; the element load is a volatile one so it cannot sink past it on arm64, where an acquire load alone does not hold earlier plain reads
    /// above it.
    /// </remarks>
    private object SlotReference(SessionId session, object[] bySlot)
    {
        if (!TryEnter())
        {
            return null;
        }

        try
        {
            if (!TryGetRow(session, out _))
            {
                return null;
            }

            var value = Volatile.Read(ref bySlot[session.Slot]);
            return TryGetRow(session, out _) ? value : null;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Places a session's observer, for this tick.
    /// </summary>
    /// <param name="session">The identity.</param>
    /// <param name="position">Where the session is looking from, in world space.</param>
    /// <returns><see langword="false"/> when the session is gone.</returns>
    /// <remarks>
    /// <para>
    /// <b>Applied immediately, not staged.</b> Everything else a session asks for is a configuration change — a profile, a budget, the entity it controls —
    /// and those are staged by the request log and applied in the next tick's prologue, which is right for something that should be in force from a known
    /// boundary. A viewpoint is not configuration: it is this tick's position, and a tick of latency on it means every session resolves its interest around
    /// where it was, which at 12 m/s and 10 Hz is more than a metre of lag in the enter and leave decisions.
    /// </para>
    /// <para>
    /// <b>Tick-side, one writer, which is what makes the plain store legal (SUB-05).</b> An application system writing this runs on the tick, before the
    /// replication track reads it in the same tick. It is not on a transport thread's allow-list and must never be called from one.
    /// </para>
    /// </remarks>
    public bool SetViewpoint(SessionId session, Vector3D position)
    {
        if (!TryEnter())
        {
            return false;
        }

        _affinity.Enter(nameof(SessionTable), nameof(SetViewpoint));
        try
        {
            if (!TryGetRow(session, out _))
            {
                return false;
            }

            _viewpoints[session.Slot] = new SessionViewpoint(position, true);
            return true;
        }
        finally
        {
            _affinity.Exit();
            Exit();
        }
    }

    /// <summary>
    /// Sets a session's Sphere radius for this tick on. Tick side, from <see cref="SubscriptionsCommands.SetRadius"/>, which checked it against the profile.
    /// </summary>
    /// <param name="session">The identity.</param>
    /// <param name="radius">The radius, in metres; 0 returns to the profile's own.</param>
    /// <returns><see langword="false"/> when the session is closing or gone.</returns>
    public bool SetRadius(SessionId session, double radius)
    {
        if (!TryEnter())
        {
            return false;
        }

        _affinity.Enter(nameof(SessionTable), nameof(SetRadius));
        try
        {
            if (!TryGetOpenRow(session, out _))
            {
                return false;
            }

            _radii[session.Slot] = radius;
            return true;
        }
        finally
        {
            _affinity.Exit();
            Exit();
        }
    }

    /// <summary>A session's run-time Sphere radius, or 0 for its profile's own. Tick side; the slot is read without a generation check, like the viewpoint.</summary>
    /// <param name="session">The identity.</param>
    /// <returns>The radius.</returns>
    public double Radius(SessionId session) => session.Slot < (uint)_capacity ? _radii[session.Slot] : 0d;

    /// <summary>
    /// Reads a session's viewpoint.
    /// </summary>
    /// <param name="session">The identity.</param>
    /// <param name="position">Where it is looking from.</param>
    /// <returns><see langword="false"/> when the session has never been placed, which is what a spatial observer treats as "sees nothing yet".</returns>
    public bool TryGetViewpoint(SessionId session, out Vector3D position)
    {
        if (session.Slot >= (uint)_capacity)
        {
            position = default;
            return false;
        }

        var slot = _viewpoints[session.Slot];
        position = slot.Position;
        return slot.IsPlaced;
    }

    /// <summary>
    /// Ends a session: the row is marked and a <see cref="SessionEventKind.Closed"/> event is queued for the next tick to deliver.
    /// </summary>
    /// <param name="session">The identity.</param>
    /// <param name="reason">Why.</param>
    /// <param name="code">The close code the client is given.</param>
    /// <param name="detail">An optional reason string for the <c>KICK</c>.</param>
    /// <param name="resumable">Whether the client may come back within the grace period.</param>
    /// <returns><see langword="false"/> when the session was already closing or gone, so a second close is a no-op rather than a second event.</returns>
    /// <remarks>Tick side only. A transport thread asks for a close through <see cref="RequestClose"/> instead.</remarks>
    public bool Close(SessionId session, SessionCloseReason reason, ushort code, string detail = null, bool resumable = false)
    {
        if (!TryEnter())
        {
            return false;
        }

        _affinity.Enter(nameof(SessionTable), nameof(Close));
        try
        {
            return CloseCore(session, reason, code, detail, resumable);
        }
        finally
        {
            _affinity.Exit();
            Exit();
        }
    }

    /// <summary>The close itself, without the guard, so the tick-side callers that already hold it do not have to give it up.</summary>
    /// <param name="session">The identity.</param>
    /// <param name="reason">Why.</param>
    /// <param name="code">The close code.</param>
    /// <param name="detail">An optional reason string.</param>
    /// <param name="resumable">Whether the client may come back.</param>
    /// <returns><see langword="false"/> when the session was already closing or gone.</returns>
    /// <remarks>
    /// The state store is a RELEASE, and it is the last thing this does to the row. A transport thread builds its <c>KICK</c> from the code, the reason and the
    /// resumable flag after it sees <see cref="SessionSlotState.Closing"/> through an acquire; publishing the state plainly would let that thread read three
    /// fields the tick had not written yet and send a client a close it never asked for.
    /// </remarks>
    private bool CloseCore(SessionId session, SessionCloseReason reason, ushort code, string detail, bool resumable)
    {
        if (!TryGetRow(session, out var row))
        {
            return false;
        }

        var state = (SessionSlotState)Volatile.Read(ref row->State);
        if (state is not (SessionSlotState.Admitted or SessionSlotState.Open))
        {
            return false;
        }

        row->CloseCode = code;
        row->CloseReason = (byte)reason;
        row->Resumable = resumable ? (byte)1 : (byte)0;
        _closeReasons[session.Slot] = detail;

        Volatile.Write(ref row->State, (int)SessionSlotState.Closing);

        _events.Append(new SessionEvent(SessionEventKind.Closed, session, (SessionRole)row->Role, _declaredLimits[session.Slot],
            _appData[session.Slot], _sessionKinds[session.Slot], null, null, SessionId.None, reason, code, resumable));
        return true;
    }

    /// <summary>
    /// Asks, from any thread, that a session be closed. The tick performs the close.
    /// </summary>
    /// <param name="session">The identity.</param>
    /// <param name="reason">Why.</param>
    /// <param name="code">The close code.</param>
    /// <returns><see langword="false"/> when the session is already gone or a close was already requested.</returns>
    /// <remarks>
    /// <para>
    /// It carries no string. That is the whole reason this member exists separately from <see cref="Close"/>: a code and a reason enum pack into the gate a
    /// transport thread publishes with a single compare-exchange, while a managed string would be a second write the tick could read before the first — a row
    /// written by two threads, which is exactly what the single-writer rule forbids.
    /// </para>
    /// <para>
    /// The identity travels in the same word, so the check and the publication are one step. Validating the generation and then compare-exchanging a separate
    /// word lets a recycle land in between and close the slot's NEXT occupant with the previous client's code and reason.
    /// </para>
    /// </remarks>
    public bool RequestClose(SessionId session, SessionCloseReason reason, ushort code)
    {
        if (!session.IsValid || session.Slot >= _capacity)
        {
            return false;
        }

        Debug.Assert((byte)reason <= GateCloseReasonMax, "a close reason must fit the gate's four bits");

        if (!TryEnter())
        {
            return false;
        }

        try
        {
            var row = _rows + session.Slot;
            while (true)
            {
                var observed = Volatile.Read(ref row->Gate);
                if (!GateNames(observed, session) || (observed & GateCloseRequestedBit) != 0)
                {
                    return false;
                }

                var next = (observed & ~GateCloseMask)
                    | GateCloseRequestedBit
                    | (((byte)reason & GateCloseReasonMax) << GateCloseReasonShift)
                    | ((long)code << GateCloseCodeShift);

                if (Interlocked.CompareExchange(ref row->Gate, next, observed) == observed)
                {
                    return true;
                }
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Turns every close a transport thread asked for into a real close. Tick side, once per tick.</summary>
    /// <returns>How many sessions were closed.</returns>
    /// <remarks>
    /// The request is cleared with a compare-exchange, not a store: the word it lives in is one transport threads move with their own compare-exchanges, so a
    /// plain zero here would drop a send that started in the same instant and take the slot's send counter with it.
    /// </remarks>
    public int ApplyPendingCloses()
    {
        if (!TryEnter())
        {
            return 0;
        }

        _affinity.Enter(nameof(SessionTable), nameof(ApplyPendingCloses));
        var closed = 0;
        try
        {
            for (var i = _openSlots.Count - 1; i >= 0; i--)
            {
                var slot = _openSlots[i];
                var row = _rows + slot;
                var observed = Volatile.Read(ref row->Gate);
                if ((observed & GateCloseRequestedBit) == 0)
                {
                    continue;
                }

                var session = SessionId.FromValue(GateId(observed));
                var reason = (SessionCloseReason)((observed >> GateCloseReasonShift) & GateCloseReasonMax);
                var code = (ushort)(observed >> GateCloseCodeShift);

                while (true)
                {
                    var current = Volatile.Read(ref row->Gate);
                    if ((current & GateCloseRequestedBit) == 0)
                    {
                        break;
                    }

                    if (Interlocked.CompareExchange(ref row->Gate, current & ~GateCloseMask, current) == current)
                    {
                        break;
                    }
                }

                if (CloseCore(session, reason, code, null, false))
                {
                    closed++;
                }
            }

            return closed;
        }
        finally
        {
            _affinity.Exit();
            Exit();
        }
    }

    /// <summary>
    /// Advances the table one tick: recycles what may be recycled, then hands this tick's events over and folds them back into the table's own bookkeeping.
    /// </summary>
    /// <returns>How many events this tick carries.</returns>
    /// <remarks>
    /// <para>
    /// The order is the invariant. Recycling runs <i>before</i> the swap, so a slot is never freed in the same tick its <c>Closed</c> event is visible: an
    /// application system reading that event this tick can still look the session up, and only the tick after does the slot come back.
    /// </para>
    /// <para>
    /// The open list is rebuilt from the delivered events rather than by scanning, which is what keeps a tick's cost proportional to the sessions that changed
    /// instead of to <see cref="SubscriptionsOptions.MaxSessions"/>.
    /// </para>
    /// <para>
    /// <b>The batch this walks was published by a monitor.</b> <see cref="SessionEvents.BeginTick"/> takes the events' lock, so everything a transport thread
    /// appended is visible here without any further ordering. The row reads below do NOT get their ordering from that lock — an <c>Opened</c> event and the
    /// gate that publishes its row are written by the same thread but read here separately — so they are acquire loads on their own account. Relying on the
    /// monitor for those would be an invisible coupling: moving event delivery off the lock would silently make this read torn rows.
    /// </para>
    /// </remarks>
    public int BeginTick()
    {
        if (!TryEnter())
        {
            return 0;
        }

        _affinity.Enter(nameof(SessionTable), nameof(BeginTick));
        try
        {
            RecycleDelivered();

            var count = _events.BeginTick();
            var batch = _events.AsSpan();
            for (var i = 0; i < batch.Length; i++)
            {
                ref readonly var e = ref batch[i];
                var slot = e.Session.Slot;
                var row = _rows + slot;
                if (!GateNames(Volatile.Read(ref row->Gate), e.Session))
                {
                    continue;
                }

                var state = Volatile.Read(ref row->State);
                switch (e.Kind)
                {
                    case SessionEventKind.Opened:
                        if (state == (int)SessionSlotState.Admitted)
                        {
                            Volatile.Write(ref row->State, (int)SessionSlotState.Open);
                            AddOpen(slot);
                        }

                        break;

                    case SessionEventKind.Closed:
                        if (state == (int)SessionSlotState.Closing)
                        {
                            Volatile.Write(ref row->State, (int)SessionSlotState.ClosedDelivered);
                            RemoveOpen(slot);
                            _closedLastTick.Add(slot);
                        }

                        break;
                }
            }

            return count;
        }
        finally
        {
            _affinity.Exit();
            Exit();
        }
    }

    /// <summary>Marks a frame as handed to a link. Callable from any thread; it is what keeps the slot alive while bytes are still moving.</summary>
    /// <param name="session">The identity.</param>
    /// <returns><see langword="false"/> when the session is gone, so the caller drops the frame instead of sending it.</returns>
    /// <remarks>
    /// The identity is validated by the same compare-exchange that moves the counter. Checking first and incrementing after is the interleaving that loses a
    /// slot for ever: a recycle between the two puts the increment on the next occupant, whose own publication then resets the counter under it, and the
    /// correcting decrement takes it negative — after which no recycle ever passes its "no send in flight" test again.
    /// </remarks>
    public bool BeginSend(SessionId session)
    {
        if (!session.IsValid || session.Slot >= _capacity)
        {
            return false;
        }

        if (!TryEnter())
        {
            return false;
        }

        try
        {
            var row = _rows + session.Slot;
            while (true)
            {
                var observed = Volatile.Read(ref row->Gate);
                if (!GateNames(observed, session))
                {
                    return false;
                }

                var sends = GateSends(observed);
                if (sends == MaxSendsInFlight)
                {
                    // Refused rather than wrapped: a wrapped counter reads as zero and lets a tick recycle the slot under the frames still on the link.
                    return false;
                }

                if (Interlocked.CompareExchange(ref row->Gate, WithSends(observed, sends + 1), observed) == observed)
                {
                    return true;
                }
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Marks a frame as finished with. Callable from any thread.</summary>
    /// <param name="session">The identity.</param>
    public void EndSend(SessionId session)
    {
        if (!session.IsValid || session.Slot >= _capacity)
        {
            return;
        }

        if (!TryEnter())
        {
            return;
        }

        try
        {
            var row = _rows + session.Slot;
            while (true)
            {
                var observed = Volatile.Read(ref row->Gate);

                // The row cannot have been recycled under a matched BeginSend: a recycle refuses while the counter is non-zero, and the counter and the
                // identity move in the same word. An unmatched EndSend is a caller bug, and answering it with nothing is better than a negative counter.
                if (!GateNames(observed, session) || GateSends(observed) == 0)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref row->Gate, WithSends(observed, GateSends(observed) - 1), observed) == observed)
                {
                    return;
                }
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Sends still on a link for this session.</summary>
    /// <param name="session">The identity.</param>
    /// <returns>The count, or zero for a slot that is gone.</returns>
    public int SendsInFlight(SessionId session)
    {
        if (!session.IsValid || session.Slot >= _capacity || !TryEnter())
        {
            return 0;
        }

        try
        {
            var observed = Volatile.Read(ref (_rows + session.Slot)->Gate);
            return GateNames(observed, session) ? GateSends(observed) : 0;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Binds a session to a declared profile. Tick side, from the request log.</summary>
    /// <param name="session">The identity.</param>
    /// <param name="profileName">The profile's name.</param>
    /// <returns><see langword="false"/> when the session is no longer open.</returns>
    public bool SetProfile(SessionId session, string profileName)
    {
        if (!TryEnter())
        {
            return false;
        }

        try
        {
            if (!TryGetOpenRow(session, out _))
            {
                return false;
            }

            _profileNames[session.Slot] = profileName;
            _profileIndices[session.Slot] = _profileResolver == null || profileName == null ? -1 : _profileResolver(profileName);
            _radii[session.Slot] = 0d;
            return true;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Binds a session to the entity it controls. Tick side, from the request log.</summary>
    /// <param name="session">The identity.</param>
    /// <param name="entity">The entity, or <see cref="EntityId.Null"/> to release.</param>
    /// <returns><see langword="false"/> when the session is no longer open.</returns>
    public bool SetControlled(SessionId session, EntityId entity)
    {
        if (!TryEnter())
        {
            return false;
        }

        try
        {
            if (!TryGetOpenRow(session, out var row))
            {
                return false;
            }

            row->Controlled = entity;
            return true;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>The entity a session controls, or <see cref="EntityId.Null"/>. Tick side, like the viewpoint.</summary>
    /// <param name="session">The identity.</param>
    /// <returns>The entity.</returns>
    /// <remarks>
    /// No gate: it is read once per followed session in the frame prologue, and the gate's counter is shared with the transport threads' sends, so taking it
    /// here bounced its line on every session. The row's identity is checked with the acquire load <see cref="TryGetRow"/> makes, and <c>Controlled</c> is
    /// written only on the tick.
    /// </remarks>
    public EntityId ControlledOf(SessionId session) => TryGetRow(session, out var row) ? row->Controlled : EntityId.Null;

    /// <summary>A session's outbound byte budget, 0 for none. Tick side, without the gate, as <see cref="ControlledOf"/>: written only on the tick.</summary>
    /// <param name="session">The identity.</param>
    /// <returns>The budget in bytes per second.</returns>
    public int BudgetOf(SessionId session) => TryGetRow(session, out var row) ? row->BytesPerSecond : 0;

    /// <summary>Sets a session's outbound byte budget. Tick side, from the request log.</summary>
    /// <param name="session">The identity.</param>
    /// <param name="bytesPerSecond">The budget; zero removes it.</param>
    /// <returns><see langword="false"/> when the session is no longer open.</returns>
    public bool SetBudget(SessionId session, int bytesPerSecond)
    {
        if (!TryEnter())
        {
            return false;
        }

        try
        {
            if (!TryGetOpenRow(session, out var row))
            {
                return false;
            }

            row->BytesPerSecond = bytesPerSecond;
            return true;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// The generation a slot carries the next time it is handed out.
    /// </summary>
    /// <param name="generation">The generation the slot carried.</param>
    /// <returns>The next one, never zero.</returns>
    /// <remarks>
    /// Zero is skipped on the wrap: a zero generation is what makes a default <see cref="SessionId"/> mean "none", so issuing one would make
    /// <see langword="default"/> name a live session. Internal and static so the wrap can be tested directly — reaching it through the table would take 65 536
    /// admissions of the same slot.
    /// </remarks>
    internal static ushort NextGeneration(ushort generation)
    {
        var next = (ushort)(generation + 1);
        return next == 0 ? (ushort)1 : next;
    }

    /// <summary>Finds the row behind an identity, checking the generation.</summary>
    /// <param name="session">The identity.</param>
    /// <param name="row">The row, or <see langword="null"/>.</param>
    /// <returns><see langword="false"/> for a stale or unknown identity.</returns>
    /// <remarks>Callers hold the caller gate, so the block is alive for the whole of the call.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetRow(SessionId session, out SessionRow* row)
    {
        var slot = session.Slot;
        if (slot >= _capacity || !session.IsValid)
        {
            row = null;
            return false;
        }

        var candidate = _rows + slot;

        // Acquire: pairs with Open's release store, so a reader that sees this identity also sees every field written before it was published. A free row's
        // gate is zero and a leased one carries the lease mark, so the identity alone answers "is this a session" — there is no second field to read.
        if (!GateNames(Volatile.Read(ref candidate->Gate), session))
        {
            row = null;
            return false;
        }

        row = candidate;
        return true;
    }

    private bool TryGetOpenRow(SessionId session, out SessionRow* row)
        => TryGetRow(session, out row) && Volatile.Read(ref row->State) == (int)SessionSlotState.Open;

    private void RecycleDelivered()
    {
        for (var i = _closedLastTick.Count - 1; i >= 0; i--)
        {
            var slot = _closedLastTick[i];
            if (!TryRecycle(slot))
            {
                _awaitingSends.Add(slot);
            }
        }

        _closedLastTick.Clear();

        for (var i = _awaitingSends.Count - 1; i >= 0; i--)
        {
            if (TryRecycle(_awaitingSends[i]))
            {
                _awaitingSends.RemoveAt(i);
            }
        }
    }

    /// <summary>Gives a slot back, with the next generation on it.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns><see langword="false"/> while a send is still in flight; the slot is retried next tick.</returns>
    /// <remarks>
    /// The identity is cleared by the same compare-exchange that observes an idle counter, so a send that starts in the same instant either wins — and the
    /// recycle sees a non-zero count and waits a tick — or loses, and finds the identity already gone. Clearing the identity first and the side arrays after is
    /// deliberate: a reader mid-flight sees the arrays empty and its second identity check fails, which is the answer it wants.
    /// </remarks>
    private bool TryRecycle(int slot)
    {
        var row = _rows + slot;
        long observed;
        while (true)
        {
            observed = Volatile.Read(ref row->Gate);
            if (GateSends(observed) != 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref row->Gate, 0, observed) == observed)
            {
                break;
            }
        }

        var generation = NextGeneration(SessionId.FromValue(GateId(observed)).Generation);

        _sessionKinds[slot] = null;
        _profileNames[slot] = null;
        _profileIndices[slot] = -1;
        _closeReasons[slot] = null;
        _appData[slot] = null;
        _declaredLimits[slot] = null;

        row->Controlled = EntityId.Null;
        Volatile.Write(ref row->State, (int)SessionSlotState.Free);

        Push(new SessionId((ushort)slot, generation));
        return true;
    }

    /// <summary>
    /// Returns an identity to the free stack.
    /// </summary>
    /// <param name="session">The identity, already stamped with its next generation.</param>
    /// <remarks>
    /// Pushes are serialized by one word, pops are not. The entry is written before the count is published, so two pushers racing would write the same index
    /// and one identity would be lost while the other was published twice — one slot handed to two connections. That was harmless while only the tick pushed;
    /// <see cref="ReleaseLease"/> makes a transport thread a pusher too, so the push takes the lock and the pop stays lock-free. A push happens once per
    /// disconnection or failed handshake, which is nowhere near any hot path.
    /// </remarks>
    private void Push(SessionId session)
    {
        var spin = new SpinWait();
        while (Interlocked.CompareExchange(ref *_pushLock, 1, 0) != 0)
        {
            spin.SpinOnce();
        }

        try
        {
            while (true)
            {
                var packed = Volatile.Read(ref *_freeHead);
                var top = (int)(uint)packed;
                Volatile.Write(ref _freeIds[top], session.Value);

                var next = Pack((ulong)packed >> 32, top + 1);
                if (Interlocked.CompareExchange(ref *_freeHead, next, packed) == packed)
                {
                    return;
                }
            }
        }
        finally
        {
            Volatile.Write(ref *_pushLock, 0);
        }
    }

    /// <summary>Packs the free stack's stamp and entry count. The stamp rises on every operation, which is what makes a pop immune to ABA.</summary>
    /// <param name="stamp">The current stamp; it is incremented here.</param>
    /// <param name="count">The new entry count.</param>
    /// <returns>The packed word.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Pack(ulong stamp, int count) => unchecked((long)(((stamp + 1) << 32) | (uint)count));

    private void AddOpen(int slot)
    {
        _openIndexBySlot[slot] = _openSlots.Count;
        _openSlots.Add(slot);
        _openCount = _openSlots.Count;
    }

    private void RemoveOpen(int slot)
    {
        var index = _openIndexBySlot[slot];
        if (index < 0)
        {
            return;
        }

        var last = _openSlots.Count - 1;
        var moved = _openSlots[last];
        _openSlots[index] = moved;
        _openIndexBySlot[moved] = index;
        _openSlots.RemoveAt(last);
        _openIndexBySlot[slot] = -1;
        _openCount = _openSlots.Count;
    }

    private static int Resolve(int requested, int operatorValue)
        => requested <= 0 ? operatorValue : Math.Min(requested, operatorValue);

    // ── the gate ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Set while a slot is leased and not yet opened: the identity exists, but no session does.</summary>
    private const long GateLeasedBit = 1L << 61;

    /// <summary>Set when a non-tick thread asked for a close. It is what makes a request carrying close code zero distinguishable from none.</summary>
    private const long GateCloseRequestedBit = 1L << 60;

    private const int GateSendsShift = 32;
    private const int GateCloseCodeShift = 40;
    private const int GateCloseReasonShift = 56;
    private const long GateSendsMask = 0xFFL << GateSendsShift;
    private const long GateCloseReasonMax = 0xF;
    private const long GateCloseMask = (0xFFFFL << GateCloseCodeShift) | (GateCloseReasonMax << GateCloseReasonShift) | GateCloseRequestedBit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GateId(long gate) => (uint)gate;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GateSends(long gate) => (int)((gate >> GateSendsShift) & 0xFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long WithSends(long gate, int sends) => (gate & ~GateSendsMask) | ((long)sends << GateSendsShift);

    /// <summary>Whether a gate word names this session — the identity matches and the slot is past its lease.</summary>
    /// <param name="gate">The observed gate.</param>
    /// <param name="session">The identity.</param>
    /// <returns><see langword="true"/> when the row is that session's.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GateNames(long gate, SessionId session) => GateId(gate) == session.Value && (gate & GateLeasedBit) == 0;

    // ── the caller gate ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The sign bit of <see cref="_callers"/>: set once <see cref="Dispose"/> has latched, never cleared.</summary>
    private const int DisposingBit = unchecked((int)0x8000_0000);

    /// <summary>Whether this table has been disposed.</summary>
    internal bool IsDisposed => Volatile.Read(ref _callers.Value) < 0;

    /// <summary>Takes a reference on the native block for the duration of one call.</summary>
    /// <returns><see langword="false"/> once disposal has latched; the caller answers as it would for a slot that is gone.</returns>
    private bool TryEnter()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _callers.Value);
            if (observed < 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _callers.Value, observed + 1, observed) == observed)
            {
                return true;
            }
        }
    }

    /// <summary>Drops the reference taken by <see cref="TryEnter"/>. The count lives in the low bits, so this never borrows into the latch.</summary>
    private void Exit() => Interlocked.Decrement(ref _callers.Value);

    /// <inheritdoc />
    /// <remarks>
    /// <b>Quiesce, then free.</b> <see cref="TryLease"/>, <see cref="BeginSend"/>, <see cref="EndSend"/> and <see cref="RequestClose"/> are callable from any
    /// thread, so freeing the block on a "not disposed yet" flag read would be a race with a real use-after-free on the other side of it. The latch stops new
    /// callers, the wait lets the ones already inside finish, and the pointers are nulled so a bug that reaches past the gate faults immediately instead of
    /// reading freed memory that still looks like rows.
    /// </remarks>
    public void Dispose()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _callers.Value);
            if (observed < 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _callers.Value, observed | DisposingBit, observed) == observed)
            {
                break;
            }
        }

        var spin = new SpinWait();
        while ((Volatile.Read(ref _callers.Value) & ~DisposingBit) != 0)
        {
            spin.SpinOnce();
        }

        _openSlots.Clear();
        _closedLastTick.Clear();
        _awaitingSends.Clear();
        _openCount = 0;
        _events.Clear();

        Array.Clear(_sessionKinds);
        Array.Clear(_profileNames);
        Array.Fill(_profileIndices, -1);
        Array.Clear(_closeReasons);
        Array.Clear(_appData);
        Array.Clear(_declaredLimits);

        var memory = _memory;
        _memory = null;
        _rows = null;
        _freeIds = null;
        _freeHead = null;
        _pushLock = null;

        memory?.Dispose();
    }

    /// <summary>The identity occupying a slot right now, or <see cref="SessionId.None"/>.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The identity.</returns>
    internal SessionId IdAt(int slot)
    {
        var rows = _rows;
        return rows == null ? SessionId.None : SessionId.FromValue(GateId(Volatile.Read(ref (rows + slot)->Gate)));
    }

    /// <summary>Walks this tick's open sessions.</summary>
    internal struct OpenSessionEnumerator
    {
        private readonly SessionTable _table;
        private int _index;

        internal OpenSessionEnumerator(SessionTable table)
        {
            _table = table;
            _index = -1;
        }

        /// <summary>The session at the cursor.</summary>
        public SessionId Current => _table.IdAt(_table._openSlots[_index]);

        /// <summary>Advances the cursor.</summary>
        /// <returns><see langword="false"/> at the end.</returns>
        public bool MoveNext() => ++_index < _table._openSlots.Count;
    }
}

/// <summary>Where a slot is in its life.</summary>
internal enum SessionSlotState
{
    /// <summary>Nobody holds it. Its identity is on the free stack.</summary>
    Free = 0,

    /// <summary>Filled and published by the admitting thread; its <c>Opened</c> event has not been delivered yet.</summary>
    Admitted = 1,

    /// <summary>Live, and visible to the tick's stages.</summary>
    Open = 2,

    /// <summary>Closed; its <c>Closed</c> event is queued but not yet delivered.</summary>
    Closing = 3,

    /// <summary>Its <c>Closed</c> event was delivered. The slot comes back once no send is in flight.</summary>
    ClosedDelivered = 4,
}

/// <summary>Bit flags carried in <see cref="SessionRow.Flags"/>.</summary>
internal static class SessionRowFlags
{
    /// <summary>Admission authorized the <c>DEBUG</c> capability for this session.</summary>
    internal const byte AllowDebug = 1 << 0;
}

/// <summary>
/// One session, as the engine stores it: exactly one cache line of native memory, so a tick stage walking sessions touches one line per session and two
/// sessions never share one.
/// </summary>
/// <remarks>
/// <b>One field is written by threads other than the tick</b>: <see cref="Gate"/>, and only ever with a compare-exchange. It carries the identity, the
/// send counter and any close a transport thread asked for, in that one word, so a transport thread can never move a counter without agreeing about which
/// session it belongs to. Everything else is the tick's. The gate is also the publication point — written with a release store when a leased row becomes a
/// session and read with an acquire load by every lookup — and <see cref="State"/> is republished the same way on every transition the transport side reads.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = SessionTable.RowBytes)]
internal struct SessionRow
{
    /// <summary>
    /// Identity, send counter and pending close, in one compare-exchange target: identity in bits 0-31, sends in flight in 32-39, a requested close's code in
    /// 40-55 and its reason in 56-59, the close-requested flag at 60 and the lease mark at 61. Zero means the slot is free.
    /// </summary>
    public long Gate;

    /// <summary>The slot's <see cref="SessionSlotState"/>. Written with a release store on every transition a transport thread can observe.</summary>
    public int State;

    /// <summary>The session's <see cref="SessionRole"/>.</summary>
    public byte Role;

    /// <summary><see cref="SessionRowFlags"/>.</summary>
    public byte Flags;

    /// <summary>The close code, once it is closing.</summary>
    public ushort CloseCode;

    /// <summary>The <see cref="SessionCloseReason"/>, once it is closing.</summary>
    public byte CloseReason;

    /// <summary>Whether the client may resume within the grace period.</summary>
    public byte Resumable;

    /// <summary>The outbound byte budget; zero means none.</summary>
    public int BytesPerSecond;

    /// <summary>How many observers this session may hold.</summary>
    public int MaxObservers;

    /// <summary>The largest frame it may be sent, resolved against the operator's ceiling.</summary>
    public int FrameBytes;

    /// <summary>The largest message it may send, resolved against the operator's ceiling.</summary>
    public int ClientMessageBytes;

    /// <summary>The entity this session controls, or <see cref="EntityId.Null"/>.</summary>
    public EntityId Controlled;

    // Size pads the declared fields to a full cache line. The tail is deliberate headroom for the per-session state later slices add, so that adding
    // one does not silently take a session across two lines.

    /// <summary>The identity occupying this slot, packed. Zero when free. A view onto <see cref="Gate"/>, which is where it lives.</summary>
    public readonly uint IdValue => (uint)Gate;

    /// <summary>Frames handed to a link and not yet finished. A view onto <see cref="Gate"/>.</summary>
    public readonly int SendsInFlight => (int)((Gate >> 32) & 0xFF);

    /// <summary>
    /// A close a non-tick thread asked for — the flag, the reason and the code — as one number; zero when none was asked for. A view onto <see cref="Gate"/>.
    /// </summary>
    public readonly int PendingClose => (int)((Gate >> 40) & 0x1FFFFF);
}

/// <summary>
/// A session's row as a caller reads it: a copy, taken while the identity was verified on both sides of the read.
/// </summary>
/// <remarks>
/// <b>Why a copy and not a reference.</b> The rows are recyclable native memory. A <c>ref readonly SessionRow</c> handed out of the table stays a valid
/// reference long after it stops describing the session that asked for it, because the slot is handed to the next client and the memory does not move. There
/// is no lifetime in the type system to catch that, and the failure it produces is a caller reading another client's limits with no error anywhere. The copy
/// is one cache line and it is taken inside the table, where the identity is known good.
/// </remarks>
internal readonly struct SessionRowView
{
    internal unsafe SessionRowView(SessionRow* row)
    {
        // The gate and the state are acquire loads: every plain field below was written before one of them was released, and a volatile load is program-ordered
        // against the loads that follow it, which is what keeps this copy from mixing a new occupant's identity with the previous one's limits.
        var gate = Volatile.Read(ref row->Gate);

        IdValue = (uint)gate;
        SendsInFlight = (int)((gate >> 32) & 0xFF);
        PendingClose = (int)((gate >> 40) & 0x1FFFFF);
        State = Volatile.Read(ref row->State);
        BytesPerSecond = row->BytesPerSecond;
        MaxObservers = row->MaxObservers;
        FrameBytes = row->FrameBytes;
        ClientMessageBytes = row->ClientMessageBytes;
        Controlled = row->Controlled;
        CloseCode = row->CloseCode;
        Role = row->Role;
        Flags = row->Flags;
        CloseReason = row->CloseReason;
        Resumable = row->Resumable;
    }

    /// <summary>The <see cref="SessionId"/> occupying the slot, packed.</summary>
    public uint IdValue { get; }

    /// <summary>Frames handed to the link and not yet finished.</summary>
    public int SendsInFlight { get; }

    /// <summary>A close a non-tick thread asked for — the flag, the reason and the code — as one number; zero when none was asked for.</summary>
    public int PendingClose { get; }

    /// <summary>Where the slot is in its life, as the row stores it.</summary>
    public int State { get; }

    /// <summary>Where the slot is in its life.</summary>
    public SessionSlotState SlotState => (SessionSlotState)State;

    /// <summary>The outbound byte budget; zero means none.</summary>
    public int BytesPerSecond { get; }

    /// <summary>How many observers this session may hold.</summary>
    public int MaxObservers { get; }

    /// <summary>The largest frame it may be sent, resolved against the operator's ceiling.</summary>
    public int FrameBytes { get; }

    /// <summary>The largest message it may send, resolved against the operator's ceiling.</summary>
    public int ClientMessageBytes { get; }

    /// <summary>The entity this session controls, or <see cref="EntityId.Null"/>.</summary>
    public EntityId Controlled { get; }

    /// <summary>The close code, once it is closing.</summary>
    public ushort CloseCode { get; }

    /// <summary>The session's <see cref="SessionRole"/>.</summary>
    public byte Role { get; }

    /// <summary><see cref="SessionRowFlags"/>.</summary>
    public byte Flags { get; }

    /// <summary>The <see cref="SessionCloseReason"/>, once it is closing.</summary>
    public byte CloseReason { get; }

    /// <summary>Whether the client may resume within the grace period.</summary>
    public byte Resumable { get; }

    /// <summary>Whether a transport thread has asked for this session to be closed and the tick has not applied it yet.</summary>
    public bool CloseRequested { get; }

    /// <summary>The identity, unpacked.</summary>
    public SessionId Session => SessionId.FromValue(IdValue);
}

/// <summary>Where a session's spatial observers are centred, and whether it has ever been placed.</summary>
/// <param name="Position">The world-space centre.</param>
/// <param name="IsPlaced">Whether an application has placed this session; a session that has not is not "at the origin", it is nowhere.</param>
/// <remarks>
/// The flag is the whole point. A default <see cref="Vector3D"/> is a legal world position, so a spatial observer that could not tell "never placed" from
/// "placed at zero" would give every unplaced session a sphere around the origin — which in a world whose origin is populated is a large view nobody asked
/// for, and in one whose origin is empty is an empty view that looks like a bug in the query.
/// </remarks>
internal readonly record struct SessionViewpoint(Vector3D Position, bool IsPlaced);
