using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// The declared profiles, resolved once at <c>Start</c>: the archetypes each one observes as plan indices, its observer's shape and radius, how it detects
/// changes and how often its sessions are served.
/// </summary>
/// <remarks>
/// Resolved once so the tick path never looks an archetype up by <see cref="Type"/> and never walks a declaration. A profile naming an archetype with no
/// projection is refused here rather than skipped: an observer pointed at an archetype clients can never be sent is a declaration that would do nothing, and
/// doing nothing quietly is how a system comes to be believed to run.
/// </remarks>
internal sealed class SubscriptionProfiles
{
    private readonly struct CompiledProfile
    {
        public string Name { get; init; }

        /// <summary>Plan indices, deduplicated and in declaration order. Empty for a profile whose observer reaches nothing.</summary>
        public int[] ArchetypeIndices { get; init; }

        /// <summary>Whether the observer is <c>World</c> rather than a <c>Sphere</c>.</summary>
        public bool World { get; init; }

        /// <summary>The sphere's radius, in world units; zero for <c>World</c>.</summary>
        public double Radius { get; init; }

        /// <summary>Whether the engine compares every live entity instead of waiting for <c>Replicate</c>.</summary>
        public bool Automatic { get; init; }

        /// <summary>The profile's sessions are served one tick in this many.</summary>
        public int TickDivisor { get; init; }
    }

    private readonly CompiledProfile[] _profiles;
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly SessionTable _sessions;
    private readonly int _planCount;

    /// <summary>Resolves every declared profile against the compiled plans.</summary>
    /// <param name="plans">The compiled plans.</param>
    /// <param name="registry">The declarations.</param>
    /// <param name="sessions">The session table, which names the profile each session is bound to.</param>
    /// <exception cref="InvalidOperationException">An observer names an archetype with no projection, or a sphere has no positive radius.</exception>
    /// <exception cref="NotSupportedException">A profile declares an observer shape, or a number of observers, that is not built.</exception>
    public SubscriptionProfiles(CompiledProjectionPlan[] plans, SubscriptionsRegistry registry, SessionTable sessions)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
        _planCount = plans.Length;
        _profiles = new CompiledProfile[registry.Profiles.Count];
        var indices = new List<int>();
        for (var p = 0; p < registry.Profiles.Count; p++)
        {
            var declaration = registry.Profiles[p];
            _byName[declaration.Name] = p;
            if (declaration.Observers.Count == 0)
            {
                // A profile that observes nothing serves its sessions nothing; TryGetProfile reports it as not served.
                _profiles[p] = new CompiledProfile { Name = declaration.Name, ArchetypeIndices = [], TickDivisor = declaration.TickDivisor };
                continue;
            }

            if (declaration.Observers.Count > 1)
            {
                // The registry refuses this at Start; a registry built directly, unfrozen, reaches here.
                throw new NotSupportedException(
                    $"Profile '{declaration.Name}' declares {declaration.Observers.Count} observers; a profile is served through exactly one World or Sphere.");
            }

            var observer = declaration.Observers[0];
            if (observer.Kind is not (ObserverKind.World or ObserverKind.Sphere))
            {
                throw new NotSupportedException(
                    $"Profile '{declaration.Name}' declares a {observer.Kind} observer, which a later phase builds. World and Sphere ship.");
            }

            if (observer.Kind == ObserverKind.Sphere && (!double.IsFinite(observer.Radius) || observer.Radius <= 0))
            {
                throw new InvalidOperationException(
                    $"Profile '{declaration.Name}' declares a Sphere observer with radius {observer.Radius}. A sphere needs a positive radius.");
            }

            indices.Clear();
            foreach (var archetype in observer.Archetypes)
            {
                var index = IndexOfArchetype(plans, archetype);
                if (index < 0)
                {
                    throw new InvalidOperationException(
                        $"Profile '{declaration.Name}' observes '{archetype.Name}', which declares no projection. An observer reaches an archetype through its "
                        + "projection, so declare one with subs.Archetype<" + archetype.Name + ">(...) or drop it from the profile.");
                }

                if (!indices.Contains(index))
                {
                    indices.Add(index);
                }
            }

            _profiles[p] = new CompiledProfile
            {
                Name = declaration.Name,
                ArchetypeIndices = indices.ToArray(),
                World = observer.Kind == ObserverKind.World,
                Radius = observer.Kind == ObserverKind.Sphere ? observer.Radius : 0d,
                Automatic = declaration.PushDetection == PushDetection.Automatic,
                TickDivisor = declaration.TickDivisor,
            };
        }
    }

    /// <summary>The profile a session is bound to, or <see langword="false"/> when it has none or its observer reaches nothing.</summary>
    /// <param name="session">The session.</param>
    /// <param name="archetypes">The plan indices the profile observes.</param>
    /// <param name="world">Whether the profile's observer is <c>World</c> rather than a sphere.</param>
    /// <param name="divisor">The profile's tick divisor: its sessions are served one tick in this many.</param>
    /// <returns>Whether the session is served.</returns>
    public bool TryGetProfile(SessionId session, out int[] archetypes, out bool world, out int divisor)
    {
        archetypes = null;
        world = false;
        divisor = 1;
        var name = _sessions.ProfileName(session);
        if (name == null || !_byName.TryGetValue(name, out var index) || _profiles[index].ArchetypeIndices.Length == 0)
        {
            return false;
        }

        archetypes = _profiles[index].ArchetypeIndices;
        world = _profiles[index].World;
        divisor = _profiles[index].TickDivisor;
        return true;
    }

    /// <summary>Which plan indices some profile observes.</summary>
    public bool[] ObservedArchetypes => Archetypes(automaticOnly: false);

    /// <summary>Which plan indices a profile with automatic detection observes.</summary>
    public bool[] AutomaticArchetypes => Archetypes(automaticOnly: true);

    /// <summary>The largest sphere radius any profile declares, or zero when every profile is <c>World</c>.</summary>
    public double MaxRadius
    {
        get
        {
            var r = 0d;
            foreach (var profile in _profiles)
            {
                r = Math.Max(r, profile.Radius);
            }

            return r;
        }
    }

    private bool[] Archetypes(bool automaticOnly)
    {
        var result = new bool[_planCount];
        foreach (var profile in _profiles)
        {
            if (automaticOnly && !profile.Automatic)
            {
                continue;
            }

            foreach (var a in profile.ArchetypeIndices)
            {
                result[a] = true;
            }
        }

        return result;
    }

    private static int IndexOfArchetype(CompiledProjectionPlan[] plans, Type archetype)
    {
        for (var i = 0; i < plans.Length; i++)
        {
            if (plans[i].ArchetypeType == archetype)
            {
                return i;
            }
        }

        return -1;
    }
}
