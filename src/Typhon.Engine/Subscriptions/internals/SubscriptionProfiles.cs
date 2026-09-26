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

        /// <summary>Whether the observer is a <c>ClientRegion</c> (09 § 7): its sessions are served from the region they send.</summary>
        public bool Region { get; init; }

        /// <summary>A ClientRegion's widest accepted extent, in metres; zero for any other shape.</summary>
        public double MaxEdgeM { get; init; }

        /// <summary>A ClientRegion's near budget in entities (09 § 7); zero for none.</summary>
        public int NearBudget { get; init; }

        // The near budget's counts in PushReplication.NearCounts, plus one: 0 is none.
        public int NearCountsPlusOne { get; init; }

        /// <summary>The radius the sphere's sessions start with, in world units — the band's midpoint R′ with a leave radius; zero for <c>World</c>.</summary>
        public double Radius { get; init; }

        /// <summary>The largest radius a session of this profile can be given (<c>SetRadius</c>); <see cref="Radius"/> when it is fixed.</summary>
        public double MaxRadius { get; init; }

        /// <summary>A sphere's half-band h (09 § 2): what its archetypes' v̂ may trail by; zero for the other shapes. Shown to a debugging client.</summary>
        public double Slack { get; init; }

        /// <summary>Where the sphere is centred (09 § 6): the session's placed viewpoint, a fixed position, a bound entity, or the controlled one.</summary>
        public ViewpointSource Source { get; init; }

        /// <summary>The entity a <see cref="ViewpointSource.Bound"/> sphere follows.</summary>
        public EntityId BoundEntity { get; init; }

        /// <summary>The position a <see cref="ViewpointSource.Fixed"/> sphere is centred on.</summary>
        public Vector3D Placement { get; init; }

        /// <summary>The sphere's distance bands (09 § 9); none for <c>World</c>.</summary>
        public LodBands Bands { get; init; }

        public double AggregateTileM { get; init; }

        public double AggregateRateHz { get; init; }

        public double AggregateRadiusM { get; init; }

        public int[] AggregateArchetypes { get; init; }

        // The grid's index in PushReplication.Aggregates, plus one: 0 is none, which the default says.
        public int AggregateGridPlusOne { get; init; }

        public int AggregatePeriodTicks { get; init; }

        /// <summary>Whether the engine compares every live entity instead of waiting for <c>Replicate</c>.</summary>
        public bool Automatic { get; init; }

        /// <summary>The profile's sessions are served one tick in this many.</summary>
        public int TickDivisor { get; init; }
    }

    private readonly CompiledProfile[] _profiles;

    // Each profile's archetypes as a set of plan indices, beside the list: the geometry tests an event's archetype against it (09 § 12).
    private readonly ArchetypeSet[] _sets;
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly SessionTable _sessions;
    private readonly int _planCount;

    // Variants by realm kind (12-realms § 1.4): per declared profile and canonical kind index, the compiled profile serving it — the profile itself, one of
    // its variants (compiled after every declared profile), or -1 for a kind it excludes.
    private readonly int[] _variantOf;
    private readonly string[] _realmKinds;
    private readonly int _declaredCount;

    /// <summary>Resolves every declared profile, and every variant of one, against the compiled plans.</summary>
    /// <param name="plans">The compiled plans.</param>
    /// <param name="registry">The declarations.</param>
    /// <param name="sessions">The session table, which names the profile each session is bound to.</param>
    /// <param name="realmKinds">The realm kinds in canonical order — what a <c>REALM</c> block's kind index indexes; <c>[""]</c> when none are declared.</param>
    /// <exception cref="InvalidOperationException">An observer names an archetype with no projection, or a sphere has no positive radius.</exception>
    /// <exception cref="NotSupportedException">A profile declares an observer shape, or a number of observers, that is not built.</exception>
    public SubscriptionProfiles(CompiledProjectionPlan[] plans, SubscriptionsRegistry registry, SessionTable sessions, string[] realmKinds = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
        _planCount = plans.Length;
        _realmKinds = realmKinds is { Length: > 0 } ? realmKinds : [""];
        var declared = registry.Profiles;
        _declaredCount = declared.Count;
        var total = declared.Count;
        foreach (var declaration in declared)
        {
            total += declaration.Variants.Count;
        }

        _profiles = new CompiledProfile[total];
        _sets = new ArchetypeSet[total];
        _variantOf = new int[declared.Count * _realmKinds.Length];
        var next = declared.Count;
        for (var p = 0; p < declared.Count; p++)
        {
            var declaration = declared[p];
            _byName[declaration.Name] = p;
            _profiles[p] = Compile(plans, declaration, declaration, p);
            for (var k = 0; k < _realmKinds.Length; k++)
            {
                _variantOf[(p * _realmKinds.Length) + k] = p;
            }

            foreach (var (kind, variant) in declaration.Variants)
            {
                var v = next++;
                _profiles[v] = Compile(plans, variant, declaration, v);
                _variantOf[(p * _realmKinds.Length) + DeclaredKind(declaration, kind)] = v;
            }

            foreach (var kind in declaration.Excluded)
            {
                _variantOf[(p * _realmKinds.Length) + DeclaredKind(declaration, kind)] = -1;
            }
        }

        sessions.BindProfiles(name => _byName.TryGetValue(name, out var index) ? index : -1);
    }

    // A variant's or an exclusion's kind: one the catalog lists (the registry refuses others at Start; a registry built directly reaches here).
    private int DeclaredKind(ProfileDeclaration profile, string kind)
    {
        var index = KindIndex(kind);
        return index >= 0 ? index : throw new InvalidOperationException(
            $"Profile '{profile.Name}' names realm kind '{kind}', which RealmKinds does not declare (12-realms § 2.7).");
    }

    /// <summary>A realm kind's canonical index, or -1 for a kind no declaration names.</summary>
    public int KindIndex(string kind) => Array.IndexOf(_realmKinds, kind ?? "");

    /// <summary>
    /// The compiled profile serving a session of declared profile <paramref name="profile"/> in a realm of kind <paramref name="kind"/> (canonical index):
    /// the profile, its variant for the kind, or -1 when the profile excludes it. A kind of -1 (no realm) is the profile itself.
    /// </summary>
    public int VariantOf(int profile, int kind) =>
        kind < 0 || (uint)profile >= (uint)_declaredCount || kind >= _realmKinds.Length ? profile : _variantOf[(profile * _realmKinds.Length) + kind];

    /// <summary>Whether compiled profile <paramref name="profile"/> serves anything: some observer reaches an archetype.</summary>
    public bool IsServed(int profile) => (uint)profile < (uint)_profiles.Length && _profiles[profile].ArchetypeIndices.Length > 0;

    /// <summary>Compiled profile <paramref name="profile"/>'s tick divisor: its sessions are served one tick in this many.</summary>
    public int DivisorOf(int profile) => _profiles[profile].TickDivisor;

    // One declaration — a profile, or a variant of one (owner) whose name, detection and rate class it keeps: those are profile-wide (12-realms § 1.4).
    private CompiledProfile Compile(CompiledProjectionPlan[] plans, ProfileDeclaration declaration, ProfileDeclaration owner, int slot)
    {
        var name = owner.Name;
        if (declaration.Observers.Count == 0)
        {
            // A profile that observes nothing serves its sessions nothing; TryGetProfile reports it as not served.
            return new CompiledProfile { Name = name, ArchetypeIndices = [], TickDivisor = owner.TickDivisor };
        }

        ObserverDeclaration observer = null;
        ObserverDeclaration aggregate = null;
        foreach (var o in declaration.Observers)
        {
            if (o.Kind == ObserverKind.Aggregate)
            {
                if (aggregate != null)
                {
                    // The registry refuses this at Start; a registry built directly, unfrozen, reaches here.
                    throw new NotSupportedException(
                        $"Profile '{name}' declares two aggregates; a profile holds at most one Aggregate beside its World, Sphere or ClientRegion.");
                }

                aggregate = o;
            }
            else
            {
                if (observer != null)
                {
                    // The registry refuses this at Start; a registry built directly, unfrozen, reaches here.
                    throw new NotSupportedException(
                        $"Profile '{name}' declares two entity observers; a profile is served through exactly one World, Sphere or ClientRegion.");
                }

                observer = o;
            }
        }

        if (observer == null)
        {
            throw new NotSupportedException(
                $"Profile '{name}' declares an Aggregate alone; an aggregate is a tier beside a World, Sphere or ClientRegion.");
        }

        if (observer.Kind == ObserverKind.Sphere && (!double.IsFinite(observer.Radius) || observer.Radius <= 0))
        {
            throw new InvalidOperationException(
                $"Profile '{name}' declares a Sphere observer with radius {observer.Radius}. A sphere needs a positive radius.");
        }

        var indices = new List<int>();
        foreach (var archetype in observer.Archetypes)
        {
            var index = IndexOfArchetype(plans, archetype);
            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"Profile '{name}' observes '{archetype.Name}', which declares no projection. An observer reaches an archetype through its "
                    + "projection, so declare one with subs.Archetype<" + archetype.Name + ">(...) or drop it from the profile.");
            }

            if (index >= ArchetypeSet.Capacity)
            {
                // The registry refuses a 256th archetype before a plan exists; a registry built directly reaches here.
                throw new NotSupportedException(
                    $"Profile '{name}' observes '{archetype.Name}' at plan index {index}. A profile's archetype set holds plan indices below "
                    + $"{ArchetypeSet.Capacity}.");
            }

            if (!indices.Contains(index))
            {
                indices.Add(index);
                _sets[slot].Add(index);
            }
        }

        return new CompiledProfile
        {
            Name = name,
            ArchetypeIndices = indices.ToArray(),
            World = observer.Kind == ObserverKind.World,
            Region = observer.Kind == ObserverKind.ClientRegion,
            MaxEdgeM = observer.Kind == ObserverKind.ClientRegion ? observer.MaxEdgeM : 0d,
            NearBudget = observer.Kind == ObserverKind.ClientRegion ? observer.NearBudget : 0,
            Radius = observer.Kind == ObserverKind.Sphere ? observer.EffectiveRadius : 0d,
            MaxRadius = observer.Kind == ObserverKind.Sphere ? Math.Max(observer.EffectiveRadius, observer.MaxRadius) : 0d,
            Slack = observer.Kind == ObserverKind.Sphere ? observer.VisibilitySlack : 0d,

            // The anchor (12-realms § 1.3): a Sphere's centre, and on every shape the source of the session's realm.
            Source = observer.FollowsControlled ? ViewpointSource.Controlled
                : observer.BoundEntity != EntityId.Null ? ViewpointSource.Bound
                : observer.Placement.HasValue ? ViewpointSource.Fixed
                : ViewpointSource.Placed,
            BoundEntity = observer.BoundEntity,
            Placement = observer.Placement ?? default,
            Bands = observer.Kind == ObserverKind.Sphere ? new LodBands(observer.Bands) : default,
            Automatic = owner.PushDetection == PushDetection.Automatic,
            TickDivisor = owner.TickDivisor,
            AggregateTileM = aggregate?.TileM ?? 0d,
            AggregateRateHz = aggregate?.RateHz ?? 0d,
            // A World's aggregate covers every tile and a ClientRegion's its hull (09 § 8): only a Sphere's has a radius.
            AggregateRadiusM = aggregate == null || observer.Kind != ObserverKind.Sphere ? 0d : aggregate.AggregateRadiusM,
            AggregateArchetypes = aggregate == null ? [] : AggregateIndices(plans, name, aggregate),
        };
    }

    private static int[] AggregateIndices(CompiledProjectionPlan[] plans, string profile, ObserverDeclaration aggregate)
    {
        var list = new List<int>();
        foreach (var archetype in aggregate.Archetypes)
        {
            var index = IndexOfArchetype(plans, archetype);
            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"Profile '{profile}' aggregates '{archetype.Name}', which declares no projection: an aggregate counts what replication serves.");
            }

            if (!list.Contains(index))
            {
                list.Add(index);
            }
        }

        list.Sort();
        return list.ToArray();
    }

    /// <summary>Whether profile <paramref name="profile"/> is served through a ClientRegion (09 § 7).</summary>
    public bool RegionOf(int profile) => _profiles[profile].Region;

    /// <summary>Whether profile <paramref name="profile"/> is served through a <c>World</c> observer.</summary>
    public bool IsWorld(int profile) => _profiles[profile].World;

    /// <summary>A ClientRegion profile's widest accepted extent, in metres.</summary>
    public double MaxEdgeOf(int profile) => _profiles[profile].MaxEdgeM;

    /// <summary>A ClientRegion profile's near budget and its counts' index in <see cref="PushReplication.NearCounts"/>; (0, −1) for none.</summary>
    public (int Budget, int Counts) NearOf(int profile) => (_profiles[profile].NearBudget, _profiles[profile].NearCountsPlusOne - 1);

    /// <summary>The widest extent any ClientRegion profile accepts, in metres; zero when none declares one. Every region window is sized for it.</summary>
    public double MaxRegionEdgeM
    {
        get
        {
            var edge = 0d;
            foreach (var profile in _profiles)
            {
                edge = Math.Max(edge, profile.MaxEdgeM);
            }

            return edge;
        }
    }

    /// <summary>
    /// The archetype sets the near budgets count (09 § 7): one per distinct set among the budgeted ClientRegion profiles, bound to them. Before the first
    /// tick.
    /// </summary>
    internal List<ArchetypeSet> BindNearCounts()
    {
        var sets = new List<ArchetypeSet>();
        for (var i = 0; i < _profiles.Length; i++)
        {
            if (_profiles[i].NearBudget <= 0)
            {
                continue;
            }

            var index = -1;
            for (var s = 0; s < sets.Count && index < 0; s++)
            {
                index = Same(sets[s], _sets[i]) ? s : -1;
            }

            if (index < 0)
            {
                index = sets.Count;
                sets.Add(_sets[i]);
            }

            _profiles[i] = _profiles[i] with { NearCountsPlusOne = index + 1 };
        }

        return sets;
    }

    private static bool Same(ArchetypeSet a, ArchetypeSet b)
    {
        for (var w = 0; w < ArchetypeSet.Words; w++)
        {
            if (a[w] != b[w])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A profile's aggregate (09 § 8): its grid in <see cref="PushReplication.Aggregates"/>, refresh period and radius; grid −1 for none.</summary>
    public (int Grid, int PeriodTicks, double RadiusM) AggregateOf(int profile) =>
        (uint)profile < (uint)_profiles.Length
            ? (_profiles[profile].AggregateGridPlusOne - 1, _profiles[profile].AggregatePeriodTicks, _profiles[profile].AggregateRadiusM)
            : (-1, 0, 0d);

    /// <summary>Binds each profile's aggregate to its grid and refresh period, once the grids exist (runtime Start).</summary>
    internal void BindAggregates(Func<double, int[], int> gridOf, double tickPeriodSeconds)
    {
        for (var i = 0; i < _profiles.Length; i++)
        {
            var p = _profiles[i];
            if (p.AggregateTileM > 0)
            {
                _profiles[i] = p with
                {
                    AggregateGridPlusOne = gridOf(p.AggregateTileM, p.AggregateArchetypes) + 1,
                    AggregatePeriodTicks = Math.Max(1, (int)Math.Round(1d / (p.AggregateRateHz * tickPeriodSeconds))),
                };
            }
        }
    }

    /// <summary>Every profile's aggregate: its tile edge and the plan indices it counts.</summary>
    internal IEnumerable<(double TileM, int[] Archetypes)> AggregateDeclarations()
    {
        foreach (var p in _profiles)
        {
            if (p.AggregateTileM > 0)
            {
                yield return (p.AggregateTileM, p.AggregateArchetypes);
            }
        }
    }

    /// <summary>The profile a session is bound to, or <see langword="false"/> when it has none or its observer reaches nothing.</summary>
    /// <param name="session">The session.</param>
    /// <param name="profile">The profile's index, for <see cref="SetOf"/>.</param>
    /// <param name="world">Whether the profile's observer is <c>World</c> rather than a sphere.</param>
    /// <param name="divisor">The profile's tick divisor: its sessions are served one tick in this many.</param>
    /// <returns>Whether the session is served.</returns>
    /// <remarks>Tick side: the index is the session table's, resolved when the profile was set, so no name is hashed here.</remarks>
    public bool TryGetProfile(SessionId session, out int profile, out bool world, out int divisor)
    {
        world = false;
        divisor = 1;
        var index = _sessions.ProfileIndex(session);
        profile = index;
        if (index < 0 || _profiles[index].ArchetypeIndices.Length == 0)
        {
            return false;
        }

        world = _profiles[index].World;
        divisor = _profiles[index].TickDivisor;
        return true;
    }

    /// <summary>The archetypes profile <paramref name="profile"/> observes, as a set of plan indices.</summary>
    /// <param name="profile">An index <see cref="TryGetProfile"/> returned.</param>
    /// <returns>The set, by reference: it lives as long as the runtime.</returns>
    public ref readonly ArchetypeSet SetOf(int profile) => ref _sets[profile];

    /// <summary>Which plan indices some profile observes.</summary>
    public bool[] ObservedArchetypes => Archetypes(automaticOnly: false);

    /// <summary>Which plan indices a profile with automatic detection observes.</summary>
    public bool[] AutomaticArchetypes => Archetypes(automaticOnly: true);

    /// <summary>
    /// The largest radius any session can take — every Sphere profile's R′ and declared maximum — or zero when every profile is <c>World</c>. The one
    /// window is sized for it (09 § 4).
    /// </summary>
    public double MaxRadius
    {
        get
        {
            var r = 0d;
            foreach (var profile in _profiles)
            {
                r = Math.Max(r, profile.MaxRadius);
            }

            return r;
        }
    }

    /// <summary>The radius a session of profile <paramref name="profile"/> starts with: its R′; zero for <c>World</c>.</summary>
    public double RadiusOf(int profile) => _profiles[profile].Radius;

    /// <summary>The largest radius a session of profile <paramref name="profile"/> can be given; its R′ when the radius is fixed.</summary>
    public double MaxRadiusOf(int profile) => _profiles[profile].MaxRadius;

    /// <summary>A sphere profile's half-band h; zero for the other shapes.</summary>
    public double SlackOf(int profile) => _profiles[profile].Slack;

    /// <summary>Where profile <paramref name="profile"/>'s sphere is centred.</summary>
    public ViewpointSource SourceOf(int profile) => _profiles[profile].Source;

    /// <summary>The entity profile <paramref name="profile"/>'s sphere follows, when its source is <see cref="ViewpointSource.Bound"/>.</summary>
    public EntityId BoundEntityOf(int profile) => _profiles[profile].BoundEntity;

    /// <summary>The fixed centre of profile <paramref name="profile"/>'s sphere, when its source is <see cref="ViewpointSource.Fixed"/>.</summary>
    public Vector3D PlacementOf(int profile) => _profiles[profile].Placement;

    /// <summary>Profile <paramref name="profile"/>'s distance bands.</summary>
    public LodBands BandsOf(int profile) => _profiles[profile].Bands;

    /// <summary>
    /// The fold's phase and window over every profile's bands (09 § 9): the smallest and the largest period declared, 0 and 0 when no profile has bands.
    /// Nested powers of two make the smallest period's flush ticks a superset of every band's, and the largest period the history a flush must cover.
    /// </summary>
    public (int Phase, int Window) FarFold
    {
        get
        {
            var phase = 0;
            var window = 0;
            foreach (var profile in _profiles)
            {
                if (profile.Bands.Count == 0)
                {
                    continue;
                }

                phase = phase == 0 ? profile.Bands.MinEvery : Math.Min(phase, profile.Bands.MinEvery);
                window = Math.Max(window, profile.Bands.MaxEvery);
            }

            return (phase, window);
        }
    }

    /// <summary>The name of profile <paramref name="profile"/>, for messages.</summary>
    public string NameOf(int profile) => _profiles[profile].Name;

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
