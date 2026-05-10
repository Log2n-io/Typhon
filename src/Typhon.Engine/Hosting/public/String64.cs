// unset

using JetBrains.Annotations;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Engine;

[ExcludeFromCodeCoverage]
unsafe public ref struct SpanStream
{
    private Span<byte> _data;

    public SpanStream(Span<byte> data)
    {
        _data = data;
    }

    public int Length => _data.Length;

    public Span<T> PopSpan<T>(int length = 1) where T : unmanaged
    {
        var res = _data.Cast<byte, T>().Slice(0, length);
        _data = _data.Slice(sizeof(T) * length);
        return res;
    }

    public ref T PopRef<T>() where T : unmanaged
    {
        var size = sizeof(T);
        var res = _data.Cast<byte, T>();
        _data = _data.Slice(size);
        return ref res[0];
    }
    public T Pop<T>() where T : unmanaged
    {
        var size = sizeof(T);
        var res = _data.Cast<byte, T>();
        _data = _data.Slice(size);
        return res[0];
    }
}

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

public static class MathHelpers{
    public static bool IsPow2(int x) => (x & (x - 1)) == 0;
    public static bool IsPow2(long x) => (x & (x - 1)) == 0;
}
