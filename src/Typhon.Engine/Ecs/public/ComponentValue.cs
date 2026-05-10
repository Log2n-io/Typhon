using System;
using System.Diagnostics;
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

    /// <summary>Create a ComponentValue from raw bytes. Used by reflection-based callers (Shell CLI).</summary>
    internal static ComponentValue CreateFromRaw(int componentTypeId, byte* data, int dataSize)
    {
        Debug.Assert(dataSize <= MaxPayloadSize, $"Data size {dataSize} exceeds max payload {MaxPayloadSize}");
        var cv = new ComponentValue();
        Unsafe.AsRef(in cv.ComponentTypeId) = componentTypeId;
        Unsafe.AsRef(in cv.DataSize) = dataSize;
        new ReadOnlySpan<byte>(data, dataSize).CopyTo(new Span<byte>(cv._data, MaxPayloadSize));
        return cv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ComponentValue Create<T>(int componentTypeId, in T value) where T : unmanaged
    {
        Debug.Assert(sizeof(T) <= MaxPayloadSize, $"Component size {sizeof(T)} exceeds max payload {MaxPayloadSize}");
        var cv = new ComponentValue();
        Unsafe.AsRef(in cv.ComponentTypeId) = componentTypeId;
        Unsafe.AsRef(in cv.DataSize) = sizeof(T);
        Unsafe.WriteUnaligned(ref cv._data[0], value);
        return cv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T Read<T>() where T : unmanaged
    {
        Debug.Assert(sizeof(T) == DataSize, $"Read<{typeof(T).Name}> size {sizeof(T)} != stored DataSize {DataSize}");
        return Unsafe.ReadUnaligned<T>(ref _data[0]);
    }
}
