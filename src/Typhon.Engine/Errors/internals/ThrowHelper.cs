using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Centralized throw helpers with <see cref="MethodImplOptions.NoInlining"/> to keep hot-path method bodies small.
/// The JIT won't inline throw paths into callers, preserving cache-friendly code layout.
/// </summary>
internal static class ThrowHelper
{
    // --- Existing (moved from ChunkAccessor<PersistentStore>.cs) ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    public static void ThrowArgument(string message) => throw new ArgumentException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    public static void ThrowInvalidOp(string message) => throw new InvalidOperationException(message);

    // --- New — Tier 1 ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    public static void ThrowLockTimeout(string resourceName, TimeSpan waitDuration) => throw new LockTimeoutException(resourceName, waitDuration);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    public static void ThrowResourceExhausted(string resourcePath, ResourceType resourceType, long currentUsage, long limit)
        => throw new ResourceExhaustedException(resourcePath, resourceType, currentUsage, limit);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    public static void ThrowCorruption(string componentName, int pageIndex, string detail) => throw new CorruptionException(componentName, pageIndex, detail);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    public static void ThrowEpochRegistryExhausted() => throw new ResourceExhaustedException("Concurrency/EpochThreadRegistry", 
        ResourceType.Synchronization, EpochThreadRegistry.MaxSlots, EpochThreadRegistry.MaxSlots);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    public static void ThrowTransactionTimeout(long transactionId, TimeSpan waitDuration) => throw new TransactionTimeoutException(transactionId, waitDuration);

    // --- Index ---

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowUniqueConstraintViolation() => throw new UniqueConstraintViolationException();

    // --- BTree API misuse ---

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowEnumerateRangeOnAllowMultiple() => 
        throw new InvalidOperationException("EnumerateRange/EnumerateRangeDescending cannot be used on AllowMultiple indexes. Use EnumerateRangeMultiple/EnumerateRangeMultipleDescending instead.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowEnumerateRangeMultipleOnUnique() => 
        throw new InvalidOperationException("EnumerateRangeMultiple/EnumerateRangeMultipleDescending cannot be used on unique indexes. Use EnumerateRange/EnumerateRangeDescending instead.");

    // --- Storage ---

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowDatabaseLocked(string databasePath, int ownerPid, string ownerMachine, DateTimeOffset startedAt)
        => throw new DatabaseLockedException(databasePath, ownerPid, ownerMachine, startedAt);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowPageCacheBackpressureTimeout(int dirtyPageCount, int epochProtectedCount, TimeSpan waitDuration)
        => throw new PageCacheBackpressureTimeoutException(dirtyPageCount, epochProtectedCount, waitDuration);

    // --- Durability ---

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowWalBackPressureTimeout(int requestedBytes, TimeSpan waitDuration) => throw new WalBackPressureTimeoutException(requestedBytes, waitDuration);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowWalClaimTooLarge(int requestedBytes, int bufferCapacity) => throw new WalClaimTooLargeException(requestedBytes, bufferCapacity);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowWalWriteFailure(Exception innerException) => throw new WalWriteException(innerException);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowWalSegmentError(string segmentPath, string detail) => throw new WalSegmentException(segmentPath, detail);
}
