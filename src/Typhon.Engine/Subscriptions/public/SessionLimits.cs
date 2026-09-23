using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// The per-session ceilings an application's admission hook hands back with a role. Every value left at zero means "use the operator's
/// <see cref="SubscriptionsOptions"/> value", so a limit is stated in exactly one place unless a session genuinely needs a different one.
/// </summary>
/// <remarks>
/// <para>
/// <b>These bound one connection; <see cref="SubscriptionsOptions"/> bounds the server.</b> Raising a session's frame size cannot raise the frame pool's
/// budget, and a session that asks for more than the operator allows gets the operator's number. That ordering is what makes admission safe to write against
/// untrusted input: the worst an application hook can do is be generous within rails it does not control.
/// </para>
/// <para>
/// A record, so an application derives one preset from another with <c>with</c> instead of repeating every field.
/// </para>
/// </remarks>
[PublicAPI]
public sealed record SessionLimits
{
    /// <summary>
    /// The outbound byte budget, in bytes per second. Zero means no per-session budget — the frame ceiling and the enter budget still apply. Within a budget
    /// records are deferred by priority and never dropped, so a small number slows a view down rather than corrupting it.
    /// </summary>
    public int BytesPerSecond { get; init; }

    /// <summary>
    /// How many observers this session may hold at once. Zero takes <see cref="SubscriptionsOptions.ObserversPerSession"/>. An observer is a query per tick,
    /// so this is the knob that bounds a session's query cost rather than its bandwidth — and, like every other limit here, it is resolved against the
    /// operator's value rather than being taken at face value.
    /// </summary>
    public int MaxObservers { get; init; }

    /// <summary>Largest frame this session may be sent, in bytes. Zero takes <see cref="SubscriptionsOptions.FrameBytes"/>.</summary>
    public int FrameBytes { get; init; }

    /// <summary>Largest message this session may send, in bytes. Zero takes <see cref="SubscriptionsOptions.ClientMessageBytes"/>.</summary>
    public int ClientMessageBytes { get; init; }

    /// <summary>
    /// Whether this session may be granted the <c>DEBUG</c> capability — engine-defined diagnostic blocks that expose grid, cluster and migration internals.
    /// Off by default because that is server structure, not game state.
    /// </summary>
    public bool AllowDebug { get; init; }

    /// <summary>
    /// The engine's default observer count: near, far, and two spare. It is the same number <see cref="SubscriptionsOptions.ObserversPerSession"/> ships with,
    /// and it is what a profile's own ceiling starts at — a session's limit is resolved against the operator's value, never against this.
    /// </summary>
    public const int DefaultMaxObservers = 4;

    /// <summary>Every limit at its operator default, and no debug. What an <c>Accept</c> with no limits means.</summary>
    public static SessionLimits Default { get; } = new();

    /// <summary>
    /// The tooling preset: operator defaults, plus the <c>DEBUG</c> capability. For an inspector, a recorder or a spectator bot the operator runs — anything
    /// that is allowed to see how the server is arranged, not only what it simulates.
    /// </summary>
    public static SessionLimits God { get; } = new() { AllowDebug = true };
}
