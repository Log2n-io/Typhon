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
    /// <summary>Everything, of the listed archetypes. Sessions that hold only this and are fully synced share one encoded frame.</summary>
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
/// <b>Phase 1 ships <see cref="ObserverKind.World"/> only.</b> The other three are declarable today and refused at <c>Start</c> with the phase that builds
/// them, which is deliberate: the alternative is an API that grows verbs later, so every application written against it has to be revisited when the verb it
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
        _observer.FollowsControlled = false;
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
        _observer.FollowsControlled = false;
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

    /// <summary>Caps the bytes this observer's records may take in one frame.</summary>
    /// <param name="bytes">The byte budget.</param>
    /// <returns>This builder.</returns>
    public ObserverBuilder Budget(int bytes)
    {
        if (bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "An observer budget is a positive byte count.");
        }

        _observer.ByteBudget = bytes;
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

    /// <summary>A client region's longest accepted edge, in metres.</summary>
    public double MaxEdgeM { get; internal set; }

    /// <summary>An aggregate observer's tile edge, in metres.</summary>
    public double TileM { get; internal set; }

    /// <summary>An aggregate observer's refresh rate, in hertz.</summary>
    public double RateHz { get; internal set; }

    /// <summary>The near tier's entity budget; 0 when none was declared.</summary>
    public int NearBudget { get; internal set; }

    /// <summary>The far tier's tile edge in metres; 0 when there is no far tier.</summary>
    public double FarTileM { get; internal set; }

    /// <summary>The far tier's refresh rate in hertz; 0 when there is no far tier.</summary>
    public double FarMaxHz { get; internal set; }

    /// <summary>The observer's byte budget; 0 when none was declared.</summary>
    public int ByteBudget { get; internal set; }

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
