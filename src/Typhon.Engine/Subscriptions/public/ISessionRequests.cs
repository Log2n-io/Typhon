using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// The session lifecycle as an application system sees it: the tick's session events, and the requests it makes of a session in answer — the part of
/// <see cref="SubscriptionsCommands"/> an application's lifecycle code needs, behind an interface so that code can be unit-tested.
/// </summary>
/// <remarks>
/// <para>
/// <b>A test seam, not a second path.</b> Write the lifecycle handling (Opened: spawn and <see cref="SessionRequest.Control"/>; Closed and resumable: hold the
/// body; Opened with <see cref="SessionEvent.ResumedFrom"/>: rebind; ResumeExpired: retire) against this interface, pass <c>ctx.Subscriptions</c> in the
/// tick and a <see cref="RecordingSessionRequests"/> in a test. The events arrive and the requests are recorded exactly as in a running engine; nothing is
/// applied, which is the engine's part and has its own tests.
/// </para>
/// <para>
/// A system that steers sessions on its hot path keeps calling <see cref="SubscriptionsCommands"/> directly: the session lifecycle is a handful of events a
/// tick, where an interface call costs nothing measurable.
/// </para>
/// </remarks>
[PublicAPI]
public interface ISessionRequests
{
    /// <summary>What happened to sessions since the last tick, in order (<see cref="SubscriptionsCommands.SessionEvents"/>).</summary>
    ReadOnlySpan<SessionEvent> SessionEvents { get; }

    /// <summary>Asks something of a session; applied by the next tick's prologue (<see cref="SubscriptionsCommands.Session"/>).</summary>
    /// <param name="session">The session.</param>
    /// <returns>A builder whose calls are recorded.</returns>
    SessionRequest Session(SessionId session);

    /// <summary>The entity a session controls, or <see cref="EntityId.Null"/> (<see cref="SubscriptionsCommands.ControlledOf"/>).</summary>
    /// <param name="session">The session.</param>
    /// <returns>The controlled entity.</returns>
    EntityId ControlledOf(SessionId session);
}
