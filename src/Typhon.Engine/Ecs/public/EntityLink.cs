using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Typed entity reference — an 8-byte wrapper around <see cref="EntityId"/> that provides compile-time archetype safety.
/// An <see cref="EntityLink{T}"/> accepts Building, House, or any descendant archetype.
/// </summary>
/// <typeparam name="T">The target archetype type (or ancestor for polymorphic references).</typeparam>
[PublicAPI]
public readonly struct EntityLink<T> : IEquatable<EntityLink<T>> where T : class
{
    private readonly EntityId _id;

    /// <summary>The underlying EntityId.</summary>
    public EntityId Id
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _id;
    }

    /// <summary>True if this link points to no entity.</summary>
    public bool IsNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _id.IsNull;
    }

    /// <summary>The null link sentinel.</summary>
    public static readonly EntityLink<T> Null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EntityLink(EntityId id) => _id = id;

    /// <summary>Unwraps the link to its underlying <see cref="EntityId"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator EntityId(EntityLink<T> link) => link._id;

    /// <summary>Wraps a raw <see cref="EntityId"/> as a typed link; no archetype compatibility check is performed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator EntityLink<T>(EntityId id) => new(id);

    /// <summary>Returns <see langword="true"/> when <paramref name="other"/> wraps the same <see cref="EntityId"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(EntityLink<T> other) => _id.Equals(other._id);

    /// <summary>Returns <see langword="true"/> when <paramref name="obj"/> is an <see cref="EntityLink{T}"/> equal to this one.</summary>
    public override bool Equals(object obj) => obj is EntityLink<T> other && Equals(other);

    /// <summary>Hash of the underlying <see cref="EntityId"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _id.GetHashCode();

    /// <summary>Value equality of two typed links (compares the underlying <see cref="EntityId"/>).</summary>
    public static bool operator ==(EntityLink<T> left, EntityLink<T> right) => left._id == right._id;

    /// <summary>Value inequality of two typed links (compares the underlying <see cref="EntityId"/>).</summary>
    public static bool operator !=(EntityLink<T> left, EntityLink<T> right) => left._id != right._id;

    /// <summary>Human-readable form, e.g. <c>EntityLink&lt;Building&gt;(...)</c>, or the null form for an empty link.</summary>
    public override string ToString() => IsNull ? $"EntityLink<{typeof(T).Name}>(Null)" : $"EntityLink<{typeof(T).Name}>({_id})";
}
