using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

[ExcludeFromCodeCoverage]
internal class ConcurrentBitmap
{
    private readonly Memory<long> _data;
    public ConcurrentBitmap(int bitCount)
    {
        var length = (bitCount + 63) / 64;
        _data = new long[length];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index)
    {
        var offset = index >> 6;
        var mask = 1L << (index & 0x3F);

        Interlocked.Or(ref _data.Span[offset], mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear(int index)
    {
        var offset = index >> 6;
        var mask = ~(1L << (index & 0x3F));

        Interlocked.And(ref _data.Span[offset], mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSet(int index)
    {
        var offset = index >> 6;
        var mask = 1L << (index & 0x3F);
        return (_data.Span[offset] & mask) != 0L;
    }
}