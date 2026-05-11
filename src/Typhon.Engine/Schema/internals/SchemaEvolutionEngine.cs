using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Result of a compatible schema migration, containing the new segments and their root page indices.
/// </summary>
internal readonly struct MigrationResult
{
    public int NewComponentSPI { get; init; }
    public int NewVersionSPI { get; init; }
    public ChunkBasedSegment<PersistentStore> NewComponentSegment { get; init; }
    public ChunkBasedSegment<PersistentStore> NewRevisionSegment { get; init; }
    public int EntitiesMigrated { get; init; }
    public long ElapsedMs { get; init; }
}

/// <summary>
/// Describes how a single surviving field maps from old layout to new layout during migration.
/// </summary>
internal readonly struct FieldMapEntry
{
    public readonly int FieldId;
    public readonly int OldOffset;
    public readonly int NewOffset;
    public readonly int OldSize;
    public readonly int NewSize;
    public readonly FieldType OldType;
    public readonly FieldType NewType;
    public readonly bool NeedsWidening;

    public FieldMapEntry(int fieldId, int oldOffset, int newOffset, int oldSize, int newSize, FieldType oldType, FieldType newType)
    {
        FieldId = fieldId;
        OldOffset = oldOffset;
        NewOffset = newOffset;
        OldSize = oldSize;
        NewSize = newSize;
        OldType = oldType;
        NewType = newType;
        NeedsWidening = oldType != newType;
    }
}

/// <summary>
/// Performs eager schema migration at startup for compatible changes (field add, remove, reorder, type widening).
/// Copies entity data from old-stride segments to new-stride segments with field remapping, preserving ChunkIds so that PK indexes, secondary indexes on
/// surviving fields, and revision chain pointers remain valid.
/// </summary>
/// <remarks>
/// <para>Design reference: <c>claude/design/Schema/03-compatible-evolution.md</c></para>
/// <para>Key decisions: D4 (eager at startup), D7 (HEAD only for revisions), D8 (auto-resolve widenings).</para>
/// </remarks>
internal static class SchemaEvolutionEngine
{
    /// <summary>
    /// Builds the field mapping between old (persisted) and new (runtime) layouts.
    /// Only includes surviving fields (present in both layouts). New fields are zero-filled by segment creation.
    /// </summary>
    internal static FieldMapEntry[] BuildFieldMap(FieldR1[] persistedFields, DBComponentDefinition newDefinition)
    {
        var persistedById = new Dictionary<int, FieldR1>(persistedFields.Length);
        foreach (var f in persistedFields)
        {
            if (!f.IsStatic)
            {
                persistedById[f.FieldId] = f;
            }
        }

        var entries = new List<FieldMapEntry>();
        foreach (var kvp in newDefinition.FieldsByName)
        {
            var runtimeField = kvp.Value;
            if (runtimeField.IsStatic)
            {
                continue;
            }

            if (persistedById.TryGetValue(runtimeField.FieldId, out var persisted))
            {
                entries.Add(new FieldMapEntry(runtimeField.FieldId, persisted.OffsetInComponentStorage, runtimeField.OffsetInComponentStorage, 
                    persisted.SizeInComponentStorage, runtimeField.SizeInComponentStorage, persisted.Type, runtimeField.Type));
            }
        }

        return entries.ToArray();
    }

    /// <summary>
    /// Applies a widening conversion from old type to new type at the given pointers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void ApplyWidening(byte* src, byte* dst, FieldType oldType, FieldType newType, int oldSize, int newSize)
    {
        // Signed integer widening: sign-extend
        if (IsSignedInteger(oldType) && IsSignedInteger(newType))
        {
            Buffer.MemoryCopy(src, dst, newSize, oldSize);
            var signByte = (src[oldSize - 1] & 0x80) != 0 ? (byte)0xFF : (byte)0x00;
            Unsafe.InitBlockUnaligned(dst + oldSize, signByte, (uint)(newSize - oldSize));
            return;
        }

        // Unsigned integer widening: zero-extend (remaining bytes already zero from page init)
        if (IsUnsignedInteger(oldType) && (IsUnsignedInteger(newType) || IsSignedInteger(newType)))
        {
            Buffer.MemoryCopy(src, dst, newSize, oldSize);
            return;
        }

        // Float → Double: IEEE754 promotion
        if (oldType == FieldType.Float && newType == FieldType.Double)
        {
            *(double*)dst = *(float*)src;
            return;
        }

        // Vector/Quaternion float → double: per-component promotion
        if (oldType == FieldType.Point2F && newType == FieldType.Point2D)
        {
            PromoteFloatComponentsToDouble(src, dst, 2);
            return;
        }

        if (oldType == FieldType.Point3F && newType == FieldType.Point3D)
        {
            PromoteFloatComponentsToDouble(src, dst, 3);
            return;
        }

        if (oldType == FieldType.Point4F && newType == FieldType.Point4D)
        {
            PromoteFloatComponentsToDouble(src, dst, 4);
            return;
        }

        if (oldType == FieldType.QuaternionF && newType == FieldType.QuaternionD)
        {
            PromoteFloatComponentsToDouble(src, dst, 4);
            return;
        }

        // String64 → String1024: copy bytes (remaining already zero)
        if (oldType == FieldType.String64 && newType == FieldType.String1024)
        {
            Buffer.MemoryCopy(src, dst, newSize, oldSize);
            return;
        }

        // Fallback: raw copy (should not reach here for valid widenings)
        Buffer.MemoryCopy(src, dst, newSize, Math.Min(oldSize, newSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void PromoteFloatComponentsToDouble(byte* src, byte* dst, int componentCount)
    {
        var srcFloats = (float*)src;
        var dstDoubles = (double*)dst;
        for (int i = 0; i < componentCount; i++)
        {
            dstDoubles[i] = srcFloats[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSignedInteger(FieldType type) => type == FieldType.Byte || type == FieldType.Short || type == FieldType.Int || type == FieldType.Long;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUnsignedInteger(FieldType type) => type == FieldType.UByte || type == FieldType.UShort || type == FieldType.UInt || type == FieldType.ULong;

    /// <summary>
    /// Migrates all occupied entities from old segment to new segment using the field map. Preserves ChunkIds by reserving the same indices in the new segment.
    /// </summary>
    internal static unsafe int MigrateEntities(ChunkBasedSegment<PersistentStore> oldSeg, ChunkBasedSegment<PersistentStore> newSeg, FieldMapEntry[] fieldMap, int oldOverhead, int newOverhead,
        ChangeSet changeSet)
    {
        var capacity = oldSeg.ChunkCapacity;
        var entitiesMigrated = 0;

        newSeg.EnsureCapacity(capacity, changeSet);

        var oldAccessor = oldSeg.CreateChunkAccessor();
        var newAccessor = newSeg.CreateChunkAccessor(changeSet);

        try
        {
            for (int chunkId = 1; chunkId < capacity; chunkId++)
            {
                if (!oldSeg.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                newSeg.ReserveChunk(chunkId);

                var oldPtr = oldAccessor.GetChunkAddress(chunkId);
                var newPtr = newAccessor.GetChunkAddress(chunkId, true);

                // Zero-fill the new chunk so added fields get default values (not stale memory)
                new Span<byte>(newPtr, newSeg.Stride).Clear();

                // Copy overhead section (AllowMultiple index element IDs stored before component data)
                if (oldOverhead > 0 && newOverhead > 0)
                {
                    var overheadCopySize = Math.Min(oldOverhead, newOverhead);
                    Buffer.MemoryCopy(oldPtr, newPtr, overheadCopySize, overheadCopySize);
                }

                // Copy/widen each surviving field
                for (int i = 0; i < fieldMap.Length; i++)
                {
                    ref readonly var entry = ref fieldMap[i];
                    var srcField = oldPtr + oldOverhead + entry.OldOffset;
                    var dstField = newPtr + newOverhead + entry.NewOffset;

                    if (entry.NeedsWidening)
                    {
                        ApplyWidening(srcField, dstField, entry.OldType, entry.NewType, entry.OldSize, entry.NewSize);
                    }
                    else
                    {
                        Buffer.MemoryCopy(srcField, dstField, entry.OldSize, entry.OldSize);
                    }
                }

                entitiesMigrated++;
            }
        }
        finally
        {
            newAccessor.Dispose();
            oldAccessor.Dispose();
        }

        return entitiesMigrated;
    }

    /// <summary>
    /// Migrates the revision chain keeping only the HEAD revision per entity. Preserves chunkIds so the PK index's compRevFirstChunkId references remain valid.
    /// </summary>
    internal static unsafe void MigrateRevisionChain(ChunkBasedSegment<PersistentStore> oldRevSeg, ChunkBasedSegment<PersistentStore> newRevSeg, ChunkBasedSegment<PersistentStore> oldCompSeg, ChangeSet changeSet)
    {
        var capacity = oldRevSeg.ChunkCapacity;
        newRevSeg.EnsureCapacity(capacity, changeSet);

        var oldAccessor = oldRevSeg.CreateChunkAccessor();
        var newAccessor = newRevSeg.CreateChunkAccessor(changeSet);

        try
        {
            for (int chunkId = 1; chunkId < capacity; chunkId++)
            {
                if (!oldRevSeg.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                var oldChunk = oldAccessor.GetChunkAddress(chunkId);
                ref var header = ref Unsafe.AsRef<CompRevStorageHeader>(oldChunk);

                if (header.ChainLength < 1 || header.ItemCount < 1)
                {
                    continue;
                }

                var headElement = FindHeadElement(ref header, oldChunk, ref oldAccessor);

                if (headElement.ComponentChunkId <= 0 || headElement.ComponentChunkId >= oldCompSeg.ChunkCapacity || 
                    !oldCompSeg.IsChunkAllocated(headElement.ComponentChunkId))
                {
                    continue;
                }

                newRevSeg.ReserveChunk(chunkId);

                var newChunk = newAccessor.GetChunkAddress(chunkId, true);
                new Span<byte>(newChunk, ComponentRevisionManager.CompRevChunkSize).Clear();

                ref var newHeader = ref Unsafe.AsRef<CompRevStorageHeader>(newChunk);
                newHeader.NextChunkId = 0;
                newHeader.ChainLength = 1;
                newHeader.ItemCount = 1;
                newHeader.FirstItemIndex = 0;
                newHeader.LastCommitRevisionIndex = 0;
                newHeader.CommitSequence = header.CommitSequence;
                newHeader.EntityPK = header.EntityPK;

                var elements = (CompRevStorageElement*)(newChunk + Unsafe.SizeOf<CompRevStorageHeader>());
                elements[0] = headElement;
            }
        }
        finally
        {
            newAccessor.Dispose();
            oldAccessor.Dispose();
        }
    }

    /// <summary>
    /// Finds the HEAD (most recent) revision element in a revision chain.
    /// </summary>
    private static unsafe CompRevStorageElement FindHeadElement(ref CompRevStorageHeader header, byte* rootChunk, ref ChunkAccessor<PersistentStore> accessor)
    {
        var headIndex = header.FirstItemIndex + header.ItemCount - 1;
        var (chunkIndex, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(headIndex);

        if (chunkIndex == 0)
        {
            var elements = (CompRevStorageElement*)(rootChunk + Unsafe.SizeOf<CompRevStorageHeader>());
            return elements[indexInChunk];
        }

        // Walk the chain to find the target chunk
        var currentChunkId = Unsafe.AsRef<int>(rootChunk); // NextChunkId is the first field
        for (int i = 1; i < chunkIndex; i++)
        {
            var chunkPtr = accessor.GetChunkAddress(currentChunkId);
            currentChunkId = *(int*)chunkPtr;
        }

        var targetChunkPtr = accessor.GetChunkAddress(currentChunkId);
        var overflowElements = (CompRevStorageElement*)(targetChunkPtr + sizeof(int));
        return overflowElements[indexInChunk];
    }

    /// <summary>
    /// Orchestrates a full compatible schema migration.
    /// </summary>
    internal static MigrationResult Migrate(ManagedPagedMMF mmf, EpochManager epochManager, SchemaDiff diff, FieldR1[] persistedFields,
        ComponentR1 persistedComp, DBComponentDefinition newDefinition, ILogger log, Action<MigrationProgressEventArgs> progressCallback = null)
    {
        var sw = Stopwatch.StartNew();

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.Analyzing, PercentComplete = 0, Elapsed = sw.Elapsed,
        });

        var fieldMap = BuildFieldMap(persistedFields, newDefinition);

        var oldOverhead = persistedComp.CompOverhead;
        var newOverhead = newDefinition.ComponentStorageOverhead;
        var oldStride = persistedComp.CompSize + oldOverhead;
        var newStride = newDefinition.ComponentStorageTotalSize;

        log?.LogInformation("Schema migration for '{Name}': old stride={OldStride}, new stride={NewStride}, {FieldCount} surviving fields",
            diff.ComponentName, oldStride, newStride, fieldMap.Length);

        var changeSet = mmf.CreateChangeSet();

        using var guard = EpochGuard.Enter(epochManager);

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.AllocatingSegments, PercentComplete = 5, Elapsed = sw.Elapsed,
        });

        // Load old component segment with OLD stride
        var oldCompSeg = mmf.LoadChunkBasedSegment(persistedComp.ComponentSPI, oldStride);

        // Allocate new component segment with NEW stride
        var initialPages = Math.Max(4, oldCompSeg.Length);
        var newCompSeg = mmf.AllocateChunkBasedSegment(PageBlockType.None, initialPages, newStride, changeSet);

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.MigratingEntities, TotalEntities = oldCompSeg.AllocatedChunkCount,
            PercentComplete = 10, Elapsed = sw.Elapsed,
        });

        // Migrate entity data
        var entitiesMigrated = MigrateEntities(oldCompSeg, newCompSeg, fieldMap, oldOverhead, newOverhead, changeSet);

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.RecreatingRevisionChain,
            EntitiesMigrated = entitiesMigrated, TotalEntities = entitiesMigrated, PercentComplete = 70, Elapsed = sw.Elapsed,
        });

        // Load old revision segment and allocate new one
        var oldRevSeg = mmf.LoadChunkBasedSegment(persistedComp.VersionSPI, ComponentRevisionManager.CompRevChunkSize);
        var revInitialPages = Math.Max(4, oldRevSeg.Length);
        var newRevSeg = mmf.AllocateChunkBasedSegment(PageBlockType.None, revInitialPages, ComponentRevisionManager.CompRevChunkSize, changeSet);

        // Migrate revision chains (HEAD only)
        MigrateRevisionChain(oldRevSeg, newRevSeg, oldCompSeg, changeSet);

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.Flushing,
            EntitiesMigrated = entitiesMigrated, TotalEntities = entitiesMigrated, PercentComplete = 90, Elapsed = sw.Elapsed,
        });

        // Flush all changes to disk before updating SPIs
        changeSet.SaveChanges();
        mmf.FlushToDisk();

        // Delete old segments (best-effort cleanup — orphaned pages if crash here are harmless)
        mmf.DeleteSegment(oldCompSeg);
        mmf.DeleteSegment(oldRevSeg);

        sw.Stop();

        log?.LogInformation("Schema migration for '{Name}' complete: {Count} entities migrated in {ElapsedMs}ms",
            diff.ComponentName, entitiesMigrated, sw.ElapsedMilliseconds);

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.Complete,
            EntitiesMigrated = entitiesMigrated, TotalEntities = entitiesMigrated, PercentComplete = 100, Elapsed = sw.Elapsed,
        });

        return new MigrationResult
        {
            NewComponentSPI = newCompSeg.RootPageIndex,
            NewVersionSPI = newRevSeg.RootPageIndex,
            NewComponentSegment = newCompSeg,
            NewRevisionSegment = newRevSeg,
            EntitiesMigrated = entitiesMigrated,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Orchestrates a full user-function-driven schema migration for breaking changes.
    /// Follows the same segment lifecycle as <see cref="Migrate"/> but delegates per-entity transformation
    /// to the migration chain instead of the field-map approach.
    /// </summary>
    internal static MigrationResult MigrateWithFunction(ManagedPagedMMF mmf, EpochManager epochManager, SchemaDiff diff, FieldR1[] persistedFields,
        ComponentR1 persistedComp, DBComponentDefinition newDefinition, MigrationChain chain, ILogger log,
        Action<MigrationProgressEventArgs> progressCallback = null)
    {
        var sw = Stopwatch.StartNew();

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.Analyzing, PercentComplete = 0, Elapsed = sw.Elapsed,
        });

        var oldOverhead = persistedComp.CompOverhead;
        var newOverhead = newDefinition.ComponentStorageOverhead;
        var oldCompSize = persistedComp.CompSize;
        var newCompSize = newDefinition.ComponentStorageSize;
        var oldStride = oldCompSize + oldOverhead;
        var newStride = newDefinition.ComponentStorageTotalSize;

        log?.LogInformation(
            "Schema migration (user function) for '{Name}': old stride={OldStride}, new stride={NewStride}, {StepCount} migration step(s)",
            diff.ComponentName, oldStride, newStride, chain.StepCount);

        var changeSet = mmf.CreateChangeSet();

        using var guard = EpochGuard.Enter(epochManager);

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.AllocatingSegments, PercentComplete = 5, Elapsed = sw.Elapsed,
        });

        // Load old component segment with OLD stride
        var oldCompSeg = mmf.LoadChunkBasedSegment(persistedComp.ComponentSPI, oldStride);

        // Allocate new component segment with NEW stride
        var initialPages = Math.Max(4, oldCompSeg.Length);
        var newCompSeg = mmf.AllocateChunkBasedSegment(PageBlockType.None, initialPages, newStride, changeSet);

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.MigratingEntities, TotalEntities = oldCompSeg.AllocatedChunkCount,
            PercentComplete = 10, Elapsed = sw.Elapsed,
        });

        // Migrate entity data using user-provided migration function(s)
        var (entitiesMigrated, failures) = MigrateEntitiesWithFunction(
            oldCompSeg, newCompSeg, chain, oldOverhead, newOverhead, oldCompSize, newCompSize, changeSet);

        // If any entities failed, throw before updating SPIs — old segments remain untouched
        if (failures != null && failures.Count > 0)
        {
            // Clean up allocated segments since migration failed
            mmf.DeleteSegment(newCompSeg);
            throw new SchemaMigrationException(diff.ComponentName, failures);
        }

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.RecreatingRevisionChain,
            EntitiesMigrated = entitiesMigrated, TotalEntities = entitiesMigrated, PercentComplete = 70, Elapsed = sw.Elapsed,
        });

        // Load old revision segment and allocate new one
        var oldRevSeg = mmf.LoadChunkBasedSegment(persistedComp.VersionSPI, ComponentRevisionManager.CompRevChunkSize);
        var revInitialPages = Math.Max(4, oldRevSeg.Length);
        var newRevSeg = mmf.AllocateChunkBasedSegment(PageBlockType.None, revInitialPages, ComponentRevisionManager.CompRevChunkSize, changeSet);

        // Migrate revision chains (HEAD only) — reuse existing logic
        MigrateRevisionChain(oldRevSeg, newRevSeg, oldCompSeg, changeSet);

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.Flushing,
            EntitiesMigrated = entitiesMigrated, TotalEntities = entitiesMigrated, PercentComplete = 90, Elapsed = sw.Elapsed,
        });

        // Flush all changes to disk before updating SPIs
        changeSet.SaveChanges();
        mmf.FlushToDisk();

        // Delete old segments (best-effort cleanup)
        mmf.DeleteSegment(oldCompSeg);
        mmf.DeleteSegment(oldRevSeg);

        sw.Stop();

        log?.LogInformation("Schema migration (user function) for '{Name}' complete: {Count} entities migrated in {ElapsedMs}ms",
            diff.ComponentName, entitiesMigrated, sw.ElapsedMilliseconds);

        progressCallback?.Invoke(new MigrationProgressEventArgs
        {
            ComponentName = diff.ComponentName, Phase = MigrationPhase.Complete,
            EntitiesMigrated = entitiesMigrated, TotalEntities = entitiesMigrated, PercentComplete = 100, Elapsed = sw.Elapsed,
        });

        return new MigrationResult
        {
            NewComponentSPI = newCompSeg.RootPageIndex,
            NewVersionSPI = newRevSeg.RootPageIndex,
            NewComponentSegment = newCompSeg,
            NewRevisionSegment = newRevSeg,
            EntitiesMigrated = entitiesMigrated,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Migrates all occupied entities using the migration chain (single-step or multi-step with double-buffer).
    /// Handles per-entity exceptions gracefully: logs the failure and continues with remaining entities.
    /// </summary>
    internal static unsafe (int EntitiesMigrated, List<MigrationFailure> Failures) MigrateEntitiesWithFunction(
        ChunkBasedSegment<PersistentStore> oldSeg, ChunkBasedSegment<PersistentStore> newSeg, MigrationChain chain,
        int oldOverhead, int newOverhead, int oldCompSize, int newCompSize, ChangeSet changeSet)
    {
        var capacity = oldSeg.ChunkCapacity;
        var entitiesMigrated = 0;
        List<MigrationFailure> failures = null;

        newSeg.EnsureCapacity(capacity, changeSet);

        var oldAccessor = oldSeg.CreateChunkAccessor();
        var newAccessor = newSeg.CreateChunkAccessor(changeSet);

        // Determine buffer strategy: stackalloc for small buffers, ArrayPool for large ones
        var maxBufSize = chain.MaxIntermediateSize;
        var usePool = maxBufSize > 1024;
        byte[] pooledBufA = null;
        byte[] pooledBufB = null;

        if (usePool)
        {
            pooledBufA = ArrayPool<byte>.Shared.Rent(maxBufSize);
            pooledBufB = ArrayPool<byte>.Shared.Rent(maxBufSize);
        }

        try
        {
            for (int chunkId = 1; chunkId < capacity; chunkId++)
            {
                if (!oldSeg.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                var oldPtr = oldAccessor.GetChunkAddress(chunkId);

                try
                {
                    // Reserve the same ChunkId in the new segment to preserve index references
                    newSeg.ReserveChunk(chunkId);

                    var newPtr = newAccessor.GetChunkAddress(chunkId, true);

                    // Zero-fill the new chunk so added fields get default values (not stale memory)
                    new Span<byte>(newPtr, newSeg.Stride).Clear();

                    // Copy overhead section (AllowMultiple element IDs) — separate from user migration
                    if (oldOverhead > 0 && newOverhead > 0)
                    {
                        var overheadCopySize = Math.Min(oldOverhead, newOverhead);
                        Buffer.MemoryCopy(oldPtr, newPtr, overheadCopySize, overheadCopySize);
                    }

                    // Execute migration chain on component data (after overhead)
                    var oldCompData = new ReadOnlySpan<byte>(oldPtr + oldOverhead, oldCompSize);

                    if (usePool)
                    {
                        ExecuteChainPooled(chain, oldCompData, newPtr + newOverhead, newCompSize, pooledBufA, pooledBufB);
                    }
                    else
                    {
                        ExecuteChainStackalloc(chain, oldCompData, newPtr + newOverhead, newCompSize, maxBufSize);
                    }

                    entitiesMigrated++;
                }
                catch (Exception ex)
                {
                    failures ??= new List<MigrationFailure>();
                    var hexDump = FormatHexDump(oldPtr + oldOverhead, oldCompSize);
                    failures.Add(new MigrationFailure
                    {
                        ChunkId = chunkId,
                        OldDataHex = hexDump,
                        Exception = ex,
                    });
                }
            }
        }
        finally
        {
            newAccessor.Dispose();
            oldAccessor.Dispose();

            if (pooledBufA != null)
            {
                ArrayPool<byte>.Shared.Return(pooledBufA);
            }

            if (pooledBufB != null)
            {
                ArrayPool<byte>.Shared.Return(pooledBufB);
            }
        }

        return (entitiesMigrated, failures);
    }

    /// <summary>
    /// Executes a migration chain using stackalloc double-buffers (for small components ≤1024 bytes).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // NoInlining to contain the stackalloc frame
    private static unsafe void ExecuteChainStackalloc(MigrationChain chain, ReadOnlySpan<byte> oldData, byte* destPtr, int destSize, int maxBufSize)
    {
        if (chain.StepCount == 1)
        {
            // Single-step: transform directly into destination
            var destSpan = new Span<byte>(destPtr, destSize);
            chain.Steps[0].Execute(oldData, destSpan);
            return;
        }

        // Multi-step: ping-pong between two buffers.
        // Step 0: read from oldData → write to bufA
        // Step 1: read from bufA → write to bufB
        // Step N (last): read from current buffer → write to destination
        Span<byte> bufA = stackalloc byte[maxBufSize];
        Span<byte> bufB = stackalloc byte[maxBufSize];

        var steps = chain.Steps;

        // First intermediate step: old data → bufA
        bufA.Slice(0, steps[0].NewSize).Clear();
        steps[0].Execute(oldData, bufA.Slice(0, steps[0].NewSize));

        // Remaining steps: ping-pong between bufA and bufB
        for (int i = 1; i < steps.Length; i++)
        {
            var step = steps[i];
            var src = (i % 2 != 0) ? bufA : bufB;
            var isLast = (i == steps.Length - 1);

            if (isLast)
            {
                var destSpan = new Span<byte>(destPtr, destSize);
                step.Execute(src.Slice(0, steps[i - 1].NewSize), destSpan);
            }
            else
            {
                var dst = (i % 2 != 0) ? bufB : bufA;
                dst.Slice(0, step.NewSize).Clear();
                step.Execute(src.Slice(0, steps[i - 1].NewSize), dst.Slice(0, step.NewSize));
            }
        }
    }

    /// <summary>
    /// Executes a migration chain using ArrayPool-rented buffers (for large components &gt;1024 bytes).
    /// </summary>
    private static unsafe void ExecuteChainPooled(MigrationChain chain, ReadOnlySpan<byte> oldData, byte* destPtr, int destSize, byte[] bufA, byte[] bufB)
    {
        if (chain.StepCount == 1)
        {
            var destSpan = new Span<byte>(destPtr, destSize);
            chain.Steps[0].Execute(oldData, destSpan);
            return;
        }

        var steps = chain.Steps;
        ReadOnlySpan<byte> currentSrc = oldData;

        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var isLast = (i == steps.Length - 1);

            if (isLast)
            {
                var destSpan = new Span<byte>(destPtr, destSize);
                step.Execute(currentSrc, destSpan);
            }
            else
            {
                var dst = (i % 2 == 0) ? bufA.AsSpan() : bufB.AsSpan();
                dst.Slice(0, step.NewSize).Clear();
                step.Execute(currentSrc, dst.Slice(0, step.NewSize));
                currentSrc = dst.Slice(0, step.NewSize);
            }
        }
    }

    /// <summary>
    /// Formats a hex dump of raw bytes for diagnostic output in migration failure logs.
    /// </summary>
    private static unsafe string FormatHexDump(byte* ptr, int size)
    {
        var displaySize = Math.Min(size, 64); // Cap at 64 bytes for readability
        var chars = new char[displaySize * 2];
        for (int i = 0; i < displaySize; i++)
        {
            var b = ptr[i];
            chars[i * 2] = GetHexChar(b >> 4);
            chars[i * 2 + 1] = GetHexChar(b & 0xF);
        }

        var hex = new string(chars);
        return displaySize < size ? hex + "..." : hex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char GetHexChar(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);

    /// <summary>
    /// Determines whether a migration is needed based on the schema diff.
    /// </summary>
    internal static bool NeedsMigration(SchemaDiff diff, int oldStride, int newStride)
    {
        if (diff.IsIdentical)
        {
            return false;
        }

        if (oldStride != newStride)
        {
            return true;
        }

        foreach (var fc in diff.FieldChanges)
        {
            if (fc.Kind == FieldChangeKind.OffsetChanged || fc.Kind == FieldChangeKind.TypeWidened)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects FieldIds of newly added indexes from the diff.
    /// </summary>
    internal static HashSet<int> GetNewIndexFieldIds(SchemaDiff diff)
    {
        HashSet<int> result = null;
        foreach (var ic in diff.IndexChanges)
        {
            if (ic.Kind == FieldChangeKind.IndexAdded)
            {
                result ??= new HashSet<int>();
                result.Add(ic.FieldId);
            }
        }
        return result;
    }
}
