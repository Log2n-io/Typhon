using System;

namespace Typhon.Engine.Internals;

/// <summary>What a recorded <see cref="SessionRequest"/> asks for.</summary>
internal enum SessionRequestKind : byte
{
    /// <summary>Unset; never recorded.</summary>
    None = 0,

    /// <summary>Bind the session to a declared interest profile.</summary>
    Profile = 1,

    /// <summary>Put an observer in one of the session's slots.</summary>
    Observe = 2,

    /// <summary>Empty one of the session's observer slots.</summary>
    Unobserve = 3,

    /// <summary>Bind the session to the entity it controls.</summary>
    Control = 4,

    /// <summary>Replace the session's whole source subscription list.</summary>
    SetSources = 5,

    /// <summary>Set the session's outbound byte budget.</summary>
    SetBudget = 6,

    /// <summary>End the session.</summary>
    Kick = 7,
}

/// <summary>
/// One recorded request. Sixteen bytes and no reference, so a worker's segment is a flat array the apply pass walks forward.
/// </summary>
/// <remarks>
/// Managed payloads — a profile name, an observer, a kick's reason — live in the segment's own object array and are named here by index. That keeps the record
/// blittable and, more usefully, keeps the segment's reset to two integer assignments plus a bounded clear of the object slots that were used.
/// </remarks>
internal struct SessionRequestRecord
{
    /// <summary>Which session, as a packed <see cref="SessionId"/>.</summary>
    public uint Session;

    /// <summary>The numeric payload: a packed <see cref="EntityId"/>, a budget, a source count.</summary>
    public long Payload;

    /// <summary>Index into the segment's object array, or -1.</summary>
    public int ObjectIndex;

    /// <summary>A kick's close code.</summary>
    public ushort Code;

    /// <summary>An observer slot.</summary>
    public byte Slot;

    /// <summary>What is being asked for.</summary>
    public SessionRequestKind Kind;
}

/// <summary>
/// One worker's share of the tick's session requests. A worker appends to its own segment, so recording is a bounds check and two stores with no
/// synchronization and no line shared with another worker.
/// </summary>
internal sealed class SessionRequestSegment
{
    private const int InitialCapacity = 8;

    private SessionRequestRecord[] _records = new SessionRequestRecord[InitialCapacity];
    private object[] _objects = new object[InitialCapacity];
    private int _count;
    private int _objectCount;

    /// <summary>Requests recorded this tick.</summary>
    public int Count => _count;

    /// <summary>Records the segment can hold before it grows. Steady state never grows.</summary>
    public int RecordCapacity => _records.Length;

    /// <summary>Managed payloads the segment can hold before it grows.</summary>
    public int ObjectCapacity => _objects.Length;

    /// <summary>This tick's records, in the order they were recorded.</summary>
    public ReadOnlySpan<SessionRequestRecord> Records => new(_records, 0, _count);

    /// <summary>Resolves a record's managed payload.</summary>
    /// <param name="index">The record's <see cref="SessionRequestRecord.ObjectIndex"/>.</param>
    /// <returns>The payload, or <see langword="null"/>.</returns>
    public object ObjectAt(int index) => index < 0 ? null : _objects[index];

    /// <summary>Appends a request.</summary>
    /// <param name="session">Which session.</param>
    /// <param name="kind">What is being asked for.</param>
    /// <param name="payload">The numeric payload.</param>
    /// <param name="managed">The managed payload, or <see langword="null"/>.</param>
    /// <param name="code">A kick's close code.</param>
    /// <param name="slot">An observer slot.</param>
    public void Add(SessionId session, SessionRequestKind kind, long payload, object managed, ushort code = 0, byte slot = 0)
    {
        if (_count == _records.Length)
        {
            Array.Resize(ref _records, _records.Length * 2);
        }

        var objectIndex = -1;
        if (managed != null)
        {
            if (_objectCount == _objects.Length)
            {
                Array.Resize(ref _objects, _objects.Length * 2);
            }

            objectIndex = _objectCount++;
            _objects[objectIndex] = managed;
        }

        ref var record = ref _records[_count++];
        record.Session = session.Value;
        record.Kind = kind;
        record.Payload = payload;
        record.ObjectIndex = objectIndex;
        record.Code = code;
        record.Slot = slot;
    }

    /// <summary>
    /// Empties the segment, keeping its capacity.
    /// </summary>
    /// <remarks>
    /// The used object slots are nulled rather than left behind. Leaving them would hold a profile name, an observer or a kick's reason alive until the same
    /// peak was reached again — a leak proportional to the busiest tick the process ever had, which is exactly the shape of leak nobody notices.
    /// </remarks>
    public void Reset()
    {
        if (_objectCount > 0)
        {
            Array.Clear(_objects, 0, _objectCount);
            _objectCount = 0;
        }

        _count = 0;
    }
}

/// <summary>
/// The tick's session requests: one segment per worker, applied single-threaded at the start of the session pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why requests and not writes.</b> Systems run in parallel and several may steer the same session in one tick. Writing the row directly would need a lock
/// per session and would still give a result that depended on which thread was quicker. Recording per worker and applying in a fixed order — worker index,
/// then record order — costs no synchronization at record time and makes "last writer wins per field" a property of the order rather than of the scheduler.
/// </para>
/// <para>
/// <b>Steady state allocates nothing.</b> Every segment keeps the capacity of the busiest tick so far, and <see cref="Apply"/> empties them rather than
/// replacing them. The only allocations are the growth steps of the ticks that reach a new peak.
/// </para>
/// <para>
/// <b>Requests for a session that has gone are dropped silently.</b> A system deciding something about a session that closed between its own execution and the
/// apply pass is not an error; it is the normal consequence of a client disconnecting mid-tick.
/// </para>
/// </remarks>
internal sealed class SessionRequestLog
{
    private SessionRequestSegment[] _segments;
    private ReplicationThreadAffinity _affinity;

    /// <summary>Creates the log.</summary>
    /// <param name="workerCount">How many workers may record at once. One segment each, so no two share a cache line's worth of state.</param>
    public SessionRequestLog(int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);

        _segments = new SessionRequestSegment[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            _segments[i] = new SessionRequestSegment();
        }
    }

    /// <summary>How many segments there are.</summary>
    public int WorkerCount => _segments.Length;

    /// <summary>
    /// Grows the log to one segment per worker, keeping the segments it already has.
    /// </summary>
    /// <param name="workerCount">How many workers this tick dispatches.</param>
    /// <remarks>
    /// Called from the tick's prologue, single-threaded and before any system runs, so the array is never resized under a worker holding a segment. It only
    /// ever grows: a tick dispatched narrower than the last one leaves the spare segments alone rather than reallocating the whole table.
    /// </remarks>
    public void EnsureWorkers(int workerCount)
    {
        if (workerCount <= _segments.Length)
        {
            return;
        }

        var grown = new SessionRequestSegment[workerCount];
        Array.Copy(_segments, grown, _segments.Length);
        for (var i = _segments.Length; i < workerCount; i++)
        {
            grown[i] = new SessionRequestSegment();
        }

        _segments = grown;
    }

    /// <summary>Requests recorded this tick, across every segment.</summary>
    public int Count
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _segments.Length; i++)
            {
                total += _segments[i].Count;
            }

            return total;
        }
    }

    /// <summary>One worker's segment.</summary>
    /// <param name="workerIndex">The worker.</param>
    /// <returns>Its segment.</returns>
    public SessionRequestSegment Segment(int workerIndex) => _segments[workerIndex];

    /// <summary>Opens a request against a session, recording into the calling worker's segment.</summary>
    /// <param name="workerIndex">The calling worker.</param>
    /// <param name="session">The session to steer.</param>
    /// <returns>The request.</returns>
    public SessionRequest Request(int workerIndex, SessionId session) => new(_segments[workerIndex], session);

    /// <summary>
    /// Applies every recorded request to the table and empties the log.
    /// </summary>
    /// <param name="table">The session table.</param>
    /// <returns>How many requests took effect. Requests against a session that has gone are not counted.</returns>
    /// <exception cref="NotSupportedException">
    /// A record naming a request a later phase builds — <see cref="SessionRequestKind.Observe"/>, <see cref="SessionRequestKind.Unobserve"/>,
    /// <see cref="SessionRequestKind.SetSources"/>. <see cref="SessionRequest"/> refuses those at the call site, where the stack still names the system that
    /// asked, so reaching this is a record built past that surface. It stays as a backstop rather than being quietly ignored, which would leave an application
    /// believing a session was steered when it was not.
    /// </exception>
    /// <remarks>
    /// <b>The segments are emptied in a <see langword="finally"/>.</b> Anything a record's application throws — the backstop above, or a future request whose
    /// handler faults — would otherwise leave the tick's records in place, and the next tick would replay every one of them and throw again at the same record.
    /// One bad request would become a permanently wedged session pass rather than one failed tick.
    /// </remarks>
    public int Apply(SessionTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        _affinity.Enter(nameof(SessionRequestLog), nameof(Apply));
        try
        {
            var applied = 0;
            for (var s = 0; s < _segments.Length; s++)
            {
                var segment = _segments[s];
                var records = segment.Records;
                for (var r = 0; r < records.Length; r++)
                {
                    if (ApplyOne(table, segment, in records[r]))
                    {
                        applied++;
                    }
                }
            }

            return applied;
        }
        finally
        {
            for (var s = 0; s < _segments.Length; s++)
            {
                _segments[s].Reset();
            }

            _affinity.Exit();
        }
    }

    /// <summary>Empties the log without applying it. For a tick that is thrown away.</summary>
    public void Reset()
    {
        for (var s = 0; s < _segments.Length; s++)
        {
            _segments[s].Reset();
        }
    }

    private static bool ApplyOne(SessionTable table, SessionRequestSegment segment, in SessionRequestRecord record)
    {
        var session = SessionId.FromValue(record.Session);
        switch (record.Kind)
        {
            case SessionRequestKind.Profile:
                return table.SetProfile(session, (string)segment.ObjectAt(record.ObjectIndex));

            case SessionRequestKind.Control:
                return table.SetControlled(session, EntityId.FromRaw(record.Payload));

            case SessionRequestKind.SetBudget:
                return table.SetBudget(session, (int)record.Payload);

            case SessionRequestKind.Kick:
                return table.Close(session, SessionCloseReason.Kicked, record.Code, (string)segment.ObjectAt(record.ObjectIndex));

            case SessionRequestKind.Observe:
            case SessionRequestKind.Unobserve:
                throw new NotSupportedException(
                    $"{session} asked for {record.Kind}, and per-session observers are Phase 2 work: Phase 1 serves the World observer a profile declares. " +
                    $"{nameof(SessionRequest)} refuses this at the call site, so a record reaching here was built past that surface.");

            case SessionRequestKind.SetSources:
                throw new NotSupportedException(
                    $"{session} asked for SetSources, and shared sources are Phase 4 work. Until then, data a client needs and a position cannot reach " +
                    $"travels as owner fields on the entity that owns it. {nameof(SessionRequest)} refuses this at the call site.");

            default:
                return false;
        }
    }
}
