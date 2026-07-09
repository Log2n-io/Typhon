using JetBrains.Annotations;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Schema.Definition;

/// <summary>
/// Store data of a type determined at construction and formatted as a string
/// </summary>
/// <remarks>
/// <para>
/// This type allows to store in a field of a component a data that can be of a user set type at construction.
/// </para>
/// <para>
/// The variant has a fixed size 64 bytes as its only field is a <see cref="String64"/> storing the data type and value in the form <c>"tt:data"</c>.
/// </para>
/// <para>
/// There are methods to get or explicitly cast the variant to the literal type of the data it stores.
/// </para>
/// <para>
/// This struct is read-only.
/// </para>
/// </remarks>
[PublicAPI]
public readonly struct Variant : IComparable<Variant>, IEquatable<Variant>
{
    /// <summary>Creates a variant holding the boolean <paramref name="value"/>.</summary>
    /// <param name="value">The value to store.</param>
    public Variant(bool value)      => _text.SetVariant(value);
    /// <summary>Creates a variant holding the signed-byte <paramref name="value"/>.</summary>
    /// <param name="value">The value to store.</param>
    public Variant(sbyte value)     => _text.SetVariant(value);
    /// <summary>Creates a variant holding the 16-bit integer <paramref name="value"/>.</summary>
    /// <param name="value">The value to store.</param>
    public Variant(short value)     => _text.SetVariant(value);
    /// <summary>Creates a variant holding the 32-bit integer <paramref name="value"/>.</summary>
    /// <param name="value">The value to store.</param>
    public Variant(int value)       => _text.SetVariant(value);

    /// <summary>Creates a variant holding the 64-bit integer <paramref name="value"/>.</summary>
    /// <param name="value">The value to store.</param>
    public Variant(long value)      => _text.SetVariant(value);

    /// <summary>Creates a variant holding the string <paramref name="value"/>.</summary>
    /// <param name="value">The string to store; its UTF-8 payload must fit in 61 bytes (64 minus the 3-byte type tag).</param>
    /// <param name="truncate"><c>true</c> to silently truncate an over-long string to fit; <c>false</c> to throw instead.</param>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> exceeds the 61-byte payload and <paramref name="truncate"/> is <c>false</c>.</exception>
    public Variant(string value, bool truncate)
    {
        _text.SetVariant(value, truncate);
    }

    /// <summary>Extracts the stored string. See <see cref="AsString"/>.</summary>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.String"/>.</exception>
    public static explicit operator string(Variant v) => v.AsString();
    /// <summary>Extracts the stored boolean. See <see cref="AsBool"/>.</summary>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.Boolean"/>.</exception>
    public static explicit operator bool(Variant v) => v.AsBool();
    /// <summary>Extracts the stored signed byte. See <see cref="AsByte"/>.</summary>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.Byte"/>.</exception>
    public static explicit operator sbyte(Variant v) => v.AsByte();
    /// <summary>Extracts the stored 16-bit integer. See <see cref="AsShort"/>.</summary>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.Short"/>.</exception>
    public static explicit operator short(Variant v) => v.AsShort();
    /// <summary>Extracts the stored 32-bit integer. See <see cref="AsInt"/>.</summary>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.Int"/>.</exception>
    public static explicit operator int(Variant v) => v.AsInt();
    /// <summary>Extracts the stored 64-bit integer. See <see cref="AsLong"/>.</summary>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.Long"/>.</exception>
    public static explicit operator long(Variant v) => v.AsLong();

    /// <summary>Returns the stored value as a string.</summary>
    /// <returns>The decoded string payload.</returns>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.String"/>.</exception>
    unsafe public string AsString()
    {
        CheckAssertType(FieldType.String);
        fixed (byte* a = _text.AsReadOnlySpan()[3..])
        {
            return Marshal.PtrToStringUTF8(new IntPtr(a));
        }
    }

    /// <summary>Returns the stored value as a boolean.</summary>
    /// <returns>The decoded boolean.</returns>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.Boolean"/>.</exception>
    unsafe public bool AsBool()
    {
        CheckAssertType(FieldType.Boolean);
        var a = _text.GetStringContentAddrReaOnly();
        return a[3] == (byte)'1';
    }

    /// <summary>Returns the stored value as a signed byte.</summary>
    /// <returns>The decoded <c>sbyte</c>.</returns>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.Byte"/>.</exception>
    public sbyte AsByte()
    {
        CheckAssertType(FieldType.Byte);
        var spanUtf8 = _text.AsReadOnlySpan()[3..];
        return sbyte.Parse(spanUtf8);
    }

    /// <summary>Returns the stored value as a 16-bit integer.</summary>
    /// <returns>The decoded <c>short</c>.</returns>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.Short"/>.</exception>
    public short AsShort()
    {
        CheckAssertType(FieldType.Short);
        var spanUtf8 = _text.AsReadOnlySpan()[3..];
        return short.Parse(spanUtf8);
    }

    /// <summary>Returns the stored value as a 32-bit integer.</summary>
    /// <returns>The decoded <c>int</c>.</returns>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.Int"/>.</exception>
    public int AsInt()
    {
        CheckAssertType(FieldType.Int);
        var spanUtf8 = _text.AsReadOnlySpan()[3..];
        return int.Parse(spanUtf8);
    }

    /// <summary>Returns the stored value as a 64-bit integer.</summary>
    /// <returns>The decoded <c>long</c>.</returns>
    /// <exception cref="InvalidOperationException">The stored type is not <see cref="FieldType.Long"/>.</exception>
    public long AsLong()
    {
        CheckAssertType(FieldType.Long);
        var spanUtf8 = _text.AsReadOnlySpan()[3..];
        return long.Parse(spanUtf8);
    }

    private void CheckAssertType(FieldType fieldType)
    {
        if (FieldType != fieldType)
        {
            throw new InvalidOperationException($"Can't cast {this} to {fieldType} because it's of {FieldType} type");
        }
    }

    /// <summary>Formats the stored value as <c>"value (type)"</c> (e.g. <c>"42 (int)"</c>); returns an empty string when the stored type is unrecognized.</summary>
    public override string ToString()
    {
        switch (FieldType)
        {
            case FieldType.Boolean:
                var b = AsBool();
                var val = b ? "true" : "false";
                return $"{val} (bool)";
            case FieldType.String:
                return $"{AsString()} (string)";
            case FieldType.Byte:
                return $"{AsByte().ToString()} (byte)";
            case FieldType.Short:
                return $"{AsShort().ToString()} (short)";
            case FieldType.Int:
                return $"{AsInt().ToString()} (int)";
            case FieldType.Long:
                return $"{AsLong().ToString()} (long)";
        }

        return "";
    }

    private readonly String64 _text;

    private Variant(String64 text)
    {
        _text = text;
    }

    /// <summary>
    /// The type of the stored value, decoded from the leading two-character type tag; <see cref="FieldType.None"/> when the buffer is not a valid
    /// <c>"tt:data"</c> encoding.
    /// </summary>
    public FieldType FieldType
    {
        get
        {
            var header = _text.AsReadOnlySpan();
            if (header.Length < 3 || header[2] != ':')
            {
                return FieldType.None;
            }

            return (ushort)((header[0] << 8) | header[1]) switch
            {
                (byte)'b' << 8 | (byte)'o' => FieldType.Boolean,
                (byte)'s' << 8 | (byte)'b' => FieldType.Byte,
                (byte)'s' << 8 | (byte)'s' => FieldType.Short,
                (byte)'s' << 8 | (byte)'i' => FieldType.Int,
                (byte)'s' << 8 | (byte)'l' => FieldType.Long,
                (byte)'u' << 8 | (byte)'b' => FieldType.UByte,
                (byte)'u' << 8 | (byte)'s' => FieldType.UShort,
                (byte)'u' << 8 | (byte)'i' => FieldType.UInt,
                (byte)'u' << 8 | (byte)'l' => FieldType.ULong,
                (byte)'f' << 8 | (byte)'l' => FieldType.Float,
                (byte)'d' << 8 | (byte)'f' => FieldType.Double,
                (byte)'c' << 8 | (byte)'h' => FieldType.Char,
                (byte)'s' << 8 | (byte)'t' => FieldType.String,
                _ => FieldType.None
            };
        }
    }

    /// <summary>Ordinal byte-wise comparison of the two variants' encoded buffers (type tag included).</summary>
    /// <param name="other">The value to compare against.</param>
    /// <returns>Negative, zero, or positive per lexicographic byte ordering.</returns>
    public int CompareTo(Variant other) => _text.AsReadOnlySpan().SequenceCompareTo(other._text.AsReadOnlySpan());

    /// <summary>Byte-wise equality of the two variants' encoded buffers — equal only when both the type tag and payload match.</summary>
    /// <param name="other">The value to compare against.</param>
    /// <returns><c>true</c> when the encodings are byte-for-byte equal.</returns>
    public bool Equals(Variant other) => other._text.AsReadOnlySpan().SequenceEqual(_text.AsReadOnlySpan());

    /// <summary>Byte-wise equality; <c>false</c> when <paramref name="obj"/> is not a <see cref="Variant"/>.</summary>
    public override bool Equals(object obj) => obj is Variant other && Equals(other);

    /// <summary>32-bit <see cref="MurmurHash2"/> over the encoded buffer bytes.</summary>
    public override int GetHashCode() => (int)MurmurHash2.Hash(_text.AsReadOnlySpan());

    /// <summary>Byte-wise equality.</summary>
    public static bool operator ==(Variant left, Variant right) => left.Equals(right);

    /// <summary>Byte-wise inequality.</summary>
    public static bool operator !=(Variant left, Variant right) => !left.Equals(right);
}
