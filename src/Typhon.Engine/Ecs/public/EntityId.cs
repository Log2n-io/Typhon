using System;
using System.Globalization;
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
    /// <remarks>
    /// Internal and <see cref="long"/>-typed because every caller is a storage path reading a primary key, which is stored signed.
    /// The public equivalent is <see cref="FromRawValue"/>, deliberately named apart rather than overloaded: an integer literal is
    /// implicitly convertible to both <see cref="long"/> and <see cref="ulong"/>, so an overload pair would make <c>FromRaw(0)</c> ambiguous
    /// at 116 existing call sites.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EntityId FromRaw(long rawValue)
    {
        var raw = (ulong)rawValue;
        return Unsafe.As<ulong, EntityId>(ref raw);
    }

    /// <summary>Raw packed value: the 48-bit <see cref="EntityKey"/> in the upper bits and the 16-bit <see cref="ArchetypeId"/> in the lower bits.</summary>
    /// <remarks>
    /// <para>
    /// <b>For carrying an identity somewhere the engine does not own</b> — an application's own wire messages, its account or character rows in another
    /// store, its logs, its admin tooling, or a client's "target" field coming back in. Round-trips through <see cref="FromRawValue"/>.
    /// </para>
    /// <para>
    /// <b>Stable within one database, NOT across databases.</b> <see cref="ArchetypeId"/> is the per-DB routing id persisted in
    /// <c>ArchetypeR1.RoutingId</c> and re-matched by archetype NAME on reopen, so a raw value stays valid across restarts of the same database — and
    /// means something different, or nothing, in another one. Renaming an archetype without setting
    /// <c>ArchetypeAttribute.PreviousName</c> breaks the match and invalidates every raw value already persisted outside the engine: the name is the
    /// durable identity, not the number.
    /// </para>
    /// </remarks>
    public ulong RawValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value;
    }

    /// <summary>Rebuilds an <see cref="EntityId"/> from the two parts <see cref="EntityKey"/> and <see cref="ArchetypeId"/> expose.</summary>
    /// <param name="entityKey">The 48-bit monotonic key (see <see cref="EntityKey"/>). Must be non-negative and fit in 48 bits.</param>
    /// <param name="archetypeId">The per-DB archetype routing id (see <see cref="ArchetypeId"/>).</param>
    /// <returns>The identifier.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entityKey"/> is negative or does not fit in 48 bits.</exception>
    /// <remarks>
    /// <b>The range check is unconditional, unlike the internal constructor's.</b> That one guards behind <see cref="CheckConfig.Enabled"/>, which is off
    /// by default so the JIT folds it away — correct for a path the engine itself feeds with values it just produced. This one takes numbers from outside
    /// the engine, where the whole point is to reject a bad one, and a check that disappears in Release would hand back a silently corrupt identity.
    /// </remarks>
    public static EntityId FromParts(long entityKey, ushort archetypeId)
    {
        if (entityKey < 0 || (ulong)entityKey >= 1UL << (64 - RoutingBits))
        {
            throw new ArgumentOutOfRangeException(nameof(entityKey), entityKey,
                $"An EntityKey is non-negative and fits in {64 - RoutingBits} bits (0 .. {(1UL << (64 - RoutingBits)) - 1}).");
        }

        var raw = ((ulong)entityKey << RoutingBits) | archetypeId;
        return Unsafe.As<ulong, EntityId>(ref raw);
    }

    /// <summary>Rebuilds an <see cref="EntityId"/> from a <see cref="RawValue"/>.</summary>
    /// <param name="rawValue">A value previously read from <see cref="RawValue"/>.</param>
    /// <returns>The identifier.</returns>
    /// <remarks>
    /// Every bit pattern is a representable identifier, so this cannot fail and does not validate: a value that was not produced by
    /// <see cref="RawValue"/> yields an id that resolves to no entity rather than an exception. Read <see cref="RawValue"/>'s remarks for what "the same
    /// database" means before persisting one.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityId FromRawValue(ulong rawValue) => Unsafe.As<ulong, EntityId>(ref rawValue);

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

    /// <summary>Parses the form <see cref="ToString"/> produces.</summary>
    /// <param name="text">Either <c>Entity(Key=&lt;key&gt;, Arch=&lt;routingId&gt;)</c> or <c>Entity(Null)</c>.</param>
    /// <param name="id">The parsed identifier, or <see cref="Null"/> when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> was one of those two forms and both numbers were in range.</returns>
    /// <remarks>
    /// Exists so a log line or a diagnostic dump is reversible: the id that appears in a trace can be fed back to a tool without the reader
    /// hand-extracting two integers. Culture-invariant, because <see cref="ToString"/> is. Surrounding whitespace is tolerated; anything else in the
    /// string is not, so a half-matching line fails rather than silently parsing its prefix.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<char> text, out EntityId id)
    {
        id = Null;
        var s = text.Trim();
        if (s.Equals("Entity(Null)", StringComparison.Ordinal))
        {
            return true;
        }

        const string keyPrefix = "Entity(Key=";
        if (!s.StartsWith(keyPrefix, StringComparison.Ordinal) || s[^1] != ')')
        {
            return false;
        }

        var body = s[keyPrefix.Length..^1];
        var comma = body.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        const string archPrefix = " Arch=";
        var archPart = body[(comma + 1)..];
        if (!archPart.StartsWith(archPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!long.TryParse(body[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out var key)
            || !ushort.TryParse(archPart[archPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var arch)
            || (ulong)key >= 1UL << (64 - RoutingBits))
        {
            return false;
        }

        id = FromParts(key, arch);
        return true;
    }

    /// <summary>Parses the form <see cref="ToString"/> produces.</summary>
    /// <param name="text">The text, or <see langword="null"/>.</param>
    /// <param name="id">The parsed identifier, or <see cref="Null"/> when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> for <see langword="null"/> or an unrecognised form.</returns>
    public static bool TryParse(string text, out EntityId id)
    {
        if (text != null)
        {
            return TryParse(text.AsSpan(), out id);
        }

        id = Null;
        return false;
    }
}
