using JetBrains.Annotations;
using System;
using System.Linq.Expressions;

namespace Typhon.Engine;

/// <summary>
/// Declares the fields of an archetype that only the session controlling the entity may see.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate section, not a flag on a field.</b> Owner fields have their own change groups and their own bit space, and they travel in <c>SELF</c>; the
/// public record has no way to address them. That is a structural guarantee rather than a filter someone has to remember to apply — one player's credit
/// balance cannot reach another player's client by a mistake in the encoder, because the encoder that writes public records cannot name the field.
/// </para>
/// <para>
/// <b>The names are public even though the values are not.</b> Every client receives the whole catalog, so an owner field's name and codec are visible to
/// everyone. Owner visibility scopes values, never structure.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class OwnerBuilder
{
    private readonly ArchetypeProjection _projection;

    internal OwnerBuilder(ArchetypeProjection projection) => _projection = projection;

    /// <summary>
    /// An owner-visible field, sent to the controlling session whenever it changes.
    /// </summary>
    /// <typeparam name="TComponent">The component holding the field.</typeparam>
    /// <typeparam name="TField">The field's type.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="selector">Selects the field — one member access, resolved once.</param>
    /// <param name="codec">How the value travels.</param>
    /// <param name="name">The wire name. <see langword="null"/> takes the field's own name.</param>
    /// <param name="group">The owner change group. <see langword="null"/> takes <see cref="ArchetypeProjection.DefaultOwnerGroup"/>.</param>
    /// <returns>This builder.</returns>
    public OwnerBuilder Field<TComponent, TField>(Comp<TComponent> component, Expression<Func<TComponent, TField>> selector, Codec codec,
        string name = null, string group = null)
        where TComponent : unmanaged
    {
        _projection.AddField(SubscriptionsNames.BuildField<TComponent, TField>(component, selector, codec, name, group, onEnter: false, owner: true));
        return this;
    }

    /// <summary>
    /// The ratio of two fields of one component, owner-visible.
    /// </summary>
    /// <typeparam name="TComponent">The component holding both fields.</typeparam>
    /// <typeparam name="TValue">The two fields' type.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="value">Selects the numerator.</param>
    /// <param name="max">Selects the denominator.</param>
    /// <param name="bits">Width of the ratio: 8, 16, 24 or 32.</param>
    /// <param name="name">The wire name. Required.</param>
    /// <param name="group">The owner change group. <see langword="null"/> takes <see cref="ArchetypeProjection.DefaultOwnerGroup"/>.</param>
    /// <returns>This builder.</returns>
    public OwnerBuilder Fraction<TComponent, TValue>(Comp<TComponent> component, Expression<Func<TComponent, TValue>> value,
        Expression<Func<TComponent, TValue>> max, int bits, string name, string group = null)
        where TComponent : unmanaged
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Fraction needs a wire name: neither the numerator's nor the denominator's name describes the ratio.", nameof(name));
        }

        var field = SubscriptionsNames.BuildField<TComponent, TValue>(component, value, Codec.Unorm(bits), name, group, onEnter: false, owner: true);
        field.MaxSourceFieldName = SubscriptionsNames.SelectorField(max, "Fraction");
        _projection.AddField(field);
        return this;
    }
}
