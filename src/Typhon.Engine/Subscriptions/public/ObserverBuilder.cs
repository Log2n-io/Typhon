using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// The shape of one region of interest.
/// </summary>
[PublicAPI]
public enum ObserverKind
{
    /// <summary>Everything, of the listed archetypes: the whole world, filled cell by cell under the enter budget, then kept by its events.</summary>
    World = 0,

    /// <summary>A radius with hysteresis, around a bound entity, the controlled entity, or a fixed point.</summary>
    Sphere = 1,

    /// <summary>A convex ground footprint the client supplies through the built-in <c>ClientRegion</c> command, clamped by the profile.</summary>
    ClientRegion = 2,

    /// <summary>Per-cell counts per archetype, for a view too wide to send entities for.</summary>
    Aggregate = 3,
}

/// <summary>
/// Declares one observer: what region it covers and which archetypes it reaches through.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ObserverKind.World"/> and <see cref="ObserverKind.Sphere"/> ship.</b> The other two are declarable today and refused at <c>Start</c> with
/// the phase that builds them, which is deliberate: the alternative is an API that grows verbs later, so every application written against it has to be revisited when the verb it
/// always wanted finally exists. Declaring the full shape now costs a clear error message and buys a public surface that does not move.
/// </para>
/// <para>
/// <b>An observer reaches an archetype only if that archetype is spatially indexed.</b> Data with no position — a market, a leaderboard, a player's own
/// inventory — is not something an observer can be pointed at; it travels as a source or as owner fields.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class ObserverBuilder
{
    private readonly ObserverDeclaration _observer;
    private bool _bandsDeclared;

    internal ObserverBuilder(ObserverDeclaration observer) => _observer = observer;

    /// <summary>
    /// Adds an archetype this observer reaches. One observer over several archetypes is what lets the engine walk a cell once for many of them.
    /// </summary>
    /// <typeparam name="TArchetype">The archetype.</typeparam>
    /// <returns>This builder.</returns>
    public ObserverBuilder Of<TArchetype>() where TArchetype : Archetype<TArchetype>
    {
        _observer.AddArchetype(typeof(TArchetype));
        return this;
    }

    /// <summary>Binds a sphere to an entity, so the region follows it.</summary>
    /// <param name="entity">The entity to follow.</param>
    /// <returns>This builder.</returns>
    public ObserverBuilder Bind(EntityId entity)
    {
        _observer.BoundEntity = entity;
        return this;
    }

    /// <summary>
    /// Places a sphere at a fixed world position.
    /// </summary>
    /// <param name="position">The centre, in world space.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// It takes a position rather than a pair of horizontal coordinates on purpose: which two axes are "the ground" is the application's convention, not the
    /// engine's, and a two-argument overload silently drops the third for anyone whose world is oriented the other way.
    /// </remarks>
    public ObserverBuilder At(Vector3D position)
    {
        _observer.Placement = position;
        return this;
    }

    /// <summary>Follows whatever entity the session controls, with no request from the application.</summary>
    /// <returns>This builder.</returns>
    public ObserverBuilder AroundControlled()
    {
        _observer.FollowsControlled = true;
        return this;
    }

    /// <summary>
    /// Caps the entities this observer's near tier sends; beyond it the near radius shrinks and the rest feeds the far tier.
    /// </summary>
    /// <param name="budget">The entity count the near tier is sized for.</param>
    /// <returns>This builder.</returns>
    public ObserverBuilder Near(int budget)
    {
        if (budget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), budget, "A near budget is a positive entity count.");
        }

        _observer.NearBudget = budget;
        return this;
    }

    /// <summary>
    /// Declares a Sphere's distance bands (09 § 9): beyond a fraction of the radius, an entity's updates are sent every N ticks.
    /// </summary>
    /// <param name="bands">The bands, innermost first — <c>b =&gt; b.Every(2, beyond: 0.5).Every(4, beyond: 0.75)</c>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The observer is not a Sphere, or its bands were already declared.</exception>
    public ObserverBuilder Bands(Action<BandBuilder> bands)
    {
        // Declared once, even empty: a second call is a second opinion about the same radius, and silently keeping either would hide one.
        ArgumentNullException.ThrowIfNull(bands);
        if (_observer.Kind != ObserverKind.Sphere)
        {
            throw new InvalidOperationException($"Distance bands are fractions of a Sphere's radius; a {_observer.Kind} observer has none.");
        }

        if (_bandsDeclared)
        {
            throw new InvalidOperationException("A Sphere's bands are declared once.");
        }

        _bandsDeclared = true;
        var builder = new BandBuilder();
        bands(builder);
        _observer.Bands = builder.Bands;
        return this;
    }

    /// <summary>
    /// Adds a far tier: per-cell counts per archetype beyond the near tier, at a bounded rate.
    /// </summary>
    /// <param name="tileM">The aggregation tile's edge, in metres.</param>
    /// <param name="maxHz">How often the far tier may be refreshed, in hertz.</param>
    /// <returns>This builder.</returns>
    public ObserverBuilder Far(double tileM, double maxHz)
    {
        if (!double.IsFinite(tileM) || tileM <= 0 || !double.IsFinite(maxHz) || maxHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tileM), tileM, "A far tier needs a positive tile edge and a positive rate.");
        }

        _observer.FarTileM = tileM;
        _observer.FarMaxHz = maxHz;
        return this;
    }

}

/// <summary>
/// What an <see cref="ObserverBuilder"/> declared, as the runtime reads it back at <c>Start</c>.
/// </summary>
[PublicAPI]
public sealed class ObserverDeclaration
{
    private readonly List<Type> _archetypes = [];

    internal ObserverDeclaration(ObserverKind kind) => Kind = kind;

    /// <summary>The region's shape.</summary>
    public ObserverKind Kind { get; }

    /// <summary>A sphere's enter radius, in metres.</summary>
    public double Radius { get; internal set; }

    /// <summary>A sphere's leave radius, in metres; 0 when the declaration left the hysteresis band to the engine.</summary>
    public double LeaveRadius { get; internal set; }

    /// <summary>A sphere's distance bands, innermost first (09 § 9); empty when every update is sent every tick.</summary>
    public IReadOnlyList<DistanceBand> Bands { get; internal set; } = [];

    /// <summary>A sphere's largest run-time radius, in metres (<c>SetRadius</c>); 0 when a session's radius is fixed.</summary>
    public double MaxRadius { get; internal set; }

    /// <summary>
    /// The radius a sphere's sessions test against: the band's midpoint <c>(R + L) / 2</c> with a leave radius, the enter radius without (09 § 3).
    /// </summary>
    public double EffectiveRadius => LeaveRadius > 0 ? (Radius + LeaveRadius) / 2d : Radius;

    /// <summary>
    /// The sphere's visibility slack <c>h_p</c> (09 § 2–3): half its band with a leave radius, the anchor slack's mirror <c>R / 48</c> without.
    /// </summary>
    public double VisibilitySlack => LeaveRadius > 0 ? (LeaveRadius - Radius) / 2d : Radius / 48d;

    /// <summary>A client region's longest accepted edge, in metres.</summary>
    public double MaxEdgeM { get; internal set; }

    /// <summary>An aggregate observer's tile edge, in metres.</summary>
    public double TileM { get; internal set; }

    /// <summary>An aggregate observer's refresh rate, in hertz.</summary>
    public double RateHz { get; internal set; }

    /// <summary>An aggregate observer's radius around the session's anchor; 0 for every tile.</summary>
    public double AggregateRadiusM { get; internal set; }

    /// <summary>The near tier's entity budget; 0 when none was declared.</summary>
    public int NearBudget { get; internal set; }

    /// <summary>The far tier's tile edge in metres; 0 when there is no far tier.</summary>
    public double FarTileM { get; internal set; }

    /// <summary>The far tier's refresh rate in hertz; 0 when there is no far tier.</summary>
    public double FarMaxHz { get; internal set; }

    /// <summary>The entity a sphere is bound to, or <see cref="EntityId.Null"/> when it is not bound to one.</summary>
    public EntityId BoundEntity { get; internal set; }

    /// <summary>A fixed placement, or <see langword="null"/> when the observer is bound or follows the controlled entity.</summary>
    public Vector3D? Placement { get; internal set; }

    /// <summary>Whether the observer follows the session's controlled entity.</summary>
    public bool FollowsControlled { get; internal set; }

    /// <summary>The archetypes this observer reaches, in declaration order.</summary>
    public IReadOnlyList<Type> Archetypes => _archetypes;

    /// <inheritdoc/>
    public override string ToString() => $"{Kind} over {_archetypes.Count} archetype(s)";

    internal void AddArchetype(Type archetype)
    {
        if (_archetypes.Contains(archetype))
        {
            throw new InvalidOperationException($"Observer {Kind} already reaches '{archetype.Name}'.");
        }

        _archetypes.Add(archetype);
    }
}
