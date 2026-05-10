using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Minimal CRUD helper for engine-internal system schema persistence (ComponentR1, ArchetypeR1, etc.).
/// Single-threaded, no MVCC, no WAL, no conflict detection, no revision tracking.
/// Operates directly on ComponentTable's ComponentSegment using chunkId as the stable identifier.
/// </summary>
internal static unsafe class SystemCrud
{
    /// <summary>
    /// Create a system entity: allocate chunk, copy data, return chunkId as the stable identifier.
    /// </summary>
    public static int Create<T>(ComponentTable table, ref T data, EpochManager epochManager, ChangeSet cs) where T : unmanaged
    {
        using var guard = EpochGuard.Enter(epochManager);

        // Allocate chunk for component data
        int chunkId = table.ComponentSegment.AllocateChunk(false, cs);

        // Copy data into chunk (at ComponentOverhead offset)
        var compAccessor = table.ComponentSegment.CreateChunkAccessor(cs);
        var dst = compAccessor.GetChunkAsSpan(chunkId, true);
        int overhead = table.ComponentOverhead;
        int compSize = Math.Min(sizeof(T), table.ComponentStorageSize);
        new Span<byte>(Unsafe.AsPointer(ref data), compSize).CopyTo(dst.Slice(overhead));
        compAccessor.Dispose();

        return chunkId;
    }

    /// <summary>
    /// Read a system entity by chunkId: read chunk data directly.
    /// </summary>
    public static bool Read<T>(ComponentTable table, int chunkId, out T data, EpochManager epochManager) where T : unmanaged
    {
        using var guard = EpochGuard.Enter(epochManager);

        // Read chunk data
        var compAccessor = table.ComponentSegment.CreateChunkAccessor();
        var src = compAccessor.GetChunkAsReadOnlySpan(chunkId);
        int overhead = table.ComponentOverhead;
        int compSize = Math.Min(sizeof(T), table.ComponentStorageSize);

        data = default;
        src.Slice(overhead, compSize).CopyTo(new Span<byte>(Unsafe.AsPointer(ref data), compSize));
        compAccessor.Dispose();

        return true;
    }

    /// <summary>
    /// Update a system entity: overwrite data in-place at the known chunkId (no reallocation).
    /// </summary>
    public static void Update<T>(ComponentTable table, int chunkId, ref T data, EpochManager epochManager, ChangeSet cs) where T : unmanaged
    {
        using var guard = EpochGuard.Enter(epochManager);

        // Overwrite data in-place
        var compAccessor = table.ComponentSegment.CreateChunkAccessor(cs);
        var dst = compAccessor.GetChunkAsSpan(chunkId, true);
        int overhead = table.ComponentOverhead;
        int compSize = Math.Min(sizeof(T), table.ComponentStorageSize);
        new Span<byte>(Unsafe.AsPointer(ref data), compSize).CopyTo(dst.Slice(overhead));
        compAccessor.Dispose();
    }
}
