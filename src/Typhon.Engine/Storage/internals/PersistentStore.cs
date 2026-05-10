using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

/// <summary>
/// Page store backed by a <see cref="ManagedPagedMMF"/> (memory-mapped file with page cache).
/// Every method delegates directly to the underlying MMF instance.
/// <para>
/// This is a <c>readonly struct</c> (8 bytes — one pointer). Used as a generic type parameter
/// on <c>CBS&lt;PersistentStore&gt;</c>. The JIT inlines all delegations, producing identical
/// assembly to the current non-generic code.
/// </para>
/// </summary>
[PublicAPI]
public readonly unsafe struct PersistentStore : IPageStore
{
    private readonly ManagedPagedMMF _mmf;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PersistentStore(ManagedPagedMMF mmf) => _mmf = mmf;

    /// <summary>
    /// Escape hatch for code needing <see cref="ManagedPagedMMF"/>-specific APIs
    /// (e.g. <c>AllocateChunkBasedSegment</c>, <c>AllocateSegment</c>, <c>CreateChangeSet</c>).
    /// </summary>
    public ManagedPagedMMF Mmf
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _mmf;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Page Access
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RequestPageEpoch(int filePageIndex, long epoch, out int memPageIndex)
        => _mmf.RequestPageEpoch(filePageIndex, epoch, out memPageIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RequestPageEpochUnchecked(int filePageIndex, long epoch, out int memPageIndex)
        => _mmf.RequestPageEpochUnchecked(filePageIndex, epoch, out memPageIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PageAccessor GetPage(int memPageIndex) => _mmf.GetPage(memPageIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* GetMemPageAddress(int memPageIndex) => _mmf.GetMemPageAddress(memPageIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* GetMemPageRawDataAddress(int memPageIndex) => _mmf.GetMemPageRawDataAddress(memPageIndex);

    public byte* MemPagesBaseAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _mmf.MemPagesBaseAddress;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dirty Tracking
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementActiveChunkWriters(int memPageIndex) => _mmf.IncrementActiveChunkWriters(memPageIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecrementActiveChunkWriters(int memPageIndex) => _mmf.DecrementActiveChunkWriters(memPageIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementDirty(int memPageIndex) => _mmf.IncrementDirty(memPageIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureDirtyAtLeast(int memPageIndex, int minValue) => _mmf.EnsureDirtyAtLeast(memPageIndex, minValue);

    // ═══════════════════════════════════════════════════════════════════════
    // Slot Ref Counting
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementSlotRefCount(int memPageIndex) => _mmf.IncrementSlotRefCount(memPageIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecrementSlotRefCount(int memPageIndex) => _mmf.DecrementSlotRefCount(memPageIndex);

    // ═══════════════════════════════════════════════════════════════════════
    // Latching
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryLatchPageExclusive(int memPageIndex) => _mmf.TryLatchPageExclusive(memPageIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UnlatchPageExclusive(int memPageIndex) => _mmf.UnlatchPageExclusive(memPageIndex);

    // ═══════════════════════════════════════════════════════════════════════
    // Growth
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AllocatePages(ref Span<int> pageIds, int startFrom, ChangeSet changeSet)
        => _mmf.AllocatePages(ref pageIds, startFrom, changeSet);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChangeSet CreateChangeSet() => new(_mmf);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetFilePageIndex(int memPageIndex) => _mmf.GetFilePageIndex(memPageIndex);

    // ═══════════════════════════════════════════════════════════════════════
    // Infrastructure
    // ═══════════════════════════════════════════════════════════════════════

    public EpochManager EpochManager
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _mmf.EpochManager;
    }

    public bool IsDisposed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _mmf.IsDisposed;
    }
}
