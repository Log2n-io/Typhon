using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// How an event finds the sessions that should hear it.
/// </summary>
[PublicAPI]
public enum EventRouting
{
    /// <summary>Nothing declared yet; the event reaches nobody.</summary>
    None = 0,

    /// <summary>Sessions whose interest contains a point the event carries.</summary>
    Near = 1,

    /// <summary>Sessions that already know any of the entities the event names.</summary>
    ToKnown = 2,

    /// <summary>The session controlling the entity the event names.</summary>
    ToOwner = 3,

    /// <summary>Every session.</summary>
    Broadcast = 4,

    /// <summary>The one session the emitter names (<see cref="SubscriptionsCommands.EmitTo{T}"/>).</summary>
    ToSession = 5,

    /// <summary>Every session in one realm — or, with a subtree, in it and every realm below it in the parent tree (12-realms § 3).</summary>
    ToRealm = 6,
}

/// <summary>
/// Declares how a system's event queue reaches clients: which sessions hear an event, and what of it travels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Events are best effort, and that is a design statement rather than a limitation.</b> They accumulate per skipped session up to a cap and then the
/// oldest are dropped with a loss count. Anything that must never be lost is state: a death is a mode change, not a message, and a client that missed the
/// message still learns the creature is dead from its next record.
/// </para>
/// <para>
/// <b>Routing costs are per event, not per session.</b> <see cref="RouteNear(Func{T, Vector3D}, double)"/> buckets events by grid cell and each session collects the cells it overlaps,
/// so a hundred sessions and a thousand events cost O(events + Σ cells) rather than their product.
/// </para>
/// <para>
/// <b>Every public instance field of <typeparamref name="T"/> travels, whether or not the declaration mentions it</b> (01-model § 7).
/// <see cref="Field{TField}"/> and <see cref="Entity"/> OVERRIDE how one of them travels; <see cref="Ignore{TField}"/> keeps one off the wire. A field
/// nobody mentions is not a field nobody wanted — it is usually one somebody forgot.
/// </para>
/// </remarks>
/// <typeparam name="T">The event type.</typeparam>
[PublicAPI]
public sealed partial class EventBuilder<T> where T : unmanaged
{
    private readonly EventDeclaration _event;

    internal EventBuilder(EventDeclaration declaration) => _event = declaration;

    /// <summary>
    /// Routes the event to sessions whose interest contains the point it carries.
    /// </summary>
    /// <param name="point">Reads the event's world position. Called once per event, never per session.</param>
    /// <param name="radiusM">When above zero, only sessions whose viewpoint lies within this radius of the point hear it.</param>
    /// <returns>This builder.</returns>
    public EventBuilder<T> RouteNear(Func<T, Vector3D> point, double radiusM = 0)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (radiusM < 0 || !double.IsFinite(radiusM))
        {
            throw new ArgumentOutOfRangeException(nameof(radiusM), radiusM, "A routing radius is zero or a positive, finite distance.");
        }

        _event.SetRouting(EventRouting.Near);
        _event.RoutingRadiusM = radiusM;
        _event.RoutingPoint = point;

        // Built here, where T is known: the encoder holds the payload's bytes, not a T, and reads the point through this without boxing.
        _event.RoutingPointReader = payload => point(System.Runtime.InteropServices.MemoryMarshal.Read<T>(payload));
        return this;
    }

    /// <summary>
    /// Routes the event to sessions of <paramref name="realm"/>'s realm whose interest contains the point it carries (12-realms § 3): the point is a place
    /// in that realm, and a session of another realm never hears it, whatever its local coordinates.
    /// </summary>
    /// <param name="point">Reads the event's position in its realm. Called once per event, never per session.</param>
    /// <param name="realm">Reads the realm the point is in — an emitter has it cheaply from the cluster or entity it acts on (<c>ClusterRef.Realm</c>).</param>
    /// <param name="radiusM">When above zero, only sessions whose viewpoint lies within this radius of the point hear it.</param>
    /// <returns>This builder.</returns>
    public EventBuilder<T> RouteNear(Func<T, Vector3D> point, Func<T, RealmId> realm, double radiusM = 0)
    {
        ArgumentNullException.ThrowIfNull(realm);
        RouteNear(point, radiusM);
        _event.RoutingRealmReader = payload => realm(System.Runtime.InteropServices.MemoryMarshal.Read<T>(payload)).Value;
        return this;
    }

    /// <summary>
    /// Routes the event to every session in <paramref name="realm"/>'s realm — with <paramref name="subtree"/>, also to every session in a realm below it
    /// in the parent tree (<see cref="RealmConfig.Parent"/>): a planet-wide message that reaches the players in its interiors (12-realms § 3). The parent
    /// tree routes; it grants no visibility.
    /// </summary>
    /// <param name="realm">Reads the realm the event is addressed to.</param>
    /// <param name="subtree">Whether realms below it hear it too.</param>
    /// <returns>This builder.</returns>
    public EventBuilder<T> RouteToRealm(Func<T, RealmId> realm, bool subtree = false)
    {
        ArgumentNullException.ThrowIfNull(realm);
        _event.SetRouting(EventRouting.ToRealm);
        _event.RoutingRealmReader = payload => realm(System.Runtime.InteropServices.MemoryMarshal.Read<T>(payload)).Value;
        _event.RoutingSubtree = subtree;
        return this;
    }

    /// <summary>
    /// Routes the event to every session that already knows any of the entities it names — the sessions for which the event means something, because they
    /// have the entity it is about.
    /// </summary>
    /// <param name="entities">Selects the entity fields to test.</param>
    /// <returns>This builder.</returns>
    public EventBuilder<T> RouteToKnown(params Expression<Func<T, EntityId>>[] entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Length == 0)
        {
            throw new ArgumentException("RouteToKnown needs at least one entity field.", nameof(entities));
        }

        _event.SetRouting(EventRouting.ToKnown);
        foreach (var selector in entities)
        {
            _event.AddRoutingEntity(SubscriptionsNames.SelectorField(selector, "RouteToKnown"));
        }

        return this;
    }

    /// <summary>Routes the event to the session controlling the entity it names.</summary>
    /// <param name="entity">Selects the entity field.</param>
    /// <returns>This builder.</returns>
    public EventBuilder<T> RouteToOwner(Expression<Func<T, EntityId>> entity)
    {
        _event.SetRouting(EventRouting.ToOwner);
        _event.AddRoutingEntity(SubscriptionsNames.SelectorField(entity, "RouteToOwner"));
        return this;
    }

    /// <summary>Routes the event to the one session the emitter names, with <see cref="SubscriptionsCommands.EmitTo{T}"/>: a reply, a private notice.</summary>
    /// <returns>This builder.</returns>
    public EventBuilder<T> RouteToSession()
    {
        _event.SetRouting(EventRouting.ToSession);
        return this;
    }

    /// <summary>Routes the event to every session. For a server-wide announcement, not for anything a session could be uninterested in.</summary>
    /// <returns>This builder.</returns>
    public EventBuilder<T> Broadcast()
    {
        _event.SetRouting(EventRouting.Broadcast);
        return this;
    }

    /// <summary>
    /// An entity reference the event carries. It travels as a netId, so a client receives the identity it already knows rather than a server-side id it
    /// could not use.
    /// </summary>
    /// <param name="selector">Selects the entity field.</param>
    /// <param name="name">The wire name. <see langword="null"/> takes the field's own name.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// An <see cref="EntityId"/> field travels as a netId by default, so this verb is what renames one or states the intent where it reads better; it is not
    /// what makes the reference travel.
    /// </remarks>
    public EventBuilder<T> Entity(Expression<Func<T, EntityId>> selector, string name = null)
    {
        var field = SubscriptionsNames.BuildMessageField<T, EntityId>(selector, Codec.EntityRef, name, typeof(T).Name);
        MessageContract.RequireDeclarableField(typeof(T), "Event", _event.Name, field.SourceFieldName, nameof(Entity));
        _event.AddField(field);
        return this;
    }

    /// <summary>A value the event carries.</summary>
    /// <typeparam name="TField">The field's type.</typeparam>
    /// <param name="selector">Selects the field.</param>
    /// <param name="codec">How the value travels.</param>
    /// <param name="name">The wire name. <see langword="null"/> takes the field's own name.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">
    /// The selector names something that is not a public instance field of <typeparamref name="T"/>, or that field is already declared or ignored.
    /// </exception>
    /// <remarks>It overrides the codec the field's CLR type defaults to; it is not what makes the field travel.</remarks>
    public EventBuilder<T> Field<TField>(Expression<Func<T, TField>> selector, Codec codec, string name = null)
    {
        var field = SubscriptionsNames.BuildMessageField<T, TField>(selector, codec, name, typeof(T).Name);
        MessageContract.RequireDeclarableField(typeof(T), "Event", _event.Name, field.SourceFieldName, nameof(Field));
        _event.AddField(field);
        return this;
    }

    /// <summary>
    /// Keeps one of the event's fields off the wire: a server-side correlation id, a field only the routing reads.
    /// </summary>
    /// <typeparam name="TField">The field's type.</typeparam>
    /// <param name="selector">Selects the field.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">
    /// The selector names something that is not a public instance field of <typeparamref name="T"/>, or that field already carries a declaration.
    /// </exception>
    /// <remarks>
    /// <b>It takes a verb because silence must not mean "not replicated".</b> Every other field travels by default, so an omission and a decision would
    /// otherwise look identical in the declaration and in review.
    /// </remarks>
    public EventBuilder<T> Ignore<TField>(Expression<Func<T, TField>> selector)
    {
        var source = SubscriptionsNames.SelectorField(selector, nameof(Ignore));
        MessageContract.RequireDeclarableField(typeof(T), "Event", _event.Name, source, nameof(Ignore));
        _event.IgnoreField(source);
        return this;
    }
}

/// <summary>
/// What an <see cref="EventBuilder{T}"/> declared.
/// </summary>
[PublicAPI]
public sealed class EventDeclaration
{
    private readonly List<ProjectedField> _overrides = [];
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private readonly List<string> _routingEntityFields = [];

    // Materialized the first time anything reads the field set, which is Start. It cannot be built when the declaration opens: Field(), Entity() and Ignore()
    // run after that and are what answer for a field the CLR type alone cannot.
    private ProjectedField[] _fields;
    private Dictionary<string, Type> _enumTypes;

    internal EventDeclaration(Type eventType, int index, int payloadSize, MessageFieldDeclaration[] attributed = null)
    {
        EventType = eventType;
        Name = eventType.Name;
        Index = index;
        PayloadSize = payloadSize;
        Attributed = attributed;
    }

    /// <summary>The fields the event's attributes declare (<see cref="IReplicatedMessage"/>), or <see langword="null"/>.</summary>
    internal MessageFieldDeclaration[] Attributed { get; }

    /// <summary>The event struct's size in memory, which an emission copies.</summary>
    internal int PayloadSize { get; }

    /// <summary>The event's CLR type.</summary>
    public Type EventType { get; }

    /// <summary>The name it travels under.</summary>
    public string Name { get; }

    /// <summary>The order this event was declared in. Not the wire index, which is assigned when the catalog is built.</summary>
    public int Index { get; }

    /// <summary>How the event finds its sessions.</summary>
    public EventRouting Routing { get; private set; }

    /// <summary>The routing radius in metres; 0 when the routing declared none.</summary>
    public double RoutingRadiusM { get; internal set; }

    /// <summary>The entity fields routing tests, in declaration order.</summary>
    public IReadOnlyList<string> RoutingEntityFields => _routingEntityFields;

    /// <summary>
    /// Every field that travels: the ones the declaration gave a codec, in declaration order, then the rest of the record's public instance fields under the
    /// codec their CLR type defaults to, ordinally by name.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A field's CLR type has no default codec and the declaration neither gave it one nor ignored it.
    /// </exception>
    /// <remarks>
    /// <b>Order here is declaration order, not wire order.</b> Wire order is W11's layout key, assigned once by <see cref="CatalogSerializer.Canonicalize"/>
    /// for every record kind, so nothing sorts twice.
    /// </remarks>
    public IReadOnlyList<ProjectedField> Fields => Complete();

    /// <summary>The record fields the declaration deliberately kept off the wire, in no particular order.</summary>
    public IReadOnlyCollection<string> IgnoredFields => _ignored;

    /// <summary>Reads the event's world position, for <see cref="EventRouting.Near"/>.</summary>
    internal Delegate RoutingPoint { get; set; }

    /// <summary>The same, over the event's bytes: what the encoder calls.</summary>
    internal EventPointReader RoutingPointReader { get; set; }

    /// <summary>The realm a <see cref="EventRouting.Near"/> point or a <see cref="EventRouting.ToRealm"/> event is in, over the event's bytes; null for realm 0.</summary>
    internal EventRealmReader RoutingRealmReader { get; set; }

    /// <summary>A <see cref="EventRouting.ToRealm"/> event reaches the realms below its realm too.</summary>
    internal bool RoutingSubtree { get; set; }

    /// <summary>The CLR enum a defaulted field's value set comes from, or <see langword="null"/>. An overridden field carries its own on the codec.</summary>
    internal IReadOnlyDictionary<string, Type> DefaultEnumTypes
    {
        get
        {
            Complete();
            return _enumTypes;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} via {Routing}, {_overrides.Count} declared field(s)";

    internal void SetRouting(EventRouting routing)
    {
        if (Routing != EventRouting.None && Routing != routing)
        {
            throw new InvalidOperationException(
                $"Event '{Name}' already routes by {Routing}; an event has one routing rule, so that the set of sessions it reaches is decided in one place.");
        }

        Routing = routing;
    }

    internal void AddRoutingEntity(string fieldName) => _routingEntityFields.Add(fieldName);

    internal void AddField(ProjectedField field)
    {
        MessageContract.RefuseFrozen(_fields, "Event", Name);
        foreach (var existing in _overrides)
        {
            if (string.Equals(existing.Name, field.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Event '{Name}' already declares a field named '{field.Name}'.");
            }
        }

        MessageContract.RefuseIgnoredOverride(_ignored, "Event", Name, field.SourceFieldName);
        _overrides.Add(field);
    }

    internal void IgnoreField(string sourceFieldName)
    {
        MessageContract.RefuseFrozen(_fields, "Event", Name);
        MessageContract.RefuseDeclaredIgnore(_overrides, "Event", Name, sourceFieldName);
        _ignored.Add(sourceFieldName);
    }

    /// <summary>Materializes the field set at the end of the declaring call, so a field with no default codec is refused where it was written.</summary>
    internal void CompleteDeclaration() => Complete();

    private ProjectedField[] Complete()
        => _fields ??= MessageContract.Complete(EventType, "Event", Name, _overrides, _ignored, Attributed, out _enumTypes);
}
