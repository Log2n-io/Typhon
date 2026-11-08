using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// One request an application made of a session, as <see cref="RecordingSessionRequests"/> recorded it.
/// </summary>
[PublicAPI]
public readonly struct RecordedSessionRequest
{
    internal RecordedSessionRequest(SessionId session, SessionRequestKind kind, long payload, object managed, ushort code)
    {
        Session = session;
        Kind = kind;
        _payload = payload;
        _managed = managed;
        Code = code;
    }

    private readonly long _payload;
    private readonly object _managed;

    /// <summary>Which session.</summary>
    public SessionId Session { get; }

    /// <summary>What was asked for.</summary>
    public SessionRequestKind Kind { get; }

    /// <summary>A <see cref="SessionRequestKind.Profile"/>'s profile name; <see langword="null"/> otherwise.</summary>
    public string Profile => Kind == SessionRequestKind.Profile ? (string)_managed : null;

    /// <summary>A <see cref="SessionRequestKind.Control"/>'s or <see cref="SessionRequestKind.Follow"/>'s entity; <see cref="EntityId.Null"/> otherwise.</summary>
    public EntityId Entity => Kind is SessionRequestKind.Control or SessionRequestKind.Follow ? EntityId.FromRawValue((ulong)_payload) : EntityId.Null;

    /// <summary>A <see cref="SessionRequestKind.SetBudget"/>'s bytes per second, or a <see cref="SessionRequestKind.SetVisibility"/>'s mask; 0 otherwise.</summary>
    public long Value => Kind is SessionRequestKind.SetBudget or SessionRequestKind.SetVisibility ? _payload : 0;

    /// <summary>A <see cref="SessionRequestKind.SetSources"/>'s set; <see langword="null"/> otherwise.</summary>
    public string SourceSet => (_managed as GroupKeysRequest)?.Set;

    /// <summary>A <see cref="SessionRequestKind.SetSources"/>'s keys; empty otherwise.</summary>
    public ReadOnlySpan<long> SourceKeys => (_managed as GroupKeysRequest)?.Keys ?? [];

    /// <summary>A <see cref="SessionRequestKind.Kick"/>'s close code; 0 otherwise.</summary>
    public ushort Code { get; }

    /// <summary>A <see cref="SessionRequestKind.Kick"/>'s reason; <see langword="null"/> otherwise.</summary>
    public string KickReason => Kind == SessionRequestKind.Kick ? (string)_managed : null;

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        SessionRequestKind.Profile => $"{Session} Profile '{Profile}'",
        SessionRequestKind.Control or SessionRequestKind.Follow => $"{Session} {Kind} {Entity}",
        SessionRequestKind.Kick => $"{Session} Kick {Code}",
        _ => $"{Session} {Kind}",
    };
}

/// <summary>
/// A fake <see cref="ISessionRequests"/> for unit tests: session events are raised by hand, and every request the code under test makes is recorded, in call
/// order, through the same <see cref="SessionRequest"/> builder — and so the same argument checks — a running engine uses.
/// </summary>
/// <example>
/// <code>
/// var subs = new RecordingSessionRequests();
/// var s = new SessionId(1, 1);
/// subs.Raise(SessionEvent.Create(SessionEventKind.Opened, s));
/// lifecycle.Run(subs);                                 // the application's code, written against ISessionRequests
/// Assert.That(subs.ControlledOf(s), Is.Not.EqualTo(EntityId.Null));
/// subs.NextTick();
/// subs.Raise(SessionEvent.Create(SessionEventKind.Closed, s, resumable: true, reason: SessionCloseReason.LinkLost));
/// </code>
/// </example>
/// <remarks>
/// Nothing is applied: a request for a session that never opened is recorded like any other. <see cref="ControlledOf"/> answers from the
/// <see cref="SessionRequestKind.Control"/> requests recorded so far, the latest per session, as the engine's apply pass would leave it. Single-threaded, as a
/// test is.
/// </remarks>
[PublicAPI]
public sealed class RecordingSessionRequests : ISessionRequests
{
    private readonly SessionRequestLog _log = new(1);
    private readonly List<SessionEvent> _events = [];
    private readonly List<RecordedSessionRequest> _requests = [];
    private readonly Dictionary<SessionId, EntityId> _controlled = [];
    private int _read;

    /// <summary>A recorder.</summary>
    /// <param name="explicitSets">The explicit group sets <see cref="SessionRequest.SetSources"/> accepts, as <c>subs.Groups(...)</c> would declare them.</param>
    public RecordingSessionRequests(params string[] explicitSets)
    {
        ArgumentNullException.ThrowIfNull(explicitSets);
        _log.ExplicitSets = new HashSet<string>(explicitSets, StringComparer.Ordinal);
    }

    /// <summary>Adds an event to this tick's batch.</summary>
    /// <param name="sessionEvent">The event, from <see cref="SessionEvent.Create"/>.</param>
    public void Raise(in SessionEvent sessionEvent) => _events.Add(sessionEvent);

    /// <summary>Starts the next tick: the events raised so far are no longer delivered. The recorded requests are kept.</summary>
    public void NextTick()
    {
        Collect();
        _events.Clear();
    }

    /// <inheritdoc />
    public ReadOnlySpan<SessionEvent> SessionEvents => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_events);

    /// <inheritdoc />
    public SessionRequest Session(SessionId session)
    {
        Collect();
        return _log.Request(0, session);
    }

    /// <inheritdoc />
    public EntityId ControlledOf(SessionId session)
    {
        Collect();
        return _controlled.TryGetValue(session, out var entity) ? entity : EntityId.Null;
    }

    /// <summary>Every request recorded, in call order, across ticks.</summary>
    public IReadOnlyList<RecordedSessionRequest> Requests
    {
        get
        {
            Collect();
            return _requests;
        }
    }

    /// <summary>The requests recorded for one session, in call order.</summary>
    /// <param name="session">The session.</param>
    /// <returns>Its requests.</returns>
    public List<RecordedSessionRequest> RequestsOf(SessionId session)
    {
        Collect();
        var of = new List<RecordedSessionRequest>();
        foreach (var r in _requests)
        {
            if (r.Session == session)
            {
                of.Add(r);
            }
        }

        return of;
    }

    /// <summary>The profile last requested for a session, or <see langword="null"/>.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The profile's name.</returns>
    public string ProfileOf(SessionId session) => Last(session, SessionRequestKind.Profile)?.Profile;

    /// <summary>Whether a session was kicked.</summary>
    /// <param name="session">The session.</param>
    /// <returns>True when a kick was requested.</returns>
    public bool WasKicked(SessionId session) => Last(session, SessionRequestKind.Kick) != null;

    /// <summary>Forgets every recorded request and the controlled entities; the events of this tick are kept.</summary>
    public void ClearRequests()
    {
        Collect();
        _requests.Clear();
        _controlled.Clear();
    }

    private RecordedSessionRequest? Last(SessionId session, SessionRequestKind kind)
    {
        Collect();
        for (var i = _requests.Count - 1; i >= 0; i--)
        {
            if (_requests[i].Session == session && _requests[i].Kind == kind)
            {
                return _requests[i];
            }
        }

        return null;
    }

    // A builder appends to the segment when its methods run, after Session returned: the segment is drained on every read instead.
    private void Collect()
    {
        var segment = _log.Segment(0);
        var records = segment.Records;
        for (; _read < records.Length; _read++)
        {
            ref readonly var r = ref records[_read];
            var session = new SessionId((ushort)(r.Session & 0xFFFF), (ushort)(r.Session >> 16));
            var request = new RecordedSessionRequest(session, r.Kind, r.Payload, segment.ObjectAt(r.ObjectIndex), r.Code);
            _requests.Add(request);
            if (r.Kind == SessionRequestKind.Control)
            {
                _controlled[session] = request.Entity;
            }
        }

        if (_read > 0 && _read == records.Length)
        {
            segment.Reset();
            _read = 0;
        }
    }
}
