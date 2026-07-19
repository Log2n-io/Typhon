using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// 64-bit entity identifier: 48-bit monotonic EntityKey (upper) + 16-bit per-DB archetype routing id (lower).
/// Routes to the correct per-archetype LinearHash and uniquely identifies an entity within the engine.
/// </summary>
/// <remarks>
/// <para>EntityKey is monotonic per-archetype, never recycled — no ABA problem, no version field needed. 2^48 ≈ 281 T allocations per archetype.</para>
/// <para><see cref="ArchetypeId"/> is the low 16 bits: the <b>per-DB, engine-assigned archetype routing id</b> (persisted in <c>ArchetypeR1.RoutingId</c>,
/// re-matched by name on reopen). Up to 65,536 archetypes composable into one database. The word-aligned 48/16 split needs no mask constant — the routing id
/// is <c>(ushort)value</c> and the key is <c>value &gt;&gt; 16</c>.</para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 8)]
[PublicAPI]
public readonly struct EntityId : IEquatable<EntityId>
{
    /// <summary>Number of low bits reserved for the per-DB archetype routing id.</summary>
    internal const int RoutingBits = 16;

    [FieldOffset(0)]
    private readonly ulong _value;

    /// <summary>48-bit monotonic key, unique within the archetype's LinearHash.</summary>
    public long EntityKey
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (long)(_value >> RoutingBits);
    }

    /// <summary>16-bit per-DB archetype routing id (low bits). Routes to the correct per-archetype storage instance for the owning engine.</summary>
    public ushort ArchetypeId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (ushort)_value;
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
        if (CheckConfig.Enabled && (ulong)entityKey >= (1UL << (64 - RoutingBits)))
        {
            ThrowHelper.ThrowInvalidOp($"EntityKey must be non-negative and fit in {64 - RoutingBits} bits");
        }
        _value = ((ulong)entityKey << RoutingBits) | archetypeId;
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

    /// <summary>Returns <see langword="true"/> when <paramref name="other"/> has the same packed identifier value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(EntityId other) => _value == other._value;

    /// <summary>Returns <see langword="true"/> when <paramref name="obj"/> is an <see cref="EntityId"/> equal to this one.</summary>
    public override bool Equals(object obj) => obj is EntityId other && Equals(other);

    /// <summary>Hash of the packed 64-bit identifier value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>Value equality of two <see cref="EntityId"/> values.</summary>
    public static bool operator ==(EntityId left, EntityId right) => left._value == right._value;

    /// <summary>Value inequality of two <see cref="EntityId"/> values.</summary>
    public static bool operator !=(EntityId left, EntityId right) => left._value != right._value;

    /// <summary>Human-readable form, e.g. <c>Entity(Key=42, Arch=3)</c>, or <c>Entity(Null)</c> for the null entity.</summary>
    public override string ToString() => IsNull ? "Entity(Null)" : $"Entity(Key={EntityKey}, Arch={ArchetypeId})";
}
