using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// One connected client's identity: a 16-bit table slot and a 16-bit generation packed into a 32-bit value.
/// </summary>
/// <remarks>
/// <para>
/// <b>It fits in a component.</b> That is the whole reason for the packing: an application that possesses an avatar stores the session on the entity, and a
/// 32-bit field costs nothing beside the entity's own data. It is also why the session table's hard maximum is 65 535 rather than a number somebody chose —
/// a slot wider than a <see cref="ushort"/> does not exist.
/// </para>
/// <para>
/// <b>The generation is what makes a recycled slot safe.</b> Slots are reused, so a bare slot number would let a stale reference — a component written three
/// ticks ago, a command that arrived late — address whichever client now occupies it. The generation rises every time a slot is handed out again, so a stale
/// id fails its lookup instead of quietly naming a stranger. A generation of zero is never issued, which makes <see langword="default"/> mean "no session"
/// and costs one value out of 65 536.
/// </para>
/// <para>
/// The generation wraps after 65 535 reuses of one slot and skips zero as it goes. Wrapping is not a correctness hole here: it would take a stale reference
/// surviving 65 536 connections on the same slot to collide, which is a lifetime no engine structure has.
/// </para>
/// </remarks>
[PublicAPI]
public readonly struct SessionId : IEquatable<SessionId>
{
    private readonly uint _value;

    /// <summary>Creates an identity from a slot and a generation.</summary>
    /// <param name="slot">The session table row.</param>
    /// <param name="generation">How many times that row has been handed out. Zero means "no session".</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SessionId(ushort slot, ushort generation) => _value = slot | ((uint)generation << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SessionId(uint value) => _value = value;

    /// <summary>The absent session. Its generation is zero, which is never issued.</summary>
    public static SessionId None => default;

    /// <summary>The packed value, as it travels in <c>WELCOME</c> and as a component field stores it.</summary>
    public uint Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value;
    }

    /// <summary>The session table row this identity names.</summary>
    public ushort Slot
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (ushort)_value;
    }

    /// <summary>How many times that row had been handed out when this identity was issued.</summary>
    public ushort Generation
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (ushort)(_value >> 16);
    }

    /// <summary>Whether this names a session at all. A zero generation is never issued, so it is the one value that cannot.</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_value >> 16) != 0;
    }

    /// <summary>Rebuilds an identity from its packed value — from the wire, or from a component field.</summary>
    /// <param name="value">A value previously read from <see cref="Value"/>.</param>
    /// <returns>The identity. It is not validated against the table; a lookup is what decides whether it still names a live session.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SessionId FromValue(uint value) => new(value);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(SessionId other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is SessionId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (int)_value;

    /// <summary>Compares two identities.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns><see langword="true"/> when they name the same session.</returns>
    public static bool operator ==(SessionId left, SessionId right) => left._value == right._value;

    /// <summary>Compares two identities.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns><see langword="true"/> when they do not name the same session.</returns>
    public static bool operator !=(SessionId left, SessionId right) => left._value != right._value;

    /// <inheritdoc />
    /// <remarks>
    /// Both halves are printed, because a log that shows only the slot cannot distinguish the client that just left from the one that took its row — which is
    /// exactly the confusion the generation exists to prevent.
    /// </remarks>
    public override string ToString() => IsValid ? $"session {Slot}.{Generation}" : "session none";
}
