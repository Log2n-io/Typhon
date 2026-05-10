using System;

namespace Typhon.Engine.Internals;

internal readonly unsafe struct StoreSpan
{
    public StoreSpan(Span<byte> span)
    {
        fixed (byte* ptr = span)
        {
            _address = ptr;
            _length = span.Length;
        }
    }

    public Span<T> ToSpan<T>() where T : unmanaged => new(_address, _length / sizeof(T));

    public static explicit operator StoreSpan(Span<byte> span) => new(span);
    public static explicit operator Span<byte>(StoreSpan span) => new(span._address, span._length);

    private readonly void* _address;
    private readonly int _length;
}

internal static class StoreSpanExtensions
{
    public static StoreSpan ToStoreSpan<T>(this Span<T> span) where T : unmanaged => new(span.Cast<T, byte>());
}
