// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Schema.Definition;

/// <summary>Single-precision 2D point / vector.</summary>
[PublicAPI]
public struct Point2F
{
    /// <summary>X coordinate.</summary>
    public float X;
    /// <summary>Y coordinate.</summary>
    public float Y;
}

/// <summary>Single-precision 3D point / vector.</summary>
[PublicAPI]
public struct Point3F
{
    /// <summary>X coordinate.</summary>
    public float X;
    /// <summary>Y coordinate.</summary>
    public float Y;
    /// <summary>Z coordinate.</summary>
    public float Z;
}

/// <summary>Single-precision 4D point / vector (homogeneous coordinates).</summary>
[PublicAPI]
public struct Point4F
{
    /// <summary>X coordinate.</summary>
    public float X;
    /// <summary>Y coordinate.</summary>
    public float Y;
    /// <summary>Z coordinate.</summary>
    public float Z;
    /// <summary>W coordinate.</summary>
    public float W;
}

/// <summary>Double-precision 2D point / vector.</summary>
[PublicAPI]
public struct Point2D
{
    /// <summary>X coordinate.</summary>
    public double X;
    /// <summary>Y coordinate.</summary>
    public double Y;
}

/// <summary>Double-precision 3D point / vector.</summary>
[PublicAPI]
public struct Point3D
{
    /// <summary>X coordinate.</summary>
    public double X;
    /// <summary>Y coordinate.</summary>
    public double Y;
    /// <summary>Z coordinate.</summary>
    public double Z;
}

/// <summary>Double-precision 4D point / vector (homogeneous coordinates).</summary>
[PublicAPI]
public struct Point4D
{
    /// <summary>X coordinate.</summary>
    public double X;
    /// <summary>Y coordinate.</summary>
    public double Y;
    /// <summary>Z coordinate.</summary>
    public double Z;
    /// <summary>W coordinate.</summary>
    public double W;
}

/// <summary>Single-precision quaternion. <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/> are the vector part; <see cref="W"/> is the scalar part.</summary>
[PublicAPI]
public struct QuaternionF
{
    /// <summary>X component of the vector part.</summary>
    public float X;
    /// <summary>Y component of the vector part.</summary>
    public float Y;
    /// <summary>Z component of the vector part.</summary>
    public float Z;
    /// <summary>W scalar component.</summary>
    public float W;
}

/// <summary>Double-precision quaternion. <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/> are the vector part; <see cref="W"/> is the scalar part.</summary>
[PublicAPI]
public struct QuaternionD
{
    /// <summary>X component of the vector part.</summary>
    public double X;
    /// <summary>Y component of the vector part.</summary>
    public double Y;
    /// <summary>Z component of the vector part.</summary>
    public double Z;
    /// <summary>W scalar component.</summary>
    public double W;
}

/// <summary>Marker type for a variable-length string field (<see cref="FieldType.String"/>); its payload lives in the component's variable-size buffer, not inline.</summary>
public struct VarString
{

}

/// <summary>A fixed 1024-byte inline UTF-8 string buffer — a blittable, fixed-size component field that stores its characters in place (no heap allocation).</summary>
[PublicAPI]
public unsafe struct String1024
{
    private const int Size = 1024;
    private fixed byte _data[1024];

    /// <summary>Gets or sets the string value, encoded as inline UTF-8 and null-terminated. The setter truncates input that does not fit the buffer.</summary>
    public string AsString
    {
        get
        {
            fixed (byte* a = _data)
            {
                return Marshal.PtrToStringUTF8(new IntPtr(a));
            }
        }

        set
        {
            fixed (char* c = value)
            fixed (byte* a = _data)
            {
                var inLength = value.Length;
                var sizeRequired = Encoding.UTF8.GetByteCount(c, inLength);
                if (sizeRequired < Size)
                {
                    var l = Encoding.UTF8.GetBytes(c, inLength, a, Size - 1);
                    a[l] = 0;            // Null terminator
                }
                else
                {
                    Span<byte> buffer = (sizeRequired < 4096) ? stackalloc byte[sizeRequired] : new byte[sizeRequired];
                    Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
                    Span<byte> d = new Span<byte>(a, Size);
                    buffer.Slice(0, Size).CopyTo(d);
                    a[Size - 1] = 0;
                }
            }
        }
    }
}

/// <summary>
/// A fixed 64-byte inline UTF-8 string buffer — a blittable, fixed-size component field that stores up to 63 bytes plus a null terminator in place (no heap
/// allocation). Input that does not fit is truncated to the buffer size. Comparison, equality, and hashing operate byte-wise over the inline buffer.
/// </summary>
[PublicAPI]
[DebuggerDisplay("String: {AsString}")]
public unsafe struct String64 : IComparable<String64>, IEquatable<String64>
{
    private const int Size = 64;
    private fixed byte _data[Size];

    /// <summary>
    /// Construct a String64 instance from a memory area containing the string
    /// </summary>
    /// <param name="stringAddr">Address of the memory area containing the UTF8 string data</param>
    /// <param name="length">Length of the <paramref name="stringAddr"/> memory area</param>
    public String64(byte* stringAddr, int length=64)
    {
        fixed (byte* a = _data)
        {
            new Span<byte>(stringAddr, length).CopyTo(new Span<byte>(a, 64));
        }
    }

    /// <summary>Returns a pointer to the first byte of the 64-byte inline buffer for read/write access. Valid only while the containing storage stays fixed in memory.</summary>
    public byte* GetStringContentAddr()
    {
        fixed (byte* a = _data)
        {
            return a;
        }
    }

    /// <summary>Read-only counterpart of <see cref="GetStringContentAddr"/>, callable on a <c>readonly</c> instance. Valid only while the containing storage stays fixed in memory.</summary>
    public readonly byte* GetStringContentAddrReaOnly()
    {
        fixed (byte* a = _data)
        {
            return a;
        }
    }

    /// <summary>Exposes the 64-byte inline buffer as a mutable <see cref="Span{T}"/> of bytes. Valid only while the containing storage stays fixed in memory.</summary>
    public Span<byte> AsSpan()
    {
        fixed (byte* a = _data)
        {
            return new Span<byte>(a, 64);
        }
    }

    /// <summary>Exposes the 64-byte inline buffer as a <see cref="ReadOnlySpan{T}"/> of bytes. Valid only while the containing storage stays fixed in memory.</summary>
    public readonly ReadOnlySpan<byte> AsReadOnlySpan()
    {
        fixed (byte* a = _data)
        {
            return new ReadOnlySpan<byte>(a, 64);
        }
    }

    /// <summary>Creates a <see cref="String64"/> from a managed string, encoding it as inline UTF-8 (truncated to fit the 64-byte buffer).</summary>
    public static implicit operator String64(string str) => new() { AsString = str };

    /// <summary>Gets or sets the string value, encoded as inline UTF-8 and null-terminated. The setter truncates input that does not fit the 64-byte buffer.</summary>
    public string AsString
    {
        get
        {
            fixed (byte* a = _data)
            {
                return Marshal.PtrToStringUTF8(new IntPtr(a));
            }
        }

        set
        {
            fixed (char* c = value)
            fixed (byte* a = _data)
            {
                var inLength = value.Length;
                var sizeRequired = Encoding.UTF8.GetByteCount(c, inLength);
                if (sizeRequired < Size)
                {
                    var l = Encoding.UTF8.GetBytes(c, inLength, a, Size - 1);
                    a[l] = 0;            // Null terminator
                }
                else
                {
                    Span<byte> buffer = (sizeRequired < 1024) ? stackalloc byte[sizeRequired] : new byte[sizeRequired];
                    Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
                    Span<byte> d = new Span<byte>(a, Size);
                    buffer.Slice(0, Size).CopyTo(d);
                    a[Size-1] = 0;
                }
            }
        }
    }

    internal void SetVariant(string value, bool truncate)
    {
        fixed (char* c = value)
        fixed (byte* a = _data)
        {
            var inLength = value.Length;
            var sizeRequired = Encoding.UTF8.GetByteCount(c, inLength);
            if (sizeRequired < (Size - 3))
            {
                var l = Encoding.UTF8.GetBytes(c, inLength, a+3, 60) + 3;
                a[0] = (byte)'s';
                a[1] = (byte)'t';
                a[2] = (byte)':';
                a[l] = 0;            // Null terminator
            }
            else
            {
                if (!truncate)
                {
                    throw new InvalidOperationException($"Can't set the given string into the variant, the string must not exceed {Size - 3} bytes as UTF8");
                }
                Span<byte> buffer = (sizeRequired < 1024) ? stackalloc byte[sizeRequired] : new byte[sizeRequired];
                Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
                Span<byte> d = new(a, Size);
                buffer[..(Size-3)].CopyTo(d[3..]);

                a[0] = (byte)'s';
                a[1] = (byte)'t';
                a[2] = (byte)':';
                a[Size-1] = 0;
            }
        }
    }

    internal void SetVariant(bool value)
    {
        _data[0] = (byte)'b';
        _data[1] = (byte)'o';
        _data[2] = (byte)':';
        _data[3] = value ? (byte)'1' : (byte)'0';
        _data[4] = 0;
    }

    internal void SetVariant(sbyte value)
    {
        var str = value.ToString();
        var inLength = str.Length;
        var size = Encoding.UTF8.GetByteCount(str);
        fixed (char* c = str)
        fixed (byte* a = _data)
        {
            Encoding.UTF8.GetBytes(c, inLength, a + 3, 61);
            a[0] = (byte)'s';
            a[1] = (byte)'b';
            a[2] = (byte)':';
            a[size + 3] = 0;
        }
    }

    internal void SetVariant(short value)
    {
        var str = value.ToString();
        var inLength = str.Length;
        var size = Encoding.UTF8.GetByteCount(str);
        fixed (char* c = str)
        fixed (byte* a = _data)
        {
            Encoding.UTF8.GetBytes(c, inLength, a + 3, 61);
            a[0] = (byte)'s';
            a[1] = (byte)'s';
            a[2] = (byte)':';
            a[size + 3] = 0;
        }
    }

    internal void SetVariant(int value)
    {
        var str = value.ToString();
        var inLength = str.Length;
        var size = Encoding.UTF8.GetByteCount(str);
        fixed (char* c = str)
        fixed (byte* a = _data)
        {
            Encoding.UTF8.GetBytes(c, inLength, a + 3, 61);
            a[0] = (byte)'s';
            a[1] = (byte)'i';
            a[2] = (byte)':';
            a[size + 3] = 0;
        }
    }

    internal void SetVariant(long value)
    {
        var str = value.ToString();
        var inLength = str.Length;
        var size = Encoding.UTF8.GetByteCount(str);
        fixed (char* c = str)
        fixed (byte* a = _data)
        {
            Encoding.UTF8.GetBytes(c, inLength, a + 3, 61);
            a[0] = (byte)'s';
            a[1] = (byte)'l';
            a[2] = (byte)':';
            a[size + 3] = 0;
        }
    }

    /// <summary>Ordinal byte-wise comparison of the two inline buffers.</summary>
    /// <param name="other">The value to compare against.</param>
    /// <returns>Negative, zero, or positive per lexicographic byte ordering.</returns>
    public int CompareTo(String64 other) => AsSpan().SequenceCompareTo(other.AsSpan());

    /// <summary>Byte-wise equality of the two inline buffers.</summary>
    /// <param name="other">The value to compare against.</param>
    /// <returns><c>true</c> when the buffers are byte-for-byte equal.</returns>
    public bool Equals(String64 other) => other.AsSpan().SequenceEqual(AsSpan());

    /// <summary>Byte-wise equality; <c>false</c> when <paramref name="obj"/> is not a <see cref="String64"/>.</summary>
    public override bool Equals(object obj) => obj is String64 other && Equals(other);

    /// <summary>32-bit <see cref="MurmurHash2"/> over the inline buffer bytes.</summary>
    public override int GetHashCode() => (int)MurmurHash2.Hash(AsSpan());

    /// <summary>Byte-wise equality.</summary>
    public static bool operator ==(String64 left, String64 right) => left.Equals(right);

    /// <summary>Byte-wise inequality.</summary>
    public static bool operator !=(String64 left, String64 right) => !left.Equals(right);
}
