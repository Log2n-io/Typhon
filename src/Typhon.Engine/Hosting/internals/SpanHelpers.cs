using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

internal static class SpanHelpers
{
    public static Span<TTo> Cast<TFRom, TTo>(this Span<TFRom> span) where TFRom : struct where TTo : struct => MemoryMarshal.Cast<TFRom, TTo>(span);
    public static ReadOnlySpan<TTo> Cast<TFRom, TTo>(this ReadOnlySpan<TFRom> span) where TFRom : struct where TTo : struct => MemoryMarshal.Cast<TFRom, TTo>(span);
    public unsafe static void Split<TA, TB>(this Span<byte> span, out Span<TA> a, out Span<TB> b) 
        where TA : unmanaged 
        where TB : unmanaged
    {
        var size = sizeof(TA);
        a = span.Slice(0, size).Cast<byte, TA>();
        b = span.Slice(size).Cast<byte, TB>();
    }
}