// unset

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Action callback for <see cref="ComponentInfo.ForEachMutableEntry{TAction}"/>.
/// Implemented as a struct for zero-overhead JIT specialization (same mechanism as <see cref="Span{T}"/>'s <c>Sort</c>).
/// </summary>
internal interface IEntryAction
{
    void Process(ref CommitContext context);
}

/// <summary>
/// Per-transaction working set for one component type: caches the component-revision info (<see cref="CompRevInfo"/>) per entity primary key, keyed in
/// <see cref="SingleCache"/>. (Component-level multi-instance — the former "AllowMultiple" dual-cache — was removed; every entity has at most one instance of a
/// given component type.)
/// </summary>
internal sealed class ComponentInfo
{
    [Flags]
    public enum OperationType
    {
        Undefined = 0,
        Created   = 1,
        Read      = 2,
        Updated   = 4,
        Deleted   = 8
    }

    public struct CompRevInfo
    {
        // Current operation type on the component for this transaction
        public OperationType Operations;

        /// ChunkId of the first CompRevTable chunk for the component (the entry point of the chain with the CompRevStorageHeader being used)
        public int CompRevTableFirstChunkId;

        /// The index in the revision table of the revision BEFORE being changed in this transaction. -1 if there's none.
        public short PrevRevisionIndex;

        /// The index in the revision table of the revision being used in this transaction.
        /// This is NOT relative to <see cref="CompRevStorageHeader.FirstItemIndex"/> but to the start of the chain (first element of the first chunk).
        public short CurRevisionIndex;

        /// The ChunkId storing the component content revision BEFORE the transaction (the previous one). 0 if there's none.
        public int PrevCompContentChunkId;

        /// The ChunkId storing the component content corresponding to the revision of this CompRevInfo instance
        public int CurCompContentChunkId;

        /// Monotonic commit counter captured at read time. Compared against header.CommitSequence during commit to detect any commits since our
        /// read — immune to revision index ordering and cleanup compaction that can fool TSN/LCRI-based detection.
        public int ReadCommitSequence;

        /// CurRevisionIndex captured at read/create time. Used by GetComponentRevision to compute position-based offset:
        /// revision = ReadCommitSequence + (CurRevisionIndex - ReadRevisionIndex).
        public short ReadRevisionIndex;
    }

    public int EntryCount => SingleCache.Count;

    // Common fields
    public int ComponentTypeId;
    public ComponentTable ComponentTable;
    /// <summary>Cached from <see cref="ComponentTable.ComponentOverhead"/> — avoids property-through-property indirection on hot path.</summary>
    public int ComponentOverhead;
    public ChunkBasedSegment<PersistentStore> CompContentSegment;
    public ChunkBasedSegment<PersistentStore> CompRevTableSegment;
    public ChunkAccessor<PersistentStore> CompContentAccessor;
    public ChunkAccessor<PersistentStore> CompRevTableAccessor;
    public ChunkAccessor<TransientStore> TransientCompContentAccessor;

    /// <summary>Per-entity (primary key → component-revision info) working set for this transaction.</summary>
    internal Dictionary<long, CompRevInfo> SingleCache;

    /// <summary>One Commit-discipline staged write: where the value lives in the tx staging buffer, plus the HEAD location captured at write time.</summary>
    public struct StagedSlot
    {
        /// <summary>0-based offset into the transaction's native staging buffer (presence in <see cref="CommitStaged"/> means "staged").</summary>
        public int Offset;

        /// <summary>HEAD location captured at stage time so publish needs no EntityMap re-lookup: cluster ⇒ clusterChunkId*64+slotIndex; flat ⇒ content
        /// chunkId.</summary>
        public int Location;
    }

    /// <summary>
    /// Commit-discipline (SingleVersion) staging map for this transaction: entity primary key → <see cref="StagedSlot"/>. Lazily allocated on the first
    /// Commit-discipline write to this component. Kept separate from <see cref="SingleCache"/> so staging entries never collide with the Versioned
    /// revision-chain consumers (rollback iteration, cluster resolution). See <c>claude/design/Ecs/committed-storage-mode.md</c> (issue #392).
    /// </summary>
    internal Dictionary<long, StagedSlot> CommitStaged;

    /// <summary>
    /// Capacity above which a recycled entry's per-transaction dictionaries are replaced rather than cleared, so one bulk transaction does not leave a pooled
    /// accessor holding its buckets forever.
    /// </summary>
    internal const int RetainedCacheCapacity = 1024;

    /// <summary>
    /// Binds this entry to <paramref name="table"/> for a new transaction: every field set, chunk accessors created against <paramref name="changeSet"/>, and
    /// the per-transaction caches emptied.
    /// </summary>
    /// <param name="componentTypeId">The component's in-memory type id.</param>
    /// <param name="table">The component table.</param>
    /// <param name="changeSet">The accessor's change set; <see langword="null"/> for a read-only one.</param>
    /// <remarks>
    /// <para>
    /// <b>The one place an entry is initialised</b>, for a new entry and a recycled one alike, so the two can never disagree about a field. An accessor keeps
    /// its entries across a pooled reuse (<c>EntityAccessor.ResetCore</c>) and rebinds them here, instead of allocating an entry and a dictionary per
    /// component per transaction.
    /// </para>
    /// <para>
    /// The accessors are overwritten, never disposed: the owning transaction flushed them before it was reset, and a read-only one never flushes — which is
    /// exactly what dropping the entry did before recycling existed.
    /// </para>
    /// </remarks>
    internal void Bind(int componentTypeId, ComponentTable table, ChangeSet changeSet)
    {
        ComponentTypeId = componentTypeId;
        ComponentTable = table;
        ComponentOverhead = table.ComponentOverhead;
        CompContentSegment = null;
        CompRevTableSegment = null;
        CompContentAccessor = default;
        CompRevTableAccessor = default;
        TransientCompContentAccessor = default;

        switch (table.StorageMode)
        {
            case StorageMode.Transient:
                TransientCompContentAccessor = table.TransientComponentSegment.CreateChunkAccessor();
                break;
            case StorageMode.SingleVersion:
                CompContentSegment = table.ComponentSegment;
                CompContentAccessor = table.ComponentSegment.CreateChunkAccessor(changeSet);
                break;
            default: // Versioned
                CompContentSegment = table.ComponentSegment;
                CompRevTableSegment = table.CompRevTableSegment;
                CompContentAccessor = table.ComponentSegment.CreateChunkAccessor(changeSet);
                CompRevTableAccessor = table.CompRevTableSegment.CreateChunkAccessor(changeSet);
                break;
        }

        if (SingleCache == null || SingleCache.Capacity > RetainedCacheCapacity)
        {
            SingleCache = new Dictionary<long, CompRevInfo>();
        }
        else
        {
            SingleCache.Clear();
        }

        if (CommitStaged != null && CommitStaged.Capacity > RetainedCacheCapacity)
        {
            CommitStaged = null;
        }
        else
        {
            CommitStaged?.Clear();
        }
    }

    public void AddNew(long pk, CompRevInfo entry) => SingleCache.Add(pk, entry);

    /// <summary>
    /// Disposes the <see cref="ChunkAccessor{PersistentStore}"/> fields to flush dirty pages.
    /// </summary>
    public void DisposeAccessors()
    {
        if (ComponentTable.StorageMode == StorageMode.Transient)
        {
            TransientCompContentAccessor.Dispose();
        }
        else
        {
            CompContentAccessor.Dispose();
            if (ComponentTable.StorageMode == StorageMode.Versioned)
            {
                CompRevTableAccessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Zero-overhead polymorphic iteration via constrained-generic struct callback.
    /// JIT specializes per <typeparamref name="TAction"/>, inlining <see cref="IEntryAction.Process"/>.
    /// Sets <see cref="CommitContext.PrimaryKey"/> and <see cref="CommitContext.CompRevInfo"/> before calling action.Process.
    /// Skips entries where Operations == Read.
    /// </summary>
    public void ForEachMutableEntry<TAction>(ref CommitContext context, ref TAction action) where TAction : struct, IEntryAction
    {
        foreach (var key in SingleCache.Keys)
        {
            context.PrimaryKey = key;
            ref var cri = ref CollectionsMarshal.GetValueRefOrNullRef(SingleCache, key);
            if (cri.Operations == OperationType.Read)
            {
                continue;
            }
            context.CompRevInfo = ref cri;
            action.Process(ref context);
        }
    }
}
