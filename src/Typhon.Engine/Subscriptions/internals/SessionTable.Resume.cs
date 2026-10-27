using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// One session's resume record: what a client that comes back within the grace period presents, and what the application gets handed when it does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexed by slot, and it outlives the slot deliberately.</b> The row is recycled on the tick after its <c>Closed</c> event — an application must not be
/// made to wait a minute for a slot because a client might come back — so what survives the grace is this record, which holds no row state: the identity that
/// was closed, the token that names it, the deadline, and the <c>appData</c> admission attached. A new session on the same slot has a different generation and
/// mints its own token, so the two cannot be confused; when it closes resumably in its turn it takes this record over and the older pending expires early,
/// which is the "under pressure the oldest expires early" of 01-model § 3 arriving by the same door.
/// </para>
/// <para>
/// <b><see cref="State"/> is the exactly-once decision (SUB-12).</b> It holds the closed session's packed identity while the record is armed and zero when it
/// is not, and both the link thread's consume and the tick's expiry claim it with one compare-exchange. A generation is never zero, so an armed identity is
/// never zero either and the two states cannot alias. Whichever CAS wins decides which of the two events the application sees, and the loser does nothing —
/// so exactly one of <c>Opened{resumedFrom}</c> and <c>ResumeExpired</c> follows a resumable <c>Closed</c>, including for a resume that arrives in the same
/// instant as the deadline.
/// </para>
/// <para>
/// <see cref="Prev"/> and <see cref="Next"/> thread the armed records in deadline order — every arm takes the same grace, so appending at the tail keeps the
/// list sorted and the expiry sweep only ever looks at its head. They are touched on the tick alone: a consume clears <see cref="State"/> and nothing else,
/// and the sweep unlinks what it finds already claimed. That is what keeps the list free of the races the state word is there to decide.
/// </para>
/// </remarks>
internal struct ResumeRecord
{
    /// <summary>The armed session's <see cref="SessionId.Value"/>, or zero when this record is not armed. The compare-exchange target.</summary>
    public long State;

    /// <summary>The token's first eight bytes: the slot in bytes 0-1, then random.</summary>
    public ulong TokenLo;

    /// <summary>The token's last eight bytes, all random but for the top bit, which is set so that no issued token is all-zero.</summary>
    public ulong TokenHi;

    /// <summary>The monotonic timestamp after which the token is stale, on the same clock <see cref="SessionTable.NowTicks"/> reads.</summary>
    public long DeadlineTicks;

    /// <summary>The previous armed record's slot, or -1. Tick side only.</summary>
    public int Prev;

    /// <summary>The next armed record's slot, or -1. Tick side only.</summary>
    public int Next;
}

internal sealed unsafe partial class SessionTable
{
    /// <summary>Bytes of a resume token on the wire, as <c>HELLO</c> and <c>WELCOME</c> carry it.</summary>
    internal const int ResumeTokenBytes = 16;

    /// <summary>Where the slot lives inside a token: the low two bytes, so a presented token names its record without a lookup.</summary>
    private const int ResumeTokenSlotBits = 16;

    /// <summary>The bit forced high in <see cref="ResumeRecord.TokenHi"/>, which is what keeps an issued token from being all-zero.</summary>
    private const ulong ResumeTokenNonZeroBit = 1UL << 63;

    /// <summary>The armed records, in deadline order: the earliest deadline's slot, or -1. Tick side only.</summary>
    private int _armedHead = -1;

    /// <summary>The latest deadline's slot, or -1. Tick side only.</summary>
    private int _armedTail = -1;

    /// <summary>Armed records right now. Tick side only.</summary>
    private int _armedCount;

    /// <summary>
    /// The monotonic clock the grace period is measured on, in <see cref="Stopwatch"/> ticks; <see langword="null"/> takes the stopwatch itself.
    /// </summary>
    /// <remarks>
    /// The same seam, in the same units, as <c>SubscriptionConnection.Clock</c>, and for the same reason: a deadline a test cannot move is a deadline a test
    /// has to sleep through. A resume racing its own deadline is the one behaviour SUB-12 is about, and it is only reachable deterministically by driving the
    /// clock.
    /// </remarks>
    internal Func<long> Clock { get; set; }

    /// <summary>Whether this table issues resume tokens at all — <see cref="SubscriptionsOptions.ResumeGraceMs"/> above zero.</summary>
    internal bool ResumeEnabled => _resumeGraceTicks > 0;

    /// <summary>Armed resume records right now, for tests and diagnostics.</summary>
    internal int PendingResumeCount => _armedCount;

    /// <summary>
    /// The application data a slot's resume record still holds, for the test that asserts it is let go of.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The object, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <b>Exposed because the release is otherwise invisible, and invisible is how it was missed.</b> A record that keeps its <c>appData</c> after the resume
    /// was consumed roots an application object in engine memory for as long as that slot stays unused — nothing fails, nothing counts it, and the
    /// application believes it handed the reference back.
    /// </remarks>
    internal object ResumeAppDataForTest(int slot) => _resumeAppData == null || slot >= _capacity ? null : _resumeAppData[slot];

    /// <summary>The current monotonic timestamp, from <see cref="Clock"/> or the stopwatch.</summary>
    internal long NowTicks => Clock?.Invoke() ?? Stopwatch.GetTimestamp();

    /// <summary>
    /// <b>Tests only</b> — SUB-12's mutant. Consumes a record by reading it rather than by claiming it, which is the one move the rule forbids.
    /// </summary>
    /// <remarks>
    /// The defect this reintroduces is the obvious implementation: check the record, hand the caller its contents, leave it armed. Both sides then act on
    /// one record — the client resumes and the sweep reports the same session expired — and the application destroys what it has just re-bound. It is on the
    /// production path deliberately: a mutant applied to a copy of the path proves something about the copy.
    /// </remarks>
    internal bool ConsumeWithoutClaimForTest { get; set; }

    /// <summary>
    /// <b>Tests only.</b> Makes <see cref="Open"/> throw after a resume has been consumed, which is the one way "neither event" is reachable.
    /// </summary>
    /// <remarks>
    /// The real causes — a table disposed between the lease and the open, an allocation failure copying the payload, a lease lost to a bug — are all either
    /// unreachable from a test or indistinguishable from a broken fixture. The property is worth a seam anyway: it is the half of SUB-12 that strands state
    /// in the application rather than corrupting state in the engine, so nothing else would ever report it.
    /// </remarks>
    internal bool ThrowFromOpenForTest { get; set; }

    /// <summary>
    /// Writes the token a session may come back with into <paramref name="destination"/>.
    /// </summary>
    /// <param name="session">The session, open or closing.</param>
    /// <param name="destination">At least <see cref="ResumeTokenBytes"/> bytes.</param>
    /// <returns>
    /// <see langword="false"/> when this table issues no tokens, when the identity does not name its row, or when no token was minted for it — in each case
    /// the client is told there is no resume by the all-zero token the wire reserves for exactly that.
    /// </returns>
    public bool TryWriteResumeToken(SessionId session, Span<byte> destination)
    {
        if (!ResumeEnabled || destination.Length < ResumeTokenBytes || !session.IsValid || session.Slot >= _capacity)
        {
            return false;
        }

        if (!TryEnter())
        {
            return false;
        }

        try
        {
            ref var row = ref Rows[session.Slot];
            if (!GateNames(Volatile.Read(ref row.Gate), session))
            {
                return false;
            }

            var token = OpenTokens.Slice(session.Slot * 2, 2);

            // Acquire on the high word, which MintResumeToken writes last: a reader that sees it set sees the low word written before it.
            var hi = Volatile.Read(ref token[1]);
            if (hi == 0)
            {
                return false;
            }

            WriteToken(destination, token[0], hi);
            return true;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Consumes the record a presented token names, which is what turns a reconnect into a resume.
    /// </summary>
    /// <param name="token">The token the client presented; any length but <see cref="ResumeTokenBytes"/> is no token, as is all-zero.</param>
    /// <param name="resumed">The identity the token named, or <see cref="SessionId.None"/>.</param>
    /// <param name="appData">The application data that session carried, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when this call is the one that consumed it.</returns>
    /// <remarks>
    /// <b>Link thread, one compare-exchange, nothing else</b> — SUB-05's allow-list, and SUB-12's exactly-once. A stale, forged or already-used token loses
    /// that CAS or never matches a record, and either way the answer is a fresh session rather than an error: a client cannot tell a token it was too late
    /// with from one that never existed, which is the right amount of information to give it.
    /// </remarks>
    public bool TryConsumeResume(ReadOnlySpan<byte> token, out SessionId resumed, out object appData)
    {
        resumed = SessionId.None;
        appData = null;

        if (!ResumeEnabled || token.Length != ResumeTokenBytes)
        {
            return false;
        }

        var lo = ReadUInt64(token);
        var hi = ReadUInt64(token[sizeof(ulong)..]);
        if ((lo | hi) == 0)
        {
            return false;
        }

        var slot = (int)(lo & ((1UL << ResumeTokenSlotBits) - 1));
        if (slot >= _capacity)
        {
            return false;
        }

        if (!TryEnter())
        {
            return false;
        }

        try
        {
            ref var record = ref Resumes[slot];

            // Acquire: the fields below were written before the release store that armed this record, and a plain read of them could otherwise be hoisted
            // above this one. The deadline is read before the CAS and the CAS is what decides, so a deadline that passes in between costs the client the
            // resume and never the record's integrity.
            var armed = Volatile.Read(ref record.State);
            if (armed == 0 || record.TokenLo != lo || record.TokenHi != hi || NowTicks > record.DeadlineTicks)
            {
                return false;
            }

            // <b>Read BEFORE the compare-exchange, and that order is load-bearing.</b> Read after it, this races the tick: the moment the CAS succeeds the
            // record reads as consumed, so the sweep may clear this entry — or a new session on the same slot may arm over it — while this thread is still
            // between its CAS and its load. The winner would then hand the application null, or another client's handle. Taken first, the copy belongs to
            // this thread whatever either side does next, and it is simply dropped when the CAS below is lost.
            var pending = _resumeAppData[slot];

            if (!ConsumeWithoutClaimForTest && Interlocked.CompareExchange(ref record.State, 0, armed) != armed)
            {
                return false;   // the sweep expired it, or another connection presented the same token first: single use (SUB-12)
            }

            resumed = SessionId.FromValue((uint)armed);
            appData = pending;
            return true;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Mints the token a session may come back with. Called by the thread that opened the slot, before the row is published.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <remarks>
    /// <b>Cryptographic randomness, 111 bits of it, and not <see cref="Random"/>.</b> A token is a bearer credential for whatever the old session controlled:
    /// a predictable one hands an avatar to whoever guesses it. The low two bytes are the slot, so a presented token names its record directly — which is
    /// what 04-transport § 3's "one CAS on its resume record" assumes — and the top bit is forced high so that no issued token reads as the wire's "none".
    /// <b>A deviation from 01-model § 3's "random 128-bit"</b>, and what it trades away is a hash table on the ingress path; 111 bits leaves a guess at
    /// 2^-111 per attempt, with a wrong guess costing the attacker a refused handshake and the server one record read.
    /// </remarks>
    private void MintResumeToken(int slot)
    {
        if (!ResumeEnabled)
        {
            return;
        }

        var token = OpenTokens.Slice(slot * 2, 2);
        Span<byte> random = stackalloc byte[ResumeTokenBytes];
        RandomNumberGenerator.Fill(random);

        var lo = (ReadUInt64(random[..sizeof(ulong)]) << ResumeTokenSlotBits) | (uint)slot;
        var hi = ReadUInt64(random[sizeof(ulong)..]) | ResumeTokenNonZeroBit;

        token[0] = lo;

        // Release, and last: TryWriteResumeToken reads this word first and takes a set one as "the low word is there too".
        Volatile.Write(ref token[1], hi);
    }

    /// <summary>
    /// Arms the resume record of a session that just closed resumably. Tick side, from the close itself.
    /// </summary>
    /// <param name="session">The identity that closed.</param>
    /// <param name="appData">What admission attached to it.</param>
    /// <returns><see langword="false"/> when there was no token to arm, so the close is not resumable after all.</returns>
    /// <remarks>
    /// Takes the record over whatever it held: a pending resume for an older session on this slot expires here rather than at its deadline, and so does the
    /// oldest pending of all once the cap is reached. Both are the same decision — a pending resume is a promise to one client that costs every other client a
    /// record — and both produce the <c>ResumeExpired</c> that tells the application to let go.
    /// </remarks>
    private bool ArmResume(SessionId session, object appData)
    {
        var slot = session.Slot;
        var token = OpenTokens.Slice(slot * 2, 2);
        if (token[1] == 0)
        {
            return false;   // opened before resume was enabled, or never minted: there is no token for a client to come back with
        }

        ref var record = ref Resumes[slot];
        ClaimResumeRecord(slot, ref record);

        while (_armedCount >= _maxPendingResumes && _armedHead >= 0)
        {
            ClaimResumeRecord(_armedHead, ref Resumes[_armedHead]);
        }

        record.TokenLo = token[0];
        record.TokenHi = token[1];
        record.DeadlineTicks = NowTicks + _resumeGraceTicks;
        _resumeAppData[slot] = appData;
        LinkArmed(slot, ref record);

        // Release: a link thread that sees this record armed must see the token and the deadline written above.
        Volatile.Write(ref record.State, session.Value);
        return true;
    }

    /// <summary>
    /// Expires every armed record whose deadline has passed, and unlinks the ones a resume already consumed. Tick side, once per tick.
    /// </summary>
    /// <remarks>
    /// The list is in deadline order, so this stops at the first record still inside its grace: the cost is the records that actually left, not the table. A
    /// record the link thread consumed is unlinked here too, which is why a consume can be one compare-exchange and touch no list.
    /// </remarks>
    private void ExpireResumes()
    {
        // Once per sweep: every deadline is compared against one instant, so the list stays monotone in the reading, and a sweep that expires fifty records
        // does not call the clock fifty times.
        var now = NowTicks;

        while (_armedHead >= 0)
        {
            var slot = _armedHead;
            ref var record = ref Resumes[slot];

            if (Volatile.Read(ref record.State) == 0)
            {
                // Consumed: the resume already happened, and the list is where that bookkeeping lands. The appData is released HERE because the consume
                // cannot do it — it took its own copy before the CAS (see TryConsumeResume) and clearing from a link thread would be a managed write
                // SUB-05 does not allow it. Without this the application's object stayed rooted in this array until the slot armed again, which for a
                // client that never drops twice is the life of the process.
                _resumeAppData[slot] = null;
                Unlink(slot, ref record);
                continue;
            }

            if (now <= record.DeadlineTicks)
            {
                return;
            }

            ClaimResumeRecord(slot, ref record);
        }
    }

    /// <summary>
    /// Takes an armed record out of service, telling the application to let go of what the session held.
    /// </summary>
    /// <param name="slot">The record's slot.</param>
    /// <param name="record">The record.</param>
    /// <remarks>
    /// <see cref="Interlocked.Exchange(ref long, long)"/> rather than a store: a resume in flight on a link thread is mid-CAS against the value this takes
    /// away, and that CAS then fails. Exactly one of the two sides wins, which is the whole of SUB-12.
    /// </remarks>
    private void ClaimResumeRecord(int slot, ref ResumeRecord record)
    {
        var previous = Interlocked.Exchange(ref record.State, 0);
        var appData = _resumeAppData[slot];
        _resumeAppData[slot] = null;
        Unlink(slot, ref record);

        if (previous != 0)
        {
            var expired = SessionId.FromValue((uint)previous);
            // No role, no limits, no kind: the row they belonged to is long recycled, and the only two things this event means are which session will not be
            // coming back and what the application attached to it.
            _events.Append(new SessionEvent(SessionEventKind.ResumeExpired, expired, default, null, appData, null, null, null,
                SessionId.None, default, 0, false));
        }
    }

    /// <summary>
    /// Reports a resume that was consumed and then could not be used, so the application still gets exactly one event for it.
    /// </summary>
    /// <param name="resumed">The identity the token named.</param>
    /// <param name="appData">What that session carried.</param>
    /// <remarks>
    /// <b>SUB-12 says exactly one of the two events, and "neither" is the half that leaks.</b> The record is claimed before the new session is opened, so a
    /// throw out of <see cref="Open"/> — a disposed table, an allocation failure copying the <c>HELLO</c> payload, a lease lost — would otherwise leave the
    /// record spent, no <c>Opened</c> appended, and the sweep taking its "already consumed" branch: the application would be left holding whatever the old
    /// session controlled with no event ever coming for it. Telling it the resume expired is the truthful answer, because from its side nothing came back.
    /// </remarks>
    private void ReportResumeAbandoned(SessionId resumed, object appData)
        => _events.Append(new SessionEvent(SessionEventKind.ResumeExpired, resumed, default, null, appData, null, null, null,
            SessionId.None, default, 0, false));

    /// <summary>Appends a record to the deadline-ordered list.</summary>
    /// <param name="slot">The record's slot.</param>
    /// <param name="record">The record.</param>
    private void LinkArmed(int slot, ref ResumeRecord record)
    {
        record.Prev = _armedTail;
        record.Next = -1;

        if (_armedTail >= 0)
        {
            Resumes[_armedTail].Next = slot;
        }
        else
        {
            _armedHead = slot;
        }

        _armedTail = slot;
        _armedCount++;
    }

    /// <summary>Takes a record out of the deadline-ordered list, if it is in it.</summary>
    /// <param name="slot">The record's slot.</param>
    /// <param name="record">The record.</param>
    private void Unlink(int slot, ref ResumeRecord record)
    {
        if (record.Prev < 0 && record.Next < 0 && _armedHead != slot)
        {
            return;
        }

        if (record.Prev >= 0)
        {
            Resumes[record.Prev].Next = record.Next;
        }
        else
        {
            _armedHead = record.Next;
        }

        if (record.Next >= 0)
        {
            Resumes[record.Next].Prev = record.Prev;
        }
        else
        {
            _armedTail = record.Prev;
        }

        record.Prev = -1;
        record.Next = -1;
        _armedCount--;
    }

    /// <summary>Forgets the token of a slot coming back to the free stack, so that a secret does not outlive the session it belonged to.</summary>
    /// <param name="slot">The slot.</param>
    /// <remarks>
    /// <b>Hygiene, not an invariant, and worth saying which.</b> Nothing can read a stale entry: every <see cref="Open"/> mints over both words, and
    /// <see cref="TryWriteResumeToken"/> refuses a gate that does not name an open session, so a recycled slot's old bytes are unreachable by construction —
    /// deleting this call would break no behaviour and no test. It stays because the bytes are a bearer credential for whatever the old session controlled,
    /// and leaving one in engine memory for as long as a slot happens to stay free is a wider blast radius than it needs to have.
    /// <para>
    /// The armed record keeps its own copy: that one is the pending resume, and it expires on its deadline rather than with the row.
    /// </para>
    /// </remarks>
    private void ClearResumeToken(int slot)
    {
        if (_openTokens == null)
        {
            return;
        }

        var token = OpenTokens.Slice(slot * 2, 2);
        token[0] = 0;
        Volatile.Write(ref token[1], 0UL);
    }

    /// <summary>Whether a close with this reason leaves a client able to come back.</summary>
    /// <param name="reason">Why the session ended.</param>
    /// <returns><see langword="true"/> for a link that went away on its own.</returns>
    /// <remarks>
    /// <b>Resume is for a link-dead session and nothing else</b> (01-model § 3). A client that said <c>BYE</c> chose to leave; one the application kicked, one
    /// that broke the protocol or was abusive, and one the server dropped for lagging must not get to hold an avatar for a minute by reconnecting — for the
    /// abusive case, resume would be the cheapest way to keep a session alive through its own eviction. What is left is the two reasons that mean the link died
    /// without anybody deciding: it was lost, or it stopped acknowledging.
    /// </remarks>
    private static bool IsResumable(SessionCloseReason reason)
        => reason is SessionCloseReason.LinkLost or SessionCloseReason.Unacknowledged;

    /// <summary>Writes a token's two words out in wire order.</summary>
    /// <param name="destination">At least <see cref="ResumeTokenBytes"/> bytes.</param>
    /// <param name="lo">The low word.</param>
    /// <param name="hi">The high word.</param>
    private static void WriteToken(Span<byte> destination, ulong lo, ulong hi)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, lo);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[sizeof(ulong)..], hi);
    }

    /// <summary>Reads one word of a token.</summary>
    /// <param name="source">At least eight bytes.</param>
    /// <returns>The word.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt64(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadUInt64LittleEndian(source);
}
