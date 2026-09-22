using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// Declares the wire fields of one archetype: what a client may see of it, under what name, quantized how, and in which change group.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anything not named here never leaves the server.</b> That is the whole point of declaring a projection rather than replicating components: the fields a
/// simulation writes every tick for its own bookkeeping — cooldowns, think timers, tick counters — are simply never mentioned, and cost nothing.
/// </para>
/// <para>
/// <b>Everything is resolved once.</b> A selector (<c>c =&gt; c.Mode</c>) names a field; at <c>Start</c> that name becomes a component offset plus a field
/// offset taken from the schema's measured layout, and the selector is never invoked again. Nothing here runs per entity.
/// </para>
/// <para>
/// <b>The refusals are immediate.</b> A duplicate wire name, a ninth change group or a 64-bit source with no explicit narrowing throws from the call that
/// declared it, not from <c>Start</c> — the stack trace then points at the line that is wrong instead of at the line that noticed.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class ArchetypeProjectionBuilder
{
    private readonly ArchetypeProjection _projection;

    internal ArchetypeProjectionBuilder(ArchetypeProjection projection) => _projection = projection;

    /// <summary>
    /// Replicates this archetype's position as motion segments: the engine measures the displacement and sends a segment only when a client's own
    /// extrapolation would be wrong by more than the tolerance.
    /// </summary>
    /// <typeparam name="TComponent">The component carrying the position.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="configure">Tolerance, teleport threshold and heartbeat. <see langword="null"/> takes the engine defaults.</param>
    /// <returns>This builder.</returns>
    public ArchetypeProjectionBuilder Motion<TComponent>(Comp<TComponent> component, Action<MotionBuilder> configure = null)
        where TComponent : unmanaged
    {
        var builder = new MotionBuilder();
        configure?.Invoke(builder);
        _projection.SetPosition(new PositionProjection
        {
            ComponentName = typeof(TComponent).Name,
            ComponentTypeId = SubscriptionsNames.ComponentTypeId(component),
            IsMotion = true,
            Motion = builder.Motion,
        });
        return this;
    }

    /// <summary>
    /// Replicates this archetype's position once, on enter, and never again. For scenery: a rock that has never moved does not need a motion model to say so.
    /// </summary>
    /// <typeparam name="TComponent">The component carrying the position.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <returns>This builder.</returns>
    public ArchetypeProjectionBuilder Position<TComponent>(Comp<TComponent> component)
        where TComponent : unmanaged
    {
        _projection.SetPosition(new PositionProjection
        {
            ComponentName = typeof(TComponent).Name,
            ComponentTypeId = SubscriptionsNames.ComponentTypeId(component),
            IsMotion = false,
        });
        return this;
    }

    /// <summary>
    /// A field sent whenever it changes, in the change group it belongs to.
    /// </summary>
    /// <typeparam name="TComponent">The component holding the field.</typeparam>
    /// <typeparam name="TField">The field's type.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="selector">Selects the field — one member access, resolved once.</param>
    /// <param name="codec">How the value travels.</param>
    /// <param name="name">The wire name. <see langword="null"/> takes the field's own name.</param>
    /// <param name="group">The change group. <see langword="null"/> takes <see cref="ArchetypeProjection.DefaultGroup"/>.</param>
    /// <returns>This builder.</returns>
    public ArchetypeProjectionBuilder Field<TComponent, TField>(Comp<TComponent> component, Expression<Func<TComponent, TField>> selector, Codec codec,
        string name = null, string group = null)
        where TComponent : unmanaged
    {
        _projection.AddField(SubscriptionsNames.BuildField<TComponent, TField>(component, selector, codec, name, group, onEnter: false, owner: false));
        return this;
    }

    /// <summary>
    /// A field sent in the enter record and never updated. For an identity a client needs once — a template id, a species, a model — and which the engine
    /// would otherwise spend a change-group tick slot watching for a change that never comes.
    /// </summary>
    /// <typeparam name="TComponent">The component holding the field.</typeparam>
    /// <typeparam name="TField">The field's type.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="selector">Selects the field.</param>
    /// <param name="codec">How the value travels.</param>
    /// <param name="name">The wire name. <see langword="null"/> takes the field's own name.</param>
    /// <returns>This builder.</returns>
    public ArchetypeProjectionBuilder OnEnter<TComponent, TField>(Comp<TComponent> component, Expression<Func<TComponent, TField>> selector, Codec codec,
        string name = null)
        where TComponent : unmanaged
    {
        _projection.AddField(SubscriptionsNames.BuildField<TComponent, TField>(component, selector, codec, name, null, onEnter: true, owner: false));
        return this;
    }

    /// <summary>
    /// The ratio of two fields of one component, as a <c>unorm</c> — a health bar, a charge level, a durability.
    /// </summary>
    /// <typeparam name="TComponent">The component holding both fields.</typeparam>
    /// <typeparam name="TValue">The two fields' type.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="value">Selects the numerator.</param>
    /// <param name="max">Selects the denominator.</param>
    /// <param name="bits">Width of the ratio: 8, 16, 24 or 32.</param>
    /// <param name="name">The wire name. Required — neither field's name describes the ratio.</param>
    /// <param name="group">The change group. <see langword="null"/> takes <see cref="ArchetypeProjection.DefaultGroup"/>.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Sending the ratio rather than the two numbers is what makes it cheap and what makes it quiet: a creature whose maximum health grows on a level-up
    /// changes nothing on the wire, and a wound is one byte instead of two integers a client would have to divide anyway.
    /// </remarks>
    public ArchetypeProjectionBuilder Fraction<TComponent, TValue>(Comp<TComponent> component, Expression<Func<TComponent, TValue>> value,
        Expression<Func<TComponent, TValue>> max, int bits, string name, string group = null)
        where TComponent : unmanaged
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Fraction needs a wire name: neither the numerator's nor the denominator's name describes the ratio.", nameof(name));
        }

        var field = SubscriptionsNames.BuildField<TComponent, TValue>(component, value, Codec.Unorm(bits), name, group, onEnter: false, owner: false);
        field.MaxSourceFieldName = SubscriptionsNames.SelectorField(max, "Fraction");
        _projection.AddField(field);
        return this;
    }

    /// <summary>
    /// An orientation that does not follow from movement: an entity turning in place. Sent as an <c>angle</c> when it moves by more than the tolerance,
    /// compared in code space so the wrap at ±π needs no special case.
    /// </summary>
    /// <typeparam name="TComponent">The component holding the heading.</typeparam>
    /// <typeparam name="TField">The heading field's type.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="selector">Selects the heading field.</param>
    /// <param name="bits">Width of the angle: 8, 16, 24 or 32.</param>
    /// <param name="toleranceDeg">How far the heading may move before a new value is sent, in degrees.</param>
    /// <param name="name">The wire name. <see langword="null"/> takes the field's own name.</param>
    /// <param name="group">The change group. <see langword="null"/> takes <see cref="ArchetypeProjection.DefaultGroup"/>.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <b>Declared now, built in Phase 2</b> — a runtime that starts with one of these refuses to start and says so. The verb exists here so an application
    /// that needs it writes it once: the shape it will have when it works is the shape it has today. A moving entity needs none of this; its heading follows
    /// from its velocity, which the client already has.
    /// </remarks>
    public ArchetypeProjectionBuilder Heading<TComponent, TField>(Comp<TComponent> component, Expression<Func<TComponent, TField>> selector, int bits,
        double toleranceDeg, string name = null, string group = null)
        where TComponent : unmanaged
    {
        var field = SubscriptionsNames.BuildField<TComponent, TField>(component, selector, Codec.Angle(bits), name, group, onEnter: false, owner: false);
        field.IsHeading = true;
        field.HeadingToleranceDeg = toleranceDeg;
        _projection.AddField(field);
        return this;
    }

    /// <summary>
    /// Fields visible only to the session controlling this entity — an inventory, a credit balance, a private waypoint. They travel in the archetype's own
    /// owner section, with their own group bit space, carried by <c>SELF</c> and unreachable from the public record by construction.
    /// </summary>
    /// <param name="configure">Declares the owner fields.</param>
    /// <returns>This builder.</returns>
    public ArchetypeProjectionBuilder Owner(Action<OwnerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new OwnerBuilder(_projection));
        return this;
    }
}

/// <summary>
/// One field of an archetype's wire projection, as it was declared.
/// </summary>
[PublicAPI]
public sealed class ProjectedField
{
    /// <summary>The name this field travels under, which is the field's own name unless the declaration overrode it.</summary>
    public string Name { get; internal set; }

    /// <summary>The component the value is read from.</summary>
    public string ComponentName { get; internal set; }

    /// <summary>The component field the value is read from.</summary>
    public string SourceFieldName { get; internal set; }

    /// <summary>The denominator field of a <c>Fraction</c>, or <see langword="null"/> for every other transform.</summary>
    public string MaxSourceFieldName { get; internal set; }

    /// <summary>The change group, or <see langword="null"/> for an enter-only field, which belongs to no group.</summary>
    public string Group { get; internal set; }

    /// <summary>Whether the field travels in enter records only.</summary>
    public bool OnEnter { get; internal set; }

    /// <summary>Whether the field is owner-visible — carried by <c>SELF</c>, never by a public record.</summary>
    public bool Owner { get; internal set; }

    /// <summary>Whether the field was declared as a heading, which Phase 1 refuses at <c>Start</c>.</summary>
    public bool IsHeading { get; internal set; }

    /// <summary>A heading's send threshold in degrees; 0 for every other transform.</summary>
    public double HeadingToleranceDeg { get; internal set; }

    /// <summary>How the value travels.</summary>
    public Codec Codec { get; internal set; }

    /// <summary>The source component's type id, for the projection compiler's slot lookup.</summary>
    internal int ComponentTypeId { get; set; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} = {ComponentName}.{SourceFieldName} as {Codec}";
}

/// <summary>
/// How an archetype's position reaches a client: as motion segments it extrapolates, or as one value in the enter record.
/// </summary>
[PublicAPI]
public sealed class PositionProjection
{
    /// <summary>The component carrying the position.</summary>
    public string ComponentName { get; internal set; }

    /// <summary>Whether the position travels as motion segments.</summary>
    public bool IsMotion { get; internal set; }

    /// <summary>The motion policy, or <see langword="null"/> for a static position.</summary>
    public MotionProjection Motion { get; internal set; }

    /// <summary>The position component's type id, for the projection compiler's slot lookup.</summary>
    internal int ComponentTypeId { get; set; }
}

/// <summary>
/// Everything one archetype declared: its position, its public fields, its owner fields and the change groups they fall into.
/// </summary>
[PublicAPI]
public sealed class ArchetypeProjection
{
    private readonly List<ProjectedField> _fields = [];
    private readonly List<ProjectedField> _ownerFields = [];
    private readonly List<string> _groups = [];
    private readonly List<string> _ownerGroups = [];

    internal ArchetypeProjection(Type archetypeType, int index, bool isStatic) : this(archetypeType.Name, archetypeType, index, isStatic)
    {
    }

    internal ArchetypeProjection(string name, Type archetypeType, int index, bool isStatic)
    {
        ArchetypeType = archetypeType;
        Name = name;
        Index = index;
        IsStatic = isStatic;
    }

    /// <summary>The change group a field falls into when its declaration names none.</summary>
    public const string DefaultGroup = "state";

    /// <summary>The change group an owner field falls into when its declaration names none.</summary>
    public const string DefaultOwnerGroup = "owner";

    /// <summary>The archetype's name, which is the name it travels under.</summary>
    public string Name { get; }

    /// <summary>The archetype type this projection was declared for.</summary>
    public Type ArchetypeType { get; }

    /// <summary>The order this archetype was declared in. Not the wire index, which is canonical and assigned when the catalog is built.</summary>
    public int Index { get; }

    /// <summary>Whether the archetype was declared static: sent once on enter, never updated.</summary>
    public bool IsStatic { get; }

    /// <summary>How the position travels, or <see langword="null"/> when the archetype declared none.</summary>
    public PositionProjection Position { get; private set; }

    /// <summary>The public fields, in declaration order. Canonical wire order is computed when the plan is compiled.</summary>
    public IReadOnlyList<ProjectedField> Fields => _fields;

    /// <summary>The owner fields, in declaration order.</summary>
    public IReadOnlyList<ProjectedField> OwnerFields => _ownerFields;

    /// <summary>The public change groups, in first-declared order.</summary>
    public IReadOnlyList<string> Groups => _groups;

    /// <summary>The owner change groups, in first-declared order.</summary>
    public IReadOnlyList<string> OwnerGroups => _ownerGroups;

    /// <inheritdoc/>
    public override string ToString() => $"{Name}: {_fields.Count} field(s), {_ownerFields.Count} owner field(s)";

    internal void SetPosition(PositionProjection position)
    {
        if (Position != null)
        {
            throw new InvalidOperationException($"Archetype '{Name}' already declared a position; an archetype has one.");
        }

        Position = position;
    }

    internal void AddField(ProjectedField field)
    {
        var fields = field.Owner ? _ownerFields : _fields;
        var section = field.Owner ? "owner section" : "public section";
        foreach (var existing in fields)
        {
            if (string.Equals(existing.Name, field.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Archetype '{Name}' already replicates a field named '{field.Name}' in its {section}. " +
                    "Two fields cannot share a wire name; pass an explicit name to one of them.");
            }
        }

        if (field.Group != null)
        {
            var groups = field.Owner ? _ownerGroups : _groups;
            if (!groups.Contains(field.Group))
            {
                if (groups.Count == ProtocolConstants.MaxGroups)
                {
                    throw new InvalidOperationException(
                        $"Archetype '{Name}' would need a {groups.Count + 1}th change group ('{field.Group}') in its {section}, and the group mask is a " +
                        $"byte: {ProtocolConstants.MaxGroups} groups per section is the wire's limit, not a policy. Merge fields that change together.");
                }

                groups.Add(field.Group);
            }
        }

        fields.Add(field);
    }
}
