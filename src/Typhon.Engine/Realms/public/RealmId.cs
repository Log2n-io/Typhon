using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Identifies a realm: one of the strictly isolated worlds a <see cref="DatabaseEngine"/> hosts (a planet, a space sector, a building interior, a
/// dungeon instance). An entity is in exactly one realm at a time; every spatial structure, query and replication view is scoped to one realm.
/// </summary>
/// <remarks>
/// <para>A dense index, not a handle: realms are addressed by array index everywhere on the hot path, so the value is bounded by the engine's
/// configured realm count and <see cref="None"/> (0xFFFF) is reserved — at most 65 535 realms.</para>
/// <para><see cref="Default"/> (0) is the realm a single-world application lives in without ever naming one: <c>ConfigureSpatialGrid</c> configures
/// it, and a spatial archetype with no realm key lives in it.</para>
/// </remarks>
[PublicAPI]
public readonly struct RealmId : IEquatable<RealmId>
{
    /// <summary>The realm index.</summary>
    public readonly ushort Value;

    /// <summary>Realm 0 — the single world of an application that never names a realm.</summary>
    public static readonly RealmId Default;

    /// <summary>No realm (for example a session outside every realm). Never a valid index.</summary>
    public static readonly RealmId None = new(NoneValue);

    /// <summary>The raw value of <see cref="None"/>.</summary>
    public const ushort NoneValue = 0xFFFF;

    /// <summary>The largest realm count an engine can be configured with (every index but <see cref="None"/>).</summary>
    public const int MaxCount = NoneValue;

    /// <summary>Creates a realm id from its index.</summary>
    public RealmId(ushort value) => Value = value;

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Value == NoneValue;

    /// <summary>The index. Explicit: <see cref="None"/> converts to 65 535, which indexes nothing, so a conversion is a decision, never an accident.</summary>
    public static explicit operator ushort(RealmId realm) => realm.Value;

    /// <summary>A realm id from its index.</summary>
    public static explicit operator RealmId(ushort value) => new(value);

    /// <inheritdoc />
    public bool Equals(RealmId other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is RealmId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <summary>Equality.</summary>
    public static bool operator ==(RealmId left, RealmId right) => left.Value == right.Value;

    /// <summary>Inequality.</summary>
    public static bool operator !=(RealmId left, RealmId right) => left.Value != right.Value;

    /// <inheritdoc />
    public override string ToString() => IsNone ? "Realm(None)" : $"Realm({Value})";
}
