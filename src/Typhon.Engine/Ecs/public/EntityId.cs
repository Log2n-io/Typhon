using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// 64-bit entity identifier: 52-bit monotonic EntityKey (upper) + 12-bit ArchetypeId (lower).
/// Routes to the correct per-archetype LinearHash and uniquely identifies an entity within the engine.
/// </summary>
/// <remarks>
/// <para>EntityKey is monotonic per-archetype, never recycled — no ABA problem, no version field needed.</para>
/// <para>ArchetypeId is a 12-bit value (max 4095) read from the <c>[Archetype(Id = N)]</c> attribute.</para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 8)]
[PublicAPI]
public readonly struct EntityId : IEquatable<EntityId>
{
    [FieldOffset(0)]
    private readonly ulong _value;

    /// <summary>52-bit monotonic key, unique within the archetype's LinearHash.</summary>
    public long EntityKey
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (long)(_value >> 12);
    }

    /// <summary>12-bit archetype identifier. Routes to the correct per-archetype LinearHash instance.</summary>
    public ushort ArchetypeId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (ushort)(_value & 0xFFF);
    }

    /// <summary>True if this is the null/default entity (no entity).</summary>
    public bool IsNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value == 0;
    }

    /// <summary>The null entity sentinel.</summary>
    public static readonly EntityId Null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EntityId(long entityKey, ushort archetypeId)
    {
        Debug.Assert(entityKey >= 0, "EntityKey must be non-negative");
        Debug.Assert(archetypeId <= 0xFFF, "ArchetypeId must fit in 12 bits (max 4095)");
        _value = ((ulong)entityKey << 12) | ((ulong)archetypeId & 0xFFF);
    }

    /// <summary>Reconstruct an EntityId from a raw packed value (e.g., from <see cref="CompRevStorageHeader.EntityPK"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EntityId FromRaw(long rawValue)
    {
        var raw = (ulong)rawValue;
        return Unsafe.As<ulong, EntityId>(ref raw);
    }

    /// <summary>Raw packed value — for serialization and diagnostics only.</summary>
    internal ulong RawValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(EntityId other) => _value == other._value;

    public override bool Equals(object obj) => obj is EntityId other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(EntityId left, EntityId right) => left._value == right._value;
    public static bool operator !=(EntityId left, EntityId right) => left._value != right._value;

    public override string ToString() => IsNull ? "Entity(Null)" : $"Entity(Key={EntityKey}, Arch={ArchetypeId})";
}
