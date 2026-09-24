using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// What a metric measures.
/// </summary>
[PublicAPI]
public enum MetricKind
{
    /// <summary>A value that is read as it stands: a queue depth, a latency percentile, a count of live things.</summary>
    Gauge = 0,

    /// <summary>A total that only rises, wrapping at 2³². A client differences two samples; a skipped sample costs nothing.</summary>
    Counter = 1,
}

/// <summary>
/// The declaration surface for engine-owned replication: the projections clients may see, the profiles that decide what each session watches, the commands
/// clients may send, the events they hear and the metrics they are told about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reached through <see cref="TyphonRuntime.Subscriptions"/>, and configured before <see cref="TyphonRuntime.Start"/>.</b> Replication rides the tick, so
/// it belongs to the runtime rather than to <see cref="DatabaseEngine"/>; and everything here is compiled once at <c>Start</c>, which is why the registry
/// freezes there and refuses a late declaration instead of quietly not applying it.
/// </para>
/// <para>
/// <b>Declaring costs nothing at run time.</b> The registry holds names, codecs and policy; it has no per-tick presence at all. What the tick sees is the
/// compiled plan the projection compiler builds from it, in which every name has already become an offset.
/// </para>
/// <para>
/// <b>Malformed declarations are refused where they are written.</b> A duplicate wire name, a ninth change group, a 256th archetype, a 64-bit source with no
/// explicit narrowing and a metric under the reserved <c>typhon.</c> prefix all throw from the call that declared them. What cannot be judged from one
/// declaration — an observer shape or a source that a later phase builds — is refused at <c>Start</c>, with the phase named.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class SubscriptionsRegistry
{
    private readonly List<ArchetypeProjection> _archetypes = [];
    private readonly List<ProfileDeclaration> _profiles = [];
    private readonly List<CommandDeclaration> _commands = [];
    private readonly List<EventDeclaration> _events = [];
    private readonly List<MetricDeclaration> _metrics = [];
    private readonly List<SourceDeclaration> _sources = [];

    /// <summary>
    /// Creates a registry over the given options.
    /// </summary>
    /// <param name="options">The operator's replication options. <see langword="null"/> takes the defaults.</param>
    /// <remarks>
    /// The runtime builds its own from <see cref="RuntimeOptions.Subscriptions"/> and exposes it as <see cref="TyphonRuntime.Subscriptions"/>; that is the one
    /// the engine reads. Constructing one directly is for a test, or for an application that assembles its declarations somewhere other than beside its host.
    /// </remarks>
    public SubscriptionsRegistry(SubscriptionsOptions options = null) => Options = options ?? new SubscriptionsOptions();

    /// <summary>The operator's replication options.</summary>
    public SubscriptionsOptions Options { get; }

    /// <summary>Session kinds, admission and the tick-visible session lifecycle.</summary>
    public SubscriptionsSessions Sessions { get; } = new();

    /// <summary>Whether <c>Start</c> has compiled these declarations. A frozen registry refuses every further declaration.</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>The archetype projections, in declaration order.</summary>
    public IReadOnlyList<ArchetypeProjection> Archetypes => _archetypes;

    /// <summary>The interest profiles, in declaration order.</summary>
    public IReadOnlyList<ProfileDeclaration> Profiles => _profiles;

    /// <summary>The command types, in declaration order.</summary>
    public IReadOnlyList<CommandDeclaration> Commands => _commands;

    /// <summary>The replicated events, in declaration order.</summary>
    public IReadOnlyList<EventDeclaration> Events => _events;

    /// <summary>The application's metrics, in declaration order. The built-ins are not listed here: the engine owns them.</summary>
    public IReadOnlyList<MetricDeclaration> Metrics => _metrics;

    /// <summary>The shared sources, in declaration order.</summary>
    public IReadOnlyList<SourceDeclaration> Sources => _sources;

    /// <summary>
    /// Declares what clients may see of an archetype.
    /// </summary>
    /// <typeparam name="TArchetype">The archetype.</typeparam>
    /// <param name="configure">Declares its position, fields and owner section.</param>
    /// <returns>This registry.</returns>
    public SubscriptionsRegistry Archetype<TArchetype>(Action<ArchetypeProjectionBuilder> configure) where TArchetype : Archetype<TArchetype>
        => DeclareArchetype<TArchetype>(configure, isStatic: false);

    /// <summary>
    /// Declares an archetype whose replicated state never changes: its record is sent once, on enter, and never updated.
    /// </summary>
    /// <typeparam name="TArchetype">The archetype.</typeparam>
    /// <param name="configure">Declares its position and fields.</param>
    /// <returns>This registry.</returns>
    /// <remarks>
    /// This is not an optimization hint the engine may ignore. A static archetype has no change groups and no per-entity comparison at all, so a world of
    /// scenery costs the projection pass nothing per tick — which is the difference between replicating a landscape and replicating a simulation.
    /// </remarks>
    public SubscriptionsRegistry Static<TArchetype>(Action<ArchetypeProjectionBuilder> configure) where TArchetype : Archetype<TArchetype>
        => DeclareArchetype<TArchetype>(configure, isStatic: true);

    /// <summary>
    /// Declares a named set of observers a session can be bound to.
    /// </summary>
    /// <param name="name">The profile's name.</param>
    /// <param name="configure">Declares its observers.</param>
    /// <returns>This registry.</returns>
    public SubscriptionsRegistry Profile(string name, Action<ProfileBuilder> configure)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(configure);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A profile needs a name.", nameof(name));
        }

        foreach (var existing in _profiles)
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Profile '{name}' is already declared.");
            }
        }

        var profile = new ProfileDeclaration(name);
        configure(new ProfileBuilder(profile));
        _profiles.Add(profile);
        return this;
    }

    /// <summary>
    /// Declares a shared non-spatial source: a View whose members are replicated to the sessions subscribed to it — a bazaar, a leaderboard, a roster.
    /// </summary>
    /// <param name="name">The source's name, as a session subscribes to it.</param>
    /// <param name="view">The View whose membership drives enters and leaves.</param>
    /// <param name="projection">Declares what of each member travels.</param>
    /// <returns>This registry.</returns>
    /// <remarks>
    /// <b>Declared now, built in Phase 4</b> — a runtime that starts with one refuses to start and says so. Data with no position cannot be reached through
    /// an observer, so an application that needs it should be able to say so against the shape it will eventually get, rather than inventing a spatial home
    /// for a leaderboard in the meantime.
    /// </remarks>
    public SubscriptionsRegistry Source(string name, ViewBase view, Action<ArchetypeProjectionBuilder> projection)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(projection);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A source needs a name.", nameof(name));
        }

        foreach (var existing in _sources)
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Source '{name}' is already declared.");
            }
        }

        var declaration = new SourceDeclaration(name, view, _sources.Count);
        projection(new ArchetypeProjectionBuilder(declaration.Projection));
        _sources.Add(declaration);
        return this;
    }

    /// <summary>
    /// Declares that an event queue's events reach clients, and how.
    /// </summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="queue">The queue systems produce into. Replication becomes its single consumer.</param>
    /// <param name="configure">Declares routing and fields.</param>
    /// <returns>This registry.</returns>
    public SubscriptionsRegistry Event<T>(EventQueue<T> queue, Action<EventBuilder<T>> configure) where T : unmanaged
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(configure);

        SubscriptionsNames.RefuseReservedName(typeof(T).Name, "An event");
        foreach (var existing in _events)
        {
            if (existing.EventType == typeof(T))
            {
                throw new InvalidOperationException($"Event '{typeof(T).Name}' is already declared.");
            }
        }

        var declaration = new EventDeclaration(queue.Name, typeof(T), _events.Count);
        configure(new EventBuilder<T>(declaration));

        // Here, and not at Start: a field defaults to its raw type, and the types that have no default — a 64-bit integer, a double — can only be answered by
        // Field or Ignore, which run inside `configure`. This is the first moment the answer is knowable, and it is still the line the author wrote.
        declaration.CompleteDeclaration();
        _events.Add(declaration);
        return this;
    }

    /// <summary>
    /// Declares a command type clients may send.
    /// </summary>
    /// <typeparam name="T">The command type: an unmanaged struct.</typeparam>
    /// <param name="configure">Declares delivery, rate, roles and pre-check.</param>
    /// <returns>This registry.</returns>
    public SubscriptionsRegistry Command<T>(Action<CommandBuilder<T>> configure) where T : unmanaged
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(configure);

        SubscriptionsNames.RefuseBuiltInCommandName(typeof(T).Name);
        foreach (var existing in _commands)
        {
            if (existing.CommandType == typeof(T))
            {
                throw new InvalidOperationException($"Command '{typeof(T).Name}' is already declared.");
            }
        }

        var declaration = new CommandDeclaration(typeof(T), _commands.Count);
        configure(new CommandBuilder<T>(declaration));

        // Here, and not at Start: a field defaults to its raw type, and the types that have no default — a 64-bit integer, a double — can only be answered by
        // Field or Ignore, which run inside `configure`. This is the first moment the answer is knowable, and it is still the line the author wrote.
        declaration.CompleteDeclaration();
        foreach (var existing in _commands)
        {
            if (string.Equals(existing.Name, declaration.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Command name '{declaration.Name}' is already taken by '{existing.CommandType.Name}'.");
            }
        }

        _commands.Add(declaration);
        return this;
    }

    /// <summary>
    /// Declares an application metric, carried to sessions that asked for statistics.
    /// </summary>
    /// <param name="name">The metric's name. The <c>typhon.</c> prefix is reserved for the engine's own.</param>
    /// <param name="unit">Its unit, as a client displays it — <c>ms</c>, <c>count</c>, <c>B/s</c>.</param>
    /// <param name="codec">How the value travels. Encoders saturate: no NaN and no infinity reach the wire.</param>
    /// <param name="source">Reads the current value, once per emission — not once per session.</param>
    /// <param name="kind">Gauge or counter.</param>
    /// <param name="labels">The label set, for a metric that contributes one value per label rather than one value.</param>
    /// <returns>This registry.</returns>
    public SubscriptionsRegistry Metric(string name, string unit, Codec codec, Func<double> source, MetricKind kind = MetricKind.Gauge,
        string[] labels = null)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A metric needs a name.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(unit))
        {
            throw new ArgumentException($"Metric '{name}' needs a unit; a number with no unit cannot be displayed or compared.", nameof(unit));
        }

        if (!codec.IsDeclared)
        {
            throw new ArgumentException($"Metric '{name}' needs a codec.", nameof(codec));
        }

        if (name.StartsWith(ProtocolConstants.BuiltInMetricPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Metric '{name}' uses the reserved '{ProtocolConstants.BuiltInMetricPrefix}' prefix, which names the engine's own metrics at reserved " +
                $"indices 0-{ProtocolConstants.FirstAppMetricIdx - 1}. Application metrics take any other name.");
        }

        foreach (var existing in _metrics)
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Metric '{name}' is already declared.");
            }
        }

        _metrics.Add(new MetricDeclaration(name, unit, codec, kind, labels, source, _metrics.Count));
        return this;
    }

    /// <summary>
    /// Compiles nothing yet: it closes the registry to further declaration and refuses what a later phase builds. Called by <see cref="TyphonRuntime.Start"/>.
    /// </summary>
    internal void Freeze()
    {
        if (IsFrozen)
        {
            return;
        }

        RefuseUnbuiltShapes();
        Sessions.Freeze();
        IsFrozen = true;
    }

    private SubscriptionsRegistry DeclareArchetype<TArchetype>(Action<ArchetypeProjectionBuilder> configure, bool isStatic)
        where TArchetype : Archetype<TArchetype>
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var existing in _archetypes)
        {
            if (existing.ArchetypeType == typeof(TArchetype))
            {
                throw new InvalidOperationException($"Archetype '{typeof(TArchetype).Name}' already has a projection.");
            }
        }

        if (_archetypes.Count == ProtocolConstants.MaxArchetypes)
        {
            throw new InvalidOperationException(
                $"'{typeof(TArchetype).Name}' would be the {_archetypes.Count + 1}th replicated archetype, and " +
                $"{ProtocolConstants.MaxArchetypes} is the limit: a client's entity handle holds the archetype in 8 bits. " +
                "It bounds replicated archetypes, not the archetypes a database may hold.");
        }

        var projection = new ArchetypeProjection(typeof(TArchetype), _archetypes.Count, isStatic);
        configure(new ArchetypeProjectionBuilder(projection));
        _archetypes.Add(projection);
        return this;
    }

    private void RefuseUnbuiltShapes()
    {
        foreach (var archetype in _archetypes)
        {
            RefuseHeadings(archetype);
        }

        foreach (var profile in _profiles)
        {
            foreach (var observer in profile.Observers)
            {
                if (observer.Kind is not (ObserverKind.World or ObserverKind.Sphere))
                {
                    throw new NotSupportedException(
                        $"Profile '{profile.Name}' declares a {observer.Kind} observer, which a later phase builds. World and Sphere ship; the other " +
                        "shapes are declarable now so the API does not grow verbs later.");
                }

                if (observer.Kind == ObserverKind.Sphere && (observer.BoundEntity != EntityId.Null || observer.FollowsControlled))
                {
                    // The sphere is centred on the session's viewpoint, which the application places on the tick. Following an entity means the ENGINE
                    // resolving that entity's position on the replication track, which is a different piece of work; refusing it is better than silently
                    // centring the sphere somewhere the declaration did not ask for.
                    throw new NotSupportedException(
                        $"Profile '{profile.Name}' declares a Sphere that follows an entity. Centre it with the session's viewpoint instead — an "
                        + "application system calls Place(session, position) each tick — until the engine-side follow is built.");
                }

                if (observer.NearBudget != 0 || observer.FarTileM != 0)
                {
                    throw new NotSupportedException(
                        $"Profile '{profile.Name}' declares near/far tiers, which Phase 2 builds together with the observers that need them.");
                }

                if (observer.Kind == ObserverKind.Sphere && observer.LeaveRadius != 0)
                {
                    // An entity is held while it is within the radius of the session's anchor: one radius, no band. Ignoring a declared band would make the
                    // declaration say something the engine does not do.
                    throw new NotSupportedException(
                        $"Profile '{profile.Name}' declares a Sphere with a leave radius. Hysteresis is Phase 2 work; declare the enter radius alone "
                        + "until then.");
                }
            }

            if (profile.Observers.Count > 1)
            {
                throw new NotSupportedException(
                    $"Profile '{profile.Name}' declares {profile.Observers.Count} observers. A profile is served through exactly one World or Sphere observer "
                    + "until Phase 2 builds the tiers that give several a meaning.");
            }
        }

        // One radius for every Sphere: the push index's cells are sized from it, so a second radius would be served at the first's without a word.
        var radius = 0d;
        foreach (var profile in _profiles)
        {
            foreach (var observer in profile.Observers)
            {
                if (observer.Kind != ObserverKind.Sphere)
                {
                    continue;
                }

                if (radius != 0d && observer.Radius != radius)
                {
                    throw new NotSupportedException(
                        $"Profile '{profile.Name}' declares a Sphere of {observer.Radius} m where another profile declares {radius} m. Every Sphere shares one "
                        + "radius until the push index is sized per profile.");
                }

                radius = observer.Radius;
            }
        }

        if (_sources.Count > 0)
        {
            throw new NotSupportedException(
                $"Source '{_sources[0].Name}' is declared, and shared sources are Phase 4 work. Until then, data a client needs and a position cannot reach " +
                "travels as owner fields on the entity that owns it.");
        }
    }

    private static void RefuseHeadings(ArchetypeProjection archetype)
    {
        foreach (var field in archetype.Fields)
        {
            if (field.IsHeading)
            {
                throw new NotSupportedException(
                    $"Archetype '{archetype.Name}' declares the heading '{field.Name}', which Phase 2 builds. A moving entity needs none: its heading " +
                    "follows from the velocity the client already has.");
            }
        }
    }

    private void ThrowIfFrozen()
    {
        if (IsFrozen)
        {
            throw new InvalidOperationException(
                "The subscriptions registry was compiled when the runtime started and cannot be changed. Every projection, profile, command, event and " +
                "metric has to be declared before TyphonRuntime.Start(), because the catalog clients negotiate against is built from them exactly once.");
        }
    }
}

/// <summary>
/// Session kinds, admission and the tick-visible session lifecycle.
/// </summary>
/// <remarks>
/// <b>A kind is a name the engine never interprets.</b> It arrives in <c>HELLO</c>, is checked against this list before anything else runs, and is handed to
/// the application's admission hook. Declaring the set is what lets an unknown kind be refused before a single application delegate is called.
/// </remarks>
[PublicAPI]
public sealed partial class SubscriptionsSessions
{
    private readonly List<string> _kinds = [];

    internal SubscriptionsSessions()
    {
    }

    /// <summary>Whether the runtime has started and this surface is closed to further declaration.</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>The declared session kinds, in declaration order. They are exported in the catalog.</summary>
    public IReadOnlyList<string> DeclaredKinds => _kinds;

    /// <summary>
    /// Declares the session kinds clients may present.
    /// </summary>
    /// <param name="kinds">The kind names.</param>
    /// <returns>This surface.</returns>
    public SubscriptionsSessions Kinds(params string[] kinds)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(kinds);
        foreach (var kind in kinds)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new ArgumentException("A session kind needs a name.", nameof(kinds));
            }

            if (_kinds.Contains(kind))
            {
                throw new InvalidOperationException($"Session kind '{kind}' is already declared.");
            }

            _kinds.Add(kind);
        }

        return this;
    }

    internal void Freeze() => IsFrozen = true;

    internal void ThrowIfFrozen()
    {
        if (IsFrozen)
        {
            throw new InvalidOperationException("Session declarations are compiled when the runtime starts; declare them before TyphonRuntime.Start().");
        }
    }
}

/// <summary>
/// An application metric and where its value comes from.
/// </summary>
[PublicAPI]
public sealed class MetricDeclaration
{
    internal MetricDeclaration(string name, string unit, Codec codec, MetricKind kind, string[] labels, Func<double> source, int index)
    {
        Name = name;
        Unit = unit;
        Codec = codec;
        Kind = kind;
        Labels = labels == null ? [] : (string[])labels.Clone();
        Source = source;
        Index = index;
    }

    /// <summary>The metric's name.</summary>
    public string Name { get; }

    /// <summary>Its unit.</summary>
    public string Unit { get; }

    /// <summary>How the value travels.</summary>
    public Codec Codec { get; }

    /// <summary>Gauge or counter.</summary>
    public MetricKind Kind { get; }

    /// <summary>The labels, empty when the metric contributes one value.</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>The order this metric was declared in. Not the wire index, which starts above the engine's reserved range.</summary>
    public int Index { get; }

    /// <summary>Reads the current value.</summary>
    internal Func<double> Source { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Unit}, {Kind})";
}

/// <summary>
/// A shared non-spatial source and the projection its members travel under.
/// </summary>
[PublicAPI]
public sealed class SourceDeclaration
{
    internal SourceDeclaration(string name, ViewBase view, int index)
    {
        Name = name;
        View = view;
        Index = index;
        Projection = new ArchetypeProjection(name, null, index, isStatic: false);
    }

    /// <summary>The source's name.</summary>
    public string Name { get; }

    /// <summary>The View whose membership drives enters and leaves.</summary>
    public ViewBase View { get; }

    /// <summary>The order this source was declared in.</summary>
    public int Index { get; }

    /// <summary>What of each member travels.</summary>
    public ArchetypeProjection Projection { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name}: {Projection.Fields.Count} field(s)";
}

/// <summary>
/// Turns a selector into a field name and a declaration into a <see cref="ProjectedField"/>, with the refusals that depend on one declaration alone.
/// </summary>
internal static class SubscriptionsNames
{
    internal static string SelectorField<TSource, TField>(Expression<Func<TSource, TField>> selector, string verb)
    {
        ArgumentNullException.ThrowIfNull(selector);
        try
        {
            return ExpressionParser.ExtractFieldName(selector);
        }
        catch (NotSupportedException inner)
        {
            throw new ArgumentException(
                $"{verb} takes one field of '{typeof(TSource).Name}', written as a single member access such as c => c.Mode. " +
                "A computed expression cannot be resolved to a storage offset, which is what makes the projection free per entity.",
                nameof(selector), inner);
        }
    }

    internal static int ComponentTypeId<TComponent>(Comp<TComponent> component) where TComponent : unmanaged => component._componentTypeId;

    internal static ProjectedField BuildField<TComponent, TField>(Comp<TComponent> component, Expression<Func<TComponent, TField>> selector, Codec codec,
        string name, string group, bool onEnter, bool owner)
        where TComponent : unmanaged
    {
        var sourceField = SelectorField(selector, onEnter ? "OnEnter" : "Field");
        var wireName = string.IsNullOrWhiteSpace(name) ? sourceField : name;
        RefuseReservedName(wireName, "A field");
        RefuseUnnarrowed64Bit<TField>(wireName, codec);

        if (!codec.IsDeclared)
        {
            throw new ArgumentException($"Field '{wireName}' needs a codec.", nameof(codec));
        }

        return new ProjectedField
        {
            Name = wireName,
            ComponentName = typeof(TComponent).Name,
            ComponentTypeId = component._componentTypeId,
            SourceFieldName = sourceField,
            Group = onEnter ? null : ResolveGroup(group, owner),
            OnEnter = onEnter,
            Owner = owner,
            Codec = codec,
        };
    }

    internal static ProjectedField BuildMessageField<TMessage, TField>(Expression<Func<TMessage, TField>> selector, Codec codec, string name, string owner)
    {
        var sourceField = SelectorField(selector, "Field");
        var wireName = string.IsNullOrWhiteSpace(name) ? sourceField : name;
        RefuseReservedName(wireName, "A field");
        RefuseUnnarrowed64Bit<TField>(wireName, codec);

        if (!codec.IsDeclared)
        {
            throw new ArgumentException($"Field '{wireName}' of '{owner}' needs a codec.", nameof(codec));
        }

        return new ProjectedField
        {
            Name = wireName,
            ComponentName = typeof(TMessage).Name,
            SourceFieldName = sourceField,
            Codec = codec,
        };
    }

    internal static void RefuseBuiltInCommandName(string name)
    {
        if (BuiltInCommands.ReservedIdx(name) >= 0)
        {
            throw new InvalidOperationException(
                $"'{name}' is a built-in command the engine interprets itself, at reserved index {BuiltInCommands.ReservedIdx(name)}. " +
                "An application command taking that name would be decoded as the built-in by every client; choose another name.");
        }
    }

    internal static void RefuseReservedName(string name, string what)
    {
        if (name.StartsWith(ProtocolConstants.BuiltInMetricPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{what} named '{name}' uses the reserved '{ProtocolConstants.BuiltInMetricPrefix}' prefix, which belongs to the engine's own declarations.");
        }

        if (BuiltInCommands.ReservedIdx(name) >= 0)
        {
            throw new InvalidOperationException($"{what} named '{name}' collides with a built-in the engine declares itself.");
        }
    }

    private static string ResolveGroup(string group, bool owner)
    {
        if (!string.IsNullOrWhiteSpace(group))
        {
            return group;
        }

        return owner ? ArchetypeProjection.DefaultOwnerGroup : ArchetypeProjection.DefaultGroup;
    }

    private static void RefuseUnnarrowed64Bit<TField>(string name, Codec codec)
    {
        if (typeof(TField) != typeof(long) && typeof(TField) != typeof(ulong))
        {
            return;
        }

        if (!codec.Saturating)
        {
            throw new InvalidOperationException(
                $"Field '{name}' reads a 64-bit integer, and no 64-bit integer reaches the wire. Narrow it explicitly — " +
                "Codec.VarUInt.Saturate() clamps to 32 bits and counts the clamps — so the loss is a decision rather than a truncation nobody sees until " +
                "the value is large.");
        }
    }
}
