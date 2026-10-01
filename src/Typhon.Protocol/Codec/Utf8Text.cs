using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Typhon.Protocol;

/// <summary>
/// A fixed-capacity UTF-8 string that lives inside a message struct: what lets an event or a command carry a <c>str</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the text is inline rather than a reference.</b> An event and a command both decode into an <c>unmanaged</c>
/// struct — the event arena is a memcpy of a fixed size under a per-worker gate, and a command decodes straight into the
/// struct the application reads. A <see cref="string"/> field would make both managed, so text that travels has to be
/// bytes the struct owns. The capacity is part of the type, which is why there is one type per capacity: C# cannot size
/// an inline buffer by a generic parameter.
/// </para>
/// <para>
/// <b>The cost, stated.</b> A message occupies its declared capacity whether the text is three bytes or all of it — a
/// <see cref="Utf8Text256"/> is 258 bytes in the arena either way. The WIRE carries only the bytes that are used
/// (<c>varu length | UTF-8</c>), so this is a memory trade inside the server and never a bandwidth one.
/// </para>
/// <para>Adding a capacity is a handful of lines: an <see cref="InlineArrayAttribute"/> buffer and a struct beside the
/// ones here. Do it when something needs it rather than in advance.</para>
/// </remarks>
public interface IInlineUtf8Text
{
    /// <summary>The largest number of UTF-8 bytes this type holds. Must equal the <c>str</c> codec's cap where it is declared.</summary>
    static abstract int Capacity { get; }

    /// <summary>The bytes in use.</summary>
    int Length { get; }
}

/// <summary>The 256-byte inline buffer of a <see cref="Utf8Text256"/>.</summary>
[InlineArray(Utf8Text256.Capacity)]
public struct Utf8Buffer256
{
    private byte _element0;
}

/// <summary>A UTF-8 string of at most 256 bytes, carried inside a message struct. See <see cref="IInlineUtf8Text"/>.</summary>
public struct Utf8Text256 : IInlineUtf8Text, IEquatable<Utf8Text256>
{
    /// <summary>The largest number of UTF-8 bytes this type holds.</summary>
    public const int Capacity = 256;

    /// <summary>
    /// The encoder <see cref="From"/> uses: it THROWS on a lone surrogate rather than emitting U+FFFD for it.
    /// </summary>
    /// <remarks>The same encoder <c>WireReader</c> decodes with, so the two ends agree on what this protocol calls text.</remarks>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private ushort _length;
    private Utf8Buffer256 _bytes;

    /// <inheritdoc/>
    static int IInlineUtf8Text.Capacity => Capacity;

    /// <summary>The number of UTF-8 bytes in use.</summary>
    public readonly int Length => _length;

    /// <summary>Whether it holds no bytes. An empty text is a value, not an absence: it travels as a zero-length string.</summary>
    public readonly bool IsEmpty => _length == 0;

    /// <summary>The bytes in use, which alias this value — never outlive it, and never keep it across a copy.</summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<byte> Utf8 => ((ReadOnlySpan<byte>)_bytes)[.._length];

    /// <summary>The text of <paramref name="value"/>, encoded as UTF-8.</summary>
    /// <param name="value">The text; <see langword="null"/> is empty.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentException">Its UTF-8 encoding is longer than <see cref="Capacity"/>.</exception>
    /// <remarks>
    /// <b>It refuses rather than truncating.</b> Cutting a string to fit can split a multi-byte character, and a silent
    /// truncation reaches whoever reads it as a shorter message rather than as an error — discovered, if ever, by a
    /// player. A caller that means to shorten says so itself.
    /// </remarks>
    public static Utf8Text256 From(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return default;
        }

        // STRICT, not the replacement fallback `Encoding.UTF8` carries. A lone surrogate — a string cut between the two
        // halves of an astral character, which is what slicing a JavaScript string by length produces — encodes to the
        // three bytes of U+FFFD under the default encoder and travels as a replacement character nobody asked for. That
        // is precisely the silent corruption the remarks above refuse to commit one level up, so it is refused here too.
        int needed;
        try
        {
            needed = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException e)
        {
            throw new ArgumentException(
                "'" + nameof(value) + "' is not valid text: it holds an unpaired surrogate, which has no UTF-8 encoding. " +
                "A string cut between the halves of an astral character is the usual cause.",
                nameof(value),
                e);
        }

        if (needed > Capacity)
        {
            throw new ArgumentException($"'{nameof(value)}' is {needed} UTF-8 bytes, past this type's capacity of {Capacity}.", nameof(value));
        }

        var text = default(Utf8Text256);
        StrictUtf8.GetBytes(value, ((Span<byte>)text._bytes)[..needed]);
        text._length = (ushort)needed;
        return text;
    }

    /// <summary>The UTF-8 bytes of <paramref name="utf8"/>, copied in.</summary>
    /// <param name="utf8">Valid UTF-8; validity is the caller's to guarantee, and the wire reader checks it before this is reached.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentException">It is longer than <see cref="Capacity"/>.</exception>
    public static Utf8Text256 FromUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > Capacity)
        {
            throw new ArgumentException($"'{nameof(utf8)}' is {utf8.Length} bytes, past this type's capacity of {Capacity}.", nameof(utf8));
        }

        var text = default(Utf8Text256);
        utf8.CopyTo((Span<byte>)text._bytes);
        text._length = (ushort)utf8.Length;
        return text;
    }

    /// <summary>The text, decoded. Allocates; <see cref="Utf8"/> is the allocation-free read.</summary>
    /// <returns>The text.</returns>
    public override readonly string ToString() => Encoding.UTF8.GetString(Utf8);

    /// <inheritdoc/>
    public readonly bool Equals(Utf8Text256 other) => Utf8.SequenceEqual(other.Utf8);

    /// <inheritdoc/>
    public override readonly bool Equals(object obj) => obj is Utf8Text256 other && Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Utf8);
        return hash.ToHashCode();
    }

    /// <summary>Whether two values hold the same bytes.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(Utf8Text256 left, Utf8Text256 right) => left.Equals(right);

    /// <summary>Whether two values hold different bytes.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(Utf8Text256 left, Utf8Text256 right) => !left.Equals(right);
}
