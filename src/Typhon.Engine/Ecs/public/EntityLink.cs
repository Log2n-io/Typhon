using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Typed entity reference — an 8-byte wrapper around <see cref="EntityId"/> that provides compile-time archetype safety.
/// <c>EntityLink&lt;Building&gt;</c> accepts Building, House, or any descendant archetype.
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator EntityId(EntityLink<T> link) => link._id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator EntityLink<T>(EntityId id) => new(id);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(EntityLink<T> other) => _id.Equals(other._id);

    public override bool Equals(object obj) => obj is EntityLink<T> other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _id.GetHashCode();

    public static bool operator ==(EntityLink<T> left, EntityLink<T> right) => left._id == right._id;
    public static bool operator !=(EntityLink<T> left, EntityLink<T> right) => left._id != right._id;

    public override string ToString() => IsNull ? $"EntityLink<{typeof(T).Name}>(Null)" : $"EntityLink<{typeof(T).Name}>({_id})";
}
