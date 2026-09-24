using JetBrains.Annotations;
using System;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// Steers one session from inside a system: its profile, its observers, what it controls, its budget, and whether it stays.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are requests, not writes.</b> Systems run in parallel over their own entities, and several of them may decide something about the same session in
/// the same tick. Each call appends a record to the calling worker's own segment — no lock, no shared cursor, no false sharing — and the engine applies every
/// segment single-threaded at the start of the session pass. Last writer wins per field, in a fixed order (worker index, then record order), so the result is
/// deterministic rather than whichever thread happened to be quicker.
/// </para>
/// <para>
/// <b>A <see langword="ref"/> <see langword="struct"/> on purpose.</b> It borrows the worker's segment for the duration of one statement; it cannot be stored
/// on a field, captured or passed to another thread, which is the compiler enforcing the per-worker discipline instead of documentation asking for it.
/// </para>
/// <para>
/// <b>What applies.</b> <see cref="Profile"/>, <see cref="Control"/>, <see cref="SetBudget"/> and <see cref="Kick"/> take effect.
/// <see cref="Observe"/>, <see cref="Unobserve"/> and <see cref="SetSources"/> throw here, at the call site, naming the phase that builds them — the shape is
/// complete now so that an application written against it never has to be revisited when the verb it wanted starts working.
/// </para>
/// <para>
/// <b>A refusal is thrown where the caller is, not a tick later.</b> Recording an unsupported verb and refusing it when the session pass runs puts the
/// exception on the engine's own stack, with nothing left naming the system that asked for it — and the system's own tick has already finished, so a debugger
/// stopped on the throw cannot show what led to it. <see cref="Kick"/> has always validated its close code at the call site for the same reason.
/// </para>
/// </remarks>
[PublicAPI]
public readonly ref struct SessionRequest
{
    private readonly SessionRequestSegment _segment;
    private readonly SessionId _session;

    internal SessionRequest(SessionRequestSegment segment, SessionId session)
    {
        _segment = segment;
        _session = session;
    }

    /// <summary>The session these requests are about.</summary>
    public SessionId Session => _session;

    /// <summary>
    /// Binds the session to a declared interest profile.
    /// </summary>
    /// <param name="name">The profile's name, as <c>subs.Profile(name, ...)</c> declared it.</param>
    /// <returns>This request, so several may be chained.</returns>
    public SessionRequest Profile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A profile request needs the name of a declared profile.", nameof(name));
        }

        _segment.Add(_session, SessionRequestKind.Profile, 0, name);
        return this;
    }

    /// <summary>
    /// Puts an observer in one of the session's observer slots, replacing whatever was there.
    /// </summary>
    /// <param name="observer">The region of interest.</param>
    /// <param name="slot">Which of the session's observer slots it occupies.</param>
    /// <returns>This request, so several may be chained.</returns>
    /// <remarks><b>The shape is final; the verb arrives in Phase 2</b>, with the observer shapes that need it.</remarks>
    /// <exception cref="NotSupportedException">Always, until Phase 2 builds per-session observers.</exception>
    public SessionRequest Observe(ObserverDeclaration observer, int slot)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, byte.MaxValue);

        throw new NotSupportedException(
            $"{_session} asked for Observe, and per-session observers are Phase 2 work: Phase 1 serves the World observer a profile declares. The shape is " +
            "final, so an application written against it does not have to be revisited when the verb starts working.");
    }

    /// <summary>
    /// Empties one of the session's observer slots.
    /// </summary>
    /// <param name="slot">Which slot.</param>
    /// <returns>This request, so several may be chained.</returns>
    /// <remarks><b>The shape is final; the verb arrives in Phase 2</b>, with <see cref="Observe"/>.</remarks>
    /// <exception cref="NotSupportedException">Always, until Phase 2 builds per-session observers.</exception>
    public SessionRequest Unobserve(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, byte.MaxValue);

        throw new NotSupportedException(
            $"{_session} asked for Unobserve, and per-session observers are Phase 2 work: Phase 1 serves the World observer a profile declares. The shape is " +
            "final, so an application written against it does not have to be revisited when the verb starts working.");
    }

    /// <summary>
    /// Binds the session to an entity it controls: owner fields and the <c>SELF</c> block follow that entity, and a profile's bound sphere follows it too.
    /// </summary>
    /// <param name="entity">The entity, or <see cref="EntityId.Null"/> to release.</param>
    /// <returns>This request, so several may be chained.</returns>
    public SessionRequest Control(EntityId entity)
    {
        _segment.Add(_session, SessionRequestKind.Control, (long)entity.RawValue, null);
        return this;
    }

    /// <summary>
    /// Replaces the session's whole source subscription list: diffed against the current one, idempotent, atomic within a frame.
    /// </summary>
    /// <param name="sources">The full list. An empty list unsubscribes from everything.</param>
    /// <returns>This request, so several may be chained.</returns>
    /// <remarks><b>The shape is final; the verb arrives in Phase 4</b>, with shared sources themselves.</remarks>
    /// <exception cref="NotSupportedException">Always, until Phase 4 builds shared sources.</exception>
    public SessionRequest SetSources(params SourceDeclaration[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        throw new NotSupportedException(
            $"{_session} asked for SetSources, and shared sources are Phase 4 work. Until then, data a client needs and a position cannot reach travels as " +
            "owner fields on the entity that owns it.");
    }

    /// <summary>
    /// Sets the session's outbound byte budget.
    /// </summary>
    /// <param name="bytesPerSecond">The budget. Zero removes it; the frame ceiling and the enter budget still apply.</param>
    /// <returns>This request, so several may be chained.</returns>
    /// <remarks>
    /// A Sphere session over its budget is degraded by LOD level (09 § 10): every distance band's period doubles per level, a profile with no band gets one
    /// beyond half its radius, and the enter budget halves. Records are deferred and never dropped, so a small number slows a view down rather than
    /// corrupting it. The level rises after a second over the budget and falls after three under 70 % of it. A World or ClientRegion session has no LOD
    /// level, so the budget does not act on it: a ClientRegion's is its profile's near budget (<c>Near</c>, 09 § 7), counted in entities.
    /// </remarks>
    public SessionRequest SetBudget(int bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerSecond);
        _segment.Add(_session, SessionRequestKind.SetBudget, bytesPerSecond, null);
        return this;
    }

    /// <summary>
    /// Ends the session.
    /// </summary>
    /// <param name="code">The close code, in the application range <c>4100-4999</c>.</param>
    /// <param name="reason">
    /// Why, at most <see cref="ProtocolConstants.KickReasonMaxBytes"/> bytes once encoded; longer reasons are truncated on the wire.
    /// </param>
    /// <returns>This request, so several may be chained.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="code"/> is outside <c>4100-4999</c>. Every lower code already means something specific to every client — 1002 framing, 1013 overload,
    /// 4003 admission — so borrowing one makes an application's decision read as an engine fault and changes whether SDKs reconnect.
    /// </exception>
    public SessionRequest Kick(ushort code, string reason = null)
    {
        if (code is < CloseCodes.FirstApplicationCode or > CloseCodes.LastApplicationCode)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code,
                $"A kick's close code is {CloseCodes.FirstApplicationCode}-{CloseCodes.LastApplicationCode}: the application range. Lower codes belong to " +
                "the protocol and tell every SDK something different about whether to come back.");
        }

        _segment.Add(_session, SessionRequestKind.Kick, 0, reason, code);
        return this;
    }
}
