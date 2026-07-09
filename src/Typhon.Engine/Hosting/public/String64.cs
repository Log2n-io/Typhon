// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Engine;

/// <summary>
/// A forward-only cursor over a <see cref="Span{T}"/> of bytes: each <c>Pop</c> reads a value (or sub-span) from the front and advances past it. A
/// <see langword="ref"/> <see langword="struct"/>, so it lives on the stack and cannot outlive the span it wraps. No bounds checking — the caller must not read
/// past the end.
/// </summary>
[ExcludeFromCodeCoverage]
unsafe public ref struct SpanStream
{
    private Span<byte> _data;

    /// <summary>Wraps <paramref name="data"/> as a forward-only byte cursor positioned at its start.</summary>
    /// <param name="data">The backing byte span; the stream reads from the front and advances the cursor.</param>
    public SpanStream(Span<byte> data)
    {
        _data = data;
    }

    /// <summary>Number of bytes remaining ahead of the cursor.</summary>
    public int Length => _data.Length;

    /// <summary>Reads <paramref name="length"/> elements of <typeparamref name="T"/> from the front and advances past them.</summary>
    /// <typeparam name="T">An unmanaged element type.</typeparam>
    /// <param name="length">Number of elements to read.</param>
    /// <returns>A <see cref="Span{T}"/> aliasing the popped region of the underlying buffer.</returns>
    public Span<T> PopSpan<T>(int length = 1) where T : unmanaged
    {
        var res = _data.Cast<byte, T>().Slice(0, length);
        _data = _data.Slice(sizeof(T) * length);
        return res;
    }

    /// <summary>Reads one <typeparamref name="T"/> from the front as a <see langword="ref"/> into the underlying buffer and advances past it.</summary>
    /// <typeparam name="T">An unmanaged element type.</typeparam>
    /// <returns>A writable reference to the popped value within the buffer.</returns>
    public ref T PopRef<T>() where T : unmanaged
    {
        var size = sizeof(T);
        var res = _data.Cast<byte, T>();
        _data = _data.Slice(size);
        return ref res[0];
    }
    /// <summary>Reads and returns one <typeparamref name="T"/> by value from the front, advancing past it.</summary>
    /// <typeparam name="T">An unmanaged element type.</typeparam>
    /// <returns>The popped value.</returns>
    public T Pop<T>() where T : unmanaged
    {
        var size = sizeof(T);
        var res = _data.Cast<byte, T>();
        _data = _data.Slice(size);
        return res[0];
    }
}

/// <summary>Helpers for reading and writing null-terminated UTF-8 strings in unmanaged memory.</summary>
[PublicAPI]
public static class StringExtensions
{
    internal unsafe static bool StoreString(string str, byte* dest, int destMaxSize)
    {
        var l = Encoding.UTF8.GetByteCount(str);
        if (l + 1 > destMaxSize)
        {
            return false;
        }

        fixed (char* c = str)
        {
            Encoding.UTF8.GetBytes(c, str.Length, dest, destMaxSize);
            dest[l] = 0;            // Null terminator
        }

        return true;
    }

    internal unsafe static string LoadString(byte* addr) => Marshal.PtrToStringUTF8((IntPtr)addr);

}

/// <summary>Minimal power-of-two test helpers.</summary>
public static class MathHelpers{
    /// <summary>Whether <paramref name="x"/> is a power of two. Returns <c>true</c> for <c>0</c>.</summary>
    /// <param name="x">The value to test.</param>
    /// <returns><c>true</c> if <paramref name="x"/> has at most one bit set.</returns>
    public static bool IsPow2(int x) => (x & (x - 1)) == 0;
    /// <summary>Whether <paramref name="x"/> is a power of two. Returns <c>true</c> for <c>0</c>.</summary>
    /// <param name="x">The value to test.</param>
    /// <returns><c>true</c> if <paramref name="x"/> has at most one bit set.</returns>
    public static bool IsPow2(long x) => (x & (x - 1)) == 0;
}
