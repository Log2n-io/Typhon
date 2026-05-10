using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Lightweight, zero-overhead typed accessor for an 8KB page in the memory-mapped cache.
/// Provides type-safe access to the three page regions: Header (64B), Metadata (128B), RawData (8000B).
/// </summary>
/// <remarks>
/// This is a ref struct wrapping a single <c>byte*</c>. The JIT elides the wrapper entirely when methods are inlined,
/// producing identical machine code to hand-written pointer arithmetic.
/// For heavy-duty internal use (e.g., <see cref="LogicalSegment.InitHeader"/>, <see cref="ChunkAccessor"/>),
/// use <see cref="Address"/> to get the raw pointer.
/// </remarks>
[PublicAPI]
public unsafe ref struct PageAccessor
{
    private readonly byte* _addr;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PageAccessor(byte* addr) => _addr = addr;

    // ── Escape hatch ─────────────────────────────────────────────

    /// <summary>Raw pointer to the page start. For InitHeader, ChunkAccessor, and similar internal use.</summary>
    public byte* Address
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _addr;
    }

    // ── Header region (0..63) ────────────────────────────────────

    /// <summary>Access the <see cref="PageBaseHeader"/> at offset 0.</summary>
    public ref PageBaseHeader Header
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Unsafe.AsRef<PageBaseHeader>(_addr);
    }

    /// <summary>Page block flags (first byte). No struct load — single byte read.</summary>
    public PageBlockFlags Flags
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (PageBlockFlags)_addr[0];
    }

    /// <summary>True if this page is the root page of its logical segment.</summary>
    public bool IsRoot
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_addr[0] & (byte)PageBlockFlags.IsLogicalSegmentRoot) != 0;
    }

    /// <summary>Access the full page start as an arbitrary unmanaged struct.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T As<T>() where T : unmanaged
        => ref Unsafe.AsRef<T>(_addr);

    /// <summary>Access an unmanaged struct at a specific byte offset within the page.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T StructAt<T>(int byteOffset) where T : unmanaged
        => ref Unsafe.AsRef<T>(_addr + byteOffset);

    // ── Metadata region (64..191) ────────────────────────────────

    /// <summary>The 128-byte metadata region (chunk occupancy bitmaps) as <see cref="Span{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> Metadata<T>() where T : unmanaged
        => new Span<T>(_addr + PagedMMF.PageBaseHeaderSize, PagedMMF.PageMetadataSize / Unsafe.SizeOf<T>());

    /// <summary>The 128-byte metadata region as <see cref="ReadOnlySpan{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> MetadataReadOnly<T>() where T : unmanaged
        => new ReadOnlySpan<T>(_addr + PagedMMF.PageBaseHeaderSize, PagedMMF.PageMetadataSize / Unsafe.SizeOf<T>());

    /// <summary>A sub-region of metadata starting at <paramref name="byteOffset"/> for <paramref name="count"/> elements of <typeparamref name="T"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> Metadata<T>(int byteOffset, int count) where T : unmanaged
        => new Span<T>(_addr + PagedMMF.PageBaseHeaderSize + byteOffset, count);

    /// <summary>A sub-region of metadata as <see cref="ReadOnlySpan{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> MetadataReadOnly<T>(int byteOffset, int count) where T : unmanaged
        => new ReadOnlySpan<T>(_addr + PagedMMF.PageBaseHeaderSize + byteOffset, count);

    // ── Raw data region (192..8191) ──────────────────────────────

    /// <summary>The full 8000-byte raw data region as <see cref="Span{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> RawData<T>() where T : unmanaged
        => new Span<T>(_addr + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize / Unsafe.SizeOf<T>());

    /// <summary>The full 8000-byte raw data region as <see cref="ReadOnlySpan{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> RawDataReadOnly<T>() where T : unmanaged
        => new ReadOnlySpan<T>(_addr + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize / Unsafe.SizeOf<T>());

    /// <summary>A sub-region of raw data starting at <paramref name="byteOffset"/> for <paramref name="count"/> elements of <typeparamref name="T"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> RawData<T>(int byteOffset, int count) where T : unmanaged
        => new Span<T>(_addr + PagedMMF.PageHeaderSize + byteOffset, count);

    /// <summary>A sub-region of raw data as <see cref="ReadOnlySpan{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> RawDataReadOnly<T>(int byteOffset, int count) where T : unmanaged
        => new ReadOnlySpan<T>(_addr + PagedMMF.PageHeaderSize + byteOffset, count);
}
