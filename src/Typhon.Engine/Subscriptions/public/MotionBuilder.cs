using JetBrains.Annotations;
using System;
using System.Linq.Expressions;

namespace Typhon.Engine;

/// <summary>
/// Configures how an archetype's position travels: how wrong a client's extrapolation may get before a new motion segment is sent, what counts as a
/// teleport, and how long a moving entity may go without a refresh.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of this reaches the catalog.</b> Tolerance, teleport speed and segment age are server policy: they decide when the engine emits a segment, and a
/// client that received the segment needs none of them to extrapolate from it. What the catalog does carry is the velocity codec whose width
/// <see cref="Teleport"/> sizes — the one place this policy becomes a wire fact.
/// </para>
/// <para>
/// Every value is per archetype, because a spaceship and a walking NPC do not deserve the same: a tolerance tight enough for a duel is wasted bandwidth on a
/// cargo hauler, and a teleport threshold that suits the hauler would interpolate a duelist across the arena.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class MotionBuilder
{
    private readonly MotionProjection _motion = new();

    internal MotionBuilder()
    {
    }

    internal MotionProjection Motion => _motion;

    /// <summary>
    /// The extrapolation error, in metres, at which the engine gives the client a new segment. Default 5 cm.
    /// </summary>
    /// <param name="metres">The error budget, above zero.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// It buys bandwidth directly: doubling it roughly halves the segments a curving entity produces, at the cost of a client that is up to twice as wrong
    /// between them. It cannot go below the position codec's own quantum — the client could not represent the difference.
    /// </remarks>
    public MotionBuilder Tolerance(double metres)
    {
        if (!double.IsFinite(metres) || metres <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metres), metres, "Tolerance needs a positive, finite distance.");
        }

        _motion.ToleranceMetres = metres;
        return this;
    }

    /// <summary>
    /// The speed above which a step is a discontinuity rather than movement: the client cuts its extrapolation and jumps, and the segment's epoch advances.
    /// </summary>
    /// <param name="maxSpeedMps">The fastest legitimate speed, in metres per second.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// <b>Give it a margin — about 1.5× the fastest thing that legitimately moves.</b> A mover travelling at exactly the threshold rounds over it on some
    /// ticks and not others, so the client sees a smooth run punctuated by jumps for no reason the simulation would recognise.
    /// </para>
    /// <para>
    /// It also sizes the velocity codec: the widest displacement one tick can carry is this speed times the tick period times the runtime's largest allowed
    /// tick multiplier, rounded up to 8, 16, 24 or 32 bits. A generous threshold therefore costs a little bandwidth on every segment, which is the honest
    /// trade — the alternative is a fixed width that is either wasteful or wrong.
    /// </para>
    /// </remarks>
    public MotionBuilder Teleport(double maxSpeedMps)
    {
        if (!double.IsFinite(maxSpeedMps) || maxSpeedMps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSpeedMps), maxSpeedMps, "Teleport needs a positive, finite speed.");
        }

        _motion.TeleportMaxSpeedMps = maxSpeedMps;
        return this;
    }

    /// <summary>
    /// How long a <b>moving</b> entity may keep the same segment before the engine refreshes it, in seconds. Default 5 s. A stationary entity never gets one:
    /// nothing drifts when the velocity is zero, so a heartbeat would be pure cost.
    /// </summary>
    /// <param name="seconds">The maximum segment age, above zero.</param>
    /// <returns>This builder.</returns>
    public MotionBuilder MaxAge(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "MaxAge needs a positive, finite duration.");
        }

        _motion.MaxAgeSeconds = seconds;
        return this;
    }

    /// <summary>
    /// Takes the velocity from a field the simulation already maintains instead of measuring it, for a simulation that does not move its entities every tick
    /// and whose measured displacement would therefore read as a stop-start stutter.
    /// </summary>
    /// <typeparam name="TComponent">The component holding the velocity.</typeparam>
    /// <typeparam name="TField">The velocity field's type.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="selector">Selects the velocity field.</param>
    /// <returns>This builder.</returns>
    public MotionBuilder VelocityFrom<TComponent, TField>(Comp<TComponent> component, Expression<Func<TComponent, TField>> selector)
        where TComponent : unmanaged
    {
        _motion.VelocityComponentName = typeof(TComponent).Name;
        _motion.VelocityFieldName = SubscriptionsNames.SelectorField(selector, "VelocityFrom");
        _motion.VelocityComponentTypeId = SubscriptionsNames.ComponentTypeId(component);
        return this;
    }

    /// <summary>
    /// Sizes the velocity codec for the nominal tick period only, ignoring the runtime's overload time dilation.
    /// </summary>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Under overload the engine ticks less often and each tick covers more time, so a displacement-per-tick codec has to be wide enough for the slowest rate
    /// the runtime may fall back to — up to 2.5 bits more than the nominal rate needs, on every segment, forever, for a state the server should rarely be in.
    /// An archetype whose movement is bounded per tick rather than per second — something that steps a fixed distance — pays that for nothing, and says so
    /// here. The cost of being wrong is a clamped velocity on a dilated tick, not a decode error.
    /// </remarks>
    public MotionBuilder IgnoreTickDilation()
    {
        _motion.IgnoresTickDilation = true;
        return this;
    }
}

/// <summary>
/// What a <see cref="MotionBuilder"/> declared, as the compiler and the catalog builder read it back. Zero on a numeric member means "the engine's default".
/// </summary>
[PublicAPI]
public sealed class MotionProjection
{
    /// <summary>The extrapolation error budget in metres, or 0 for the engine default (5 cm).</summary>
    public double ToleranceMetres { get; internal set; }

    /// <summary>The teleport threshold in metres per second, or 0 when the archetype declared none.</summary>
    public double TeleportMaxSpeedMps { get; internal set; }

    /// <summary>The segment heartbeat in seconds, or 0 for the engine default (5 s).</summary>
    public double MaxAgeSeconds { get; internal set; }

    /// <summary>The component holding the declared velocity field, or <see langword="null"/> when velocity is measured.</summary>
    public string VelocityComponentName { get; internal set; }

    /// <summary>The declared velocity field, or <see langword="null"/> when velocity is measured.</summary>
    public string VelocityFieldName { get; internal set; }

    /// <summary>Whether the velocity codec is sized for the nominal tick period only.</summary>
    public bool IgnoresTickDilation { get; internal set; }

    /// <summary>The velocity field's component type id, for the projection compiler.</summary>
    internal int VelocityComponentTypeId { get; set; }
}
