using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Declares a named set of observers — "player", "god", "spectator" — that a session is bound to as a whole.
/// </summary>
/// <remarks>
/// A profile exists so interest is a policy an application states once rather than a sequence of requests every connection has to get right. Switching a
/// session's profile replaces its whole interest atomically and resets its view, which is the only honest way to do it: a half-switched session would be
/// told about entities under one rule and forgotten about them under another.
/// </remarks>
[PublicAPI]
public sealed class ProfileBuilder
{
    private readonly ProfileDeclaration _profile;

    internal ProfileBuilder(ProfileDeclaration profile) => _profile = profile;

    /// <summary>
    /// Raises how many observers this profile may declare, for an application that admits sessions with a matching
    /// <see cref="SessionLimits.MaxObservers"/>.
    /// </summary>
    /// <param name="count">The profile's own ceiling, at least 1. Defaults to <see cref="SessionLimits.DefaultMaxObservers"/>.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <b>This ceiling is the declaration's, never a session's.</b> A profile is declared once, at <c>Start</c>, and bound by whichever sessions ask for it;
    /// the admission hook is what decides whether a given session may hold that many observers, and its number is the one that binds at run time. Capping the
    /// declaration at the <i>default</i> limit instead — which is what this replaced — made a session admitted with a higher limit unable to ever bind a
    /// profile that used it, because the profile could not be written in the first place.
    /// </remarks>
    public ProfileBuilder MaxObservers(int count)
    {
        _profile.SetMaxObservers(count);
        return this;
    }

    /// <summary>
    /// Who says this profile's entities changed (ADR-067). Default <see cref="PushDetection.Explicit"/>: the application, by calling
    /// <see cref="SubscriptionsCommands.Replicate{TArchetype}(in ClusterRef{TArchetype}, int)"/> after each replicated write.
    /// </summary>
    /// <param name="detection">The mode. <see cref="PushDetection.Automatic"/> is refused unless
    /// <see cref="SubscriptionsOptions.AllowAutomaticPushDetection"/> is set.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// An entity is projected only when it is pushed — by the application, or by the engine for a spawn, a destroy, a <c>WriteSpatial</c>, a migration or a
    /// client still extrapolating it — and a session learns about it from its own geometry rather than from a per-session known-set.
    /// </remarks>
    public ProfileBuilder Detection(PushDetection detection)
    {
        _profile.PushDetection = detection;
        return this;
    }

    /// <summary>
    /// Serves this profile's sessions one tick in <paramref name="ticks"/>, staggered by session; each frame carries everything since the session's last
    /// one, replayed from the push log.
    /// </summary>
    /// <param name="ticks">1, 2 or 4.</param>
    /// <returns>This builder.</returns>
    public ProfileBuilder Every(int ticks)
    {
        if (ticks is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "A profile is served every 1, 2 or 4 ticks.");
        }

        _profile.TickDivisor = ticks;
        return this;
    }

    /// <summary>
    /// Everything, of the archetypes named with <see cref="ObserverBuilder.Of{TArchetype}"/>. For a tool, a viewer bot, or a world small enough that the whole
    /// of it is the interesting part. Cells are delivered in grid order behind one cursor, then every event of the tick.
    /// </summary>
    /// <returns>The observer's builder.</returns>
    public ObserverBuilder World() => _profile.Add(new ObserverDeclaration(ObserverKind.World));

    /// <summary>
    /// A radius around a point, an entity, or the session's controlled entity.
    /// </summary>
    /// <param name="radius">The enter radius, in metres.</param>
    /// <param name="leave">The leave radius, in metres — larger than <paramref name="radius"/>. Zero: no band.</param>
    /// <param name="max">
    /// The largest radius a session of this profile can be given at run time through <see cref="SubscriptionsCommands.SetRadius"/> — a player boarding
    /// an aircraft. Zero: the session's radius is fixed. The window is sized for it at <c>Start</c>, so it counts against the window bound.
    /// </param>
    /// <returns>The observer's builder.</returns>
    /// <remarks>
    /// <para>
    /// The two radii are not a refinement: an entity hovering on one boundary would enter and leave every tick, and every re-entry costs a full enter record.
    /// Hysteresis is what makes the cost of jitter zero instead of unbounded.
    /// </para>
    /// <para>
    /// <b>The band is a bound, not per-entity memory</b> (09 § 3): an entity whose true position is within <paramref name="radius"/> is held, one past
    /// <paramref name="leave"/> is not, and between them it may be either. The profile's sessions test the midpoint <c>R′ = (R + L) / 2</c> against a
    /// position that moves only past half the band, so an entity oscillating by less than that never flaps.
    /// </para>
    /// </remarks>
    public ObserverBuilder Sphere(double radius, double leave = 0, double max = 0)
    {
        if (!double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "A sphere needs a positive, finite radius.");
        }

        if (leave != 0 && (!double.IsFinite(leave) || leave <= radius))
        {
            throw new ArgumentOutOfRangeException(nameof(leave), leave, "A sphere's leave radius must be larger than its enter radius.");
        }

        var effective = leave > 0 ? (radius + leave) / 2d : radius;
        if (max != 0 && (!double.IsFinite(max) || max < effective))
        {
            throw new ArgumentOutOfRangeException(nameof(max), max,
                $"A sphere's largest run-time radius must be at least the radius its sessions start with, {effective} m (the band's midpoint when a leave "
                + "radius is declared).");
        }

        return _profile.Add(new ObserverDeclaration(ObserverKind.Sphere) { Radius = radius, LeaveRadius = leave, MaxRadius = max });
    }

    /// <summary>
    /// A convex ground footprint the client sends through the built-in <c>ClientRegion</c> command — a camera's view, clipped at the horizon.
    /// </summary>
    /// <param name="maxEdgeM">
    /// The widest the region's bounding box may be on any axis, in metres. A client asking for more is clamped about its centroid, not refused. It sizes every
    /// session's window, <c>⌈maxEdgeM / c⌉ + 5</c> cells per axis, which counts against the window bound at <c>Start</c> (09 § 7).
    /// </param>
    /// <returns>The observer's builder.</returns>
    /// <remarks>
    /// A session holds the entities whose visibility position lies in the region it last sent and whose cell it has been delivered; until it sends one it
    /// holds nothing. <see cref="ObserverBuilder.Near"/> caps what it holds, by whole cells nearest the region's centroid.
    /// </remarks>
    public ObserverBuilder ClientRegion(double maxEdgeM)
    {
        if (!double.IsFinite(maxEdgeM) || maxEdgeM <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEdgeM), maxEdgeM, "A client region needs a positive, finite edge limit.");
        }

        return _profile.Add(new ObserverDeclaration(ObserverKind.ClientRegion) { MaxEdgeM = maxEdgeM });
    }

    /// <summary>
    /// Per-cell counts per archetype, for a view too wide to send entities for: a strategic map, a far tier, a heat map.
    /// </summary>
    /// <param name="tileM">The tile's edge, in metres: a whole number of replication cells.</param>
    /// <param name="rateHz">How often the counts are refreshed, in hertz.</param>
    /// <param name="radiusM">
    /// The tiles a session is sent: those within this distance of its sphere's anchor; 0 for every tile. A <c>World</c> profile's aggregate covers every tile.
    /// </param>
    /// <returns>The observer's builder.</returns>
    /// <remarks>
    /// A tier (09 § 5, § 8): declared beside the profile's one entity observer (<c>World</c> or <c>Sphere</c>), it sends the tiles' counts in <c>AGG</c>
    /// blocks — never entity records — refreshed at most <paramref name="rateHz"/> times a second, each time the tiles that changed. Every archetype it
    /// counts must be one some profile replicates.
    /// </remarks>
    public ObserverBuilder Aggregate(double tileM, double rateHz, double radiusM = 0)
    {
        if (!double.IsFinite(radiusM) || radiusM < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusM), radiusM, "An aggregate's radius is zero (every tile) or a positive, finite distance.");
        }

        if (!double.IsFinite(tileM) || tileM <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tileM), tileM, "An aggregate observer needs a positive, finite tile edge.");
        }

        if (!double.IsFinite(rateHz) || rateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rateHz), rateHz, "An aggregate observer needs a positive, finite rate.");
        }

        return _profile.Add(new ObserverDeclaration(ObserverKind.Aggregate) { TileM = tileM, RateHz = rateHz, AggregateRadiusM = radiusM });
    }
}

/// <summary>
/// A named profile and the observers it holds.
/// </summary>
[PublicAPI]
public sealed class ProfileDeclaration
{
    private readonly List<ObserverDeclaration> _observers = [];

    internal ProfileDeclaration(string name) => Name = name;

    /// <summary>The profile's name, as a session request and the catalog refer to it.</summary>
    public string Name { get; }

    /// <summary>The observers, in declaration order.</summary>
    public IReadOnlyList<ObserverDeclaration> Observers => _observers;

    /// <summary>
    /// How many observers this profile may declare. <see cref="SessionLimits.DefaultMaxObservers"/> unless <see cref="ProfileBuilder.MaxObservers"/> raised
    /// it; a session's own <see cref="SessionLimits.MaxObservers"/> is enforced at admission, not here.
    /// </summary>
    public int MaxObservers { get; private set; } = SessionLimits.DefaultMaxObservers;

    /// <summary>Who signals a change for this profile's archetypes. See <see cref="ProfileBuilder.Detection"/>.</summary>
    public PushDetection PushDetection { get; internal set; }

    /// <summary>This profile's sessions are served one tick in this many. See <see cref="ProfileBuilder.Every"/>.</summary>
    public int TickDivisor { get; internal set; } = 1;

    /// <inheritdoc/>
    public override string ToString() => $"{Name}: {_observers.Count} observer(s)";

    internal void SetMaxObservers(int count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "A profile holds at least one observer.");
        }

        if (count < _observers.Count)
        {
            throw new InvalidOperationException(
                $"Profile '{Name}' already declares {_observers.Count} observer(s) and cannot lower its ceiling to {count}. Set the ceiling before the " +
                "observers, so the declaration reads in the order it is checked.");
        }

        MaxObservers = count;
    }

    internal ObserverBuilder Add(ObserverDeclaration observer)
    {
        if (_observers.Count == MaxObservers)
        {
            throw new InvalidOperationException(
                $"Profile '{Name}' would hold a {_observers.Count + 1}th observer; its ceiling is {MaxObservers}. An observer is a spatial query per " +
                "session per tick, so the cap bounds query cost, not bandwidth. Raise it with MaxObservers(n) and admit sessions with a matching " +
                "SessionLimits.MaxObservers — admission is what enforces a session's own limit.");
        }

        _observers.Add(observer);
        return new ObserverBuilder(observer);
    }
}
