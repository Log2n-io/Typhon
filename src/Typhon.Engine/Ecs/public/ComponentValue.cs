using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// 128-byte self-contained component value used during entity spawning.
/// Carries component type ID, data size, and up to 112 bytes of inline component data.
/// </summary>
/// <remarks>
/// <para>Created exclusively via <see cref="Comp{T}.Set"/> or <see cref="Comp{T}.Default"/>.</para>
/// <para>Components larger than 112 bytes must use incremental spawn (spawn then write).</para>
/// <para>Layout: 4B ComponentTypeId + 4B DataSize + 4B reserved = 12B header, then 112B payload, then 4B pad = 128B.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 128)]
[PublicAPI]
public unsafe struct ComponentValue
{
    /// <summary>Maximum payload size in bytes.</summary>
    public const int MaxPayloadSize = 112;

    internal readonly int ComponentTypeId;
    internal readonly int DataSize;
    private readonly int _headerPad; // aligns _data to 12-byte offset, ensures 128B total with 112B payload

    // 12 bytes header above, 112 bytes payload below, 4 bytes implicit tail padding = 128B total
    private fixed byte _data[MaxPayloadSize];

    /// <summary>
    /// This value's payload bytes, as a span over the inline buffer (#1102).
    /// </summary>
    /// <remarks>
    /// <b>A span and not a pointer, and an accessor rather than the arithmetic repeated at each call site.</b> Two places already reached the payload by
    /// adding a literal 12 to a reinterpreted <c>ref</c> — the header's width — and a third was about to. The offset belongs with the layout it comes from.
    /// It is a span because a <see cref="ComponentValue"/> is frequently an element of a managed array, where a <c>byte*</c> would be a pointer over GC
    /// data; <see cref="MemoryMarshal.CreateReadOnlySpan{T}"/> keeps the GC aware of it.
    /// </remarks>
    /// <remarks>
    /// <b><c>readonly</c> on purpose:</b> <see cref="ComponentValue"/> cannot be a <c>readonly struct</c> (a <c>fixed</c> buffer is not), so without it
    /// every access through an <c>in</c> parameter — which is how the spawn path passes one, per value written — emits a defensive 128-byte copy.
    /// The length is clamped because a malformed <see cref="DataSize"/> would otherwise span past the inline buffer.
    /// </remarks>
    internal readonly ReadOnlySpan<byte> Payload =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref Unsafe.As<ComponentValue, byte>(ref Unsafe.AsRef(in this)), HeaderSize),
            Math.Min(DataSize, MaxPayloadSize));

    /// <summary>Bytes of header before the payload: the component type id, the data size and the alignment pad.</summary>
    internal const int HeaderSize = 12;

    /// <summary>Create a ComponentValue from raw bytes. Used by reflection-based callers (Shell CLI).</summary>
    internal static ComponentValue CreateFromRaw(int componentTypeId, byte* data, int dataSize)
    {
        if (CheckConfig.Enabled && dataSize > MaxPayloadSize)
        {
            ThrowHelper.ThrowInvalidOp($"Data size {dataSize} exceeds max payload {MaxPayloadSize}");
        }
        var cv = new ComponentValue();
        Unsafe.AsRef(in cv.ComponentTypeId) = componentTypeId;
        Unsafe.AsRef(in cv.DataSize) = dataSize;
        new ReadOnlySpan<byte>(data, dataSize).CopyTo(new Span<byte>(cv._data, MaxPayloadSize));
        return cv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ComponentValue Create<T>(int componentTypeId, in T value) where T : unmanaged
    {
        if (CheckConfig.Enabled && sizeof(T) > MaxPayloadSize)
        {
            ThrowHelper.ThrowInvalidOp($"Component size {sizeof(T)} exceeds max payload {MaxPayloadSize}");
        }
        var cv = new ComponentValue();
        Unsafe.AsRef(in cv.ComponentTypeId) = componentTypeId;
        Unsafe.AsRef(in cv.DataSize) = sizeof(T);
        Unsafe.WriteUnaligned(ref cv._data[0], value);
        return cv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T Read<T>() where T : unmanaged
    {
        if (CheckConfig.Enabled && sizeof(T) != DataSize)
        {
            ThrowHelper.ThrowInvalidOp($"Read<{typeof(T).Name}> size {sizeof(T)} != stored DataSize {DataSize}");
        }
        return Unsafe.ReadUnaligned<T>(ref _data[0]);
    }
}
