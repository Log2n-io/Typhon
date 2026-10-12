// unset

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Shared revision chain walk logic used by <see cref="Transaction"/> to resolve visible revisions.
/// Extracted from the duplicated inner loops of the two <c>GetCompRevInfoFromIndex</c> overloads.
/// </summary>
internal static class RevisionChainReader
{
    /// <summary>
    /// Walks a revision chain and returns the <see cref="ComponentInfo.CompRevInfo"/> for the latest visible revision at the given <paramref name="transactionTSN"/>.
    /// </summary>
    /// <param name="compRevTableAccessor">Accessor for reading revision table chunks.</param>
    /// <param name="compRevFirstChunkId">First chunk ID of the entity's revision chain.</param>
    /// <param name="transactionTSN">The reader's snapshot TSN — entries with a commit stamp &gt; this are invisible.</param>
    /// <param name="cards">The engine's commit cards, through which a flagged entry is resolved (VIS-01); <see langword="null"/> only for a walk with no
    /// committer alive (an open's rebuild), which then takes flagged entries for uncommitted.</param>
    /// <param name="skipTimeout">Skip Stopwatch.GetTimestamp overhead for uncontended read paths (PTA).</param>
    /// <returns>
    /// <see cref="RevisionReadStatus.Success"/> with revision metadata on success;
    /// <see cref="RevisionReadStatus.SnapshotInvisible"/> if no committed entry is visible;
    /// <see cref="RevisionReadStatus.Deleted"/> if the latest visible entry is a tombstone (ComponentChunkId == 0).
    /// </returns>
    internal static Result<ComponentInfo.CompRevInfo, RevisionReadStatus> WalkChain(ref ChunkAccessor<PersistentStore> compRevTableAccessor, int compRevFirstChunkId,
        long transactionTSN, CommitCardTable cards, bool skipTimeout = false)
    {
        // ── Fast path: single-entry chain (common case for steady-state entities) ──
        // No lock, no WaitContext, no clock — and validated: the seqlock read (TryWalkSingleEntryOptimistic). The plain read this used to be trusted "no
        // concurrent writers", which ordinary transaction reads (EntityRef) do not guarantee: a compaction rewrites the root with a plain copy, and a read
        // racing it could take the new item count with the old element, a revision the compaction had dropped.
        if (skipTimeout && TryWalkSingleEntryOptimistic(ref compRevTableAccessor, compRevFirstChunkId, transactionTSN, out var fastResult))
        {
            return fastResult;
        }

        // PTA / no-caller-context path: infinite deadline when skipping the timeout, else a fresh per-call timeout.
        var lockWc = skipTimeout
            ? new WaitContext(Deadline.Infinite, default)
            : WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);
        return WalkChainCore(ref compRevTableAccessor, compRevFirstChunkId, transactionTSN, cards, lockWc);
    }

    /// <summary>
    /// Overload for the transactional resolve path. Tries the optimistic single-entry fast path first (the steady-state shape: cleanup trims every
    /// chain to its head after commit), which takes no lock and reads no clock; any concurrent locked mutation is detected by the exclusive-holder
    /// probe + content re-validation and falls back to the locked walk. That walk reads no clock either unless its lock is contended: only then does
    /// <paramref name="tx"/> compose the wait's budget — for that acquisition, never once per transaction (CX-06).
    /// </summary>
    internal static Result<ComponentInfo.CompRevInfo, RevisionReadStatus> WalkChain(ref ChunkAccessor<PersistentStore> compRevTableAccessor, int compRevFirstChunkId,
        long transactionTSN, Transaction tx)
    {
        Debug.Assert(tx != null, "the transactional walk composes its wait budget from the transaction (CX-06)");
        if (TryWalkSingleEntryOptimistic(ref compRevTableAccessor, compRevFirstChunkId, transactionTSN, out var fastResult))
        {
            return fastResult;
        }

        return WalkChainCore(ref compRevTableAccessor, compRevFirstChunkId, transactionTSN, tx.CommitCards, default, tx);
    }

    /// <summary>
    /// Optimistic (seqlock-style) resolve of a single-entry chain — the steady-state shape, since post-commit cleanup trims every chain to its head.
    /// Reads ONLY the root chunk (stable while the entity is alive and the caller's epoch scope is held — no reclaimable overflow chunk is ever touched,
    /// which is what makes this safe where a full optimistic chain walk is not), then validates:
    /// <list type="bullet">
    /// <item>No exclusive holder before AND after the data reads — every chain mutator that can produce a torn read (AddCompRev, cleanup compaction,
    /// prepare and publish — the publish takes the lock itself since #1158, AP-05) runs under the exclusive chain lock.</item>
    /// <item>Header quad (FirstItemIndex/ItemCount/ChainLength/Reserved), CommitSequence and the 12 element bytes re-read unchanged — catches a locked
    /// mutator session that ran entirely between the two probes, a publish among them (stamp + IsolationFlag flip + CS bump). If every
    /// compared byte is unchanged, the value read IS the current consistent state, so returning it is correct even if transient states existed in
    /// between.</item>
    /// </list>
    /// A publish can never mutate an entry this path ACCEPTS: publish targets its transaction's isolated entry, and an isolated (or torn mid-flip)
    /// element is rejected here and falls back to the locked walk.
    /// <para>Memory ordering: EVERY load in this method is a <see cref="Volatile"/>.Read — volatile loads are program-ordered among themselves, which is
    /// exactly the seqlock requirement (probe → data → re-probe must execute in order). That makes the path correct on arm64 (each load is an ldar);
    /// on x64 they compile to plain movs (TSO). Do not "optimize" any of them to plain loads: a single plain data read could sink below the
    /// re-validation on arm64 and void it. The mutator side is ordered by <see cref="Internals.AccessControlSmall"/>'s Interlocked enter/exit (full
    /// fences on both architectures). Single-shot: any validation failure falls back to the locked walk, no retry loop.</para>
    /// </summary>
    private static bool TryWalkSingleEntryOptimistic(ref ChunkAccessor<PersistentStore> compRevTableAccessor, int compRevFirstChunkId, long transactionTSN,
        out Result<ComponentInfo.CompRevInfo, RevisionReadStatus> result)
    {
        result = default;

        // Preserve chain-walk telemetry fidelity: when MVCC chain-walk tracing is on, always take the emitting slow path.
        if (TelemetryConfig.DataMvccChainWalkActive)
        {
            return false;
        }

        ref var header = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId);
        if (header.Control.IsExclusivelyHeld)
        {
            return false;
        }

        // Header quad at offset 8 (8-byte aligned): FirstItemIndex | ItemCount | ChainLength | Reserved — one atomic load.
        ref long quadRef = ref Unsafe.As<short, long>(ref header.FirstItemIndex);
        long quad1 = Volatile.Read(ref quadRef);
        short firstItemIndex = (short)quad1;
        short itemCount = (short)(quad1 >> 16);
        if (itemCount != 1 || firstItemIndex < 0 || firstItemIndex >= ComponentRevisionManager.CompRevCountInRoot)
        {
            return false;
        }

        int commitSequence1 = Volatile.Read(ref header.CommitSequence);

        // Element at root offset 28 + index*12 (4-byte aligned) — read as three ordered atomic int loads.
        var chunkContent = compRevTableAccessor.GetChunkAsSpan(compRevFirstChunkId);
        ref var element = ref chunkContent.Slice(Unsafe.SizeOf<CompRevStorageHeader>()).Cast<byte, CompRevStorageElement>()[firstItemIndex];
        ref int elementWords = ref Unsafe.As<CompRevStorageElement, int>(ref element);
        int w0 = Volatile.Read(ref elementWords);                         // ComponentChunkId
        int w1 = Volatile.Read(ref Unsafe.Add(ref elementWords, 1));      // _packedTickHigh
        int w2 = Volatile.Read(ref Unsafe.Add(ref elementWords, 2));      // _packedTickLow | _packedUowId << 16

        long elementTsn = ((long)(uint)w1 << 16) | (ushort)w2;
        bool isolation = (((uint)w2 >> 16) & 0x8000) != 0;
        bool isVoid = w0 == 0 && w1 == 0 && w2 == 0;
        if (isVoid || isolation || elementTsn == 0 || elementTsn > transactionTSN)
        {
            return false;
        }

        // Re-validate: element bytes, CommitSequence, header quad, then the exclusive probe last.
        if (Volatile.Read(ref elementWords) != w0
            || Volatile.Read(ref Unsafe.Add(ref elementWords, 1)) != w1
            || Volatile.Read(ref Unsafe.Add(ref elementWords, 2)) != w2
            || Volatile.Read(ref header.CommitSequence) != commitSequence1
            || Volatile.Read(ref quadRef) != quad1
            || header.Control.IsExclusivelyHeld)
        {
            return false;
        }


        var compRevInfo = new ComponentInfo.CompRevInfo
        {
            Operations = ComponentInfo.OperationType.Undefined,
            CompRevTableFirstChunkId = compRevFirstChunkId,
            CurCompContentChunkId = w0,
            CurRevisionIndex = firstItemIndex,
            PrevCompContentChunkId = 0,
            PrevRevisionIndex = -1,
            ReadCommitSequence = commitSequence1,
        };

        result = w0 == 0
            ? new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(compRevInfo, RevisionReadStatus.Deleted)
            : new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(compRevInfo);
        return true;
    }

    /// <summary>
    /// The full walk, under the chain's shared lock. The visible revision is the one with the highest commit stamp at or below the snapshot (VIS-05) —
    /// not the last visible in chain order: two writers of one entity publish in either order. A flagged entry is resolved through its owner's commit card
    /// (VIS-01): an owner registered with a stamp is committing, and its stamp decides; one that is not is an uncommitted write, invisible. No revalidation:
    /// a publish takes this lock exclusively, so the owner cannot stamp the entry and unregister while it is held. The reader's own pending entries are
    /// skipped — its writes resolve through its own cache.
    /// </summary>
    private static Result<ComponentInfo.CompRevInfo, RevisionReadStatus> WalkChainCore(ref ChunkAccessor<PersistentStore> compRevTableAccessor, int compRevFirstChunkId,
        long transactionTSN, CommitCardTable cards, WaitContext lockWc, Transaction budgetOwner = null)
    {
        short prevCompRevisionIndex = -1;
        short curCompRevisionIndex = -1;
        int prevCompChunkId = 0;
        int curCompChunkId = 0;
        long bestStamp = 0;
        int readCommitSequence;

        {
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, compRevFirstChunkId, false, true, lockWc, budgetOwner);
            readCommitSequence = enumerator.Header.CommitSequence;   // under the shared lock, consistent with the walk
            while (enumerator.MoveNext())
            {
                ref var element = ref enumerator.Current;
                if (!ComponentRevisionManager.TryResolveStamp(element, cards, transactionTSN, out var stamp) || stamp > transactionTSN || stamp <= bestStamp)
                {
                    continue;
                }

                prevCompRevisionIndex = curCompRevisionIndex;
                prevCompChunkId = curCompChunkId;
                curCompRevisionIndex = (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
                curCompChunkId = element.ComponentChunkId;
                bestStamp = stamp;
            }
        }

        // Phase 6: chain length is approximated from the first chunk's ChainLength header. Cap at byte max for the wire payload.
        // The leaf-gate read here lets the JIT fold the entire ChainWalk emit path away when MVCC tracing is disabled — the
        // GetChunk read itself is not free on this hot path (called per-entity-read).
        byte chainLenForEvent = 0;
        if (TelemetryConfig.DataMvccChainWalkActive)
        {
            chainLenForEvent = (byte)Math.Min(compRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId).ChainLength, byte.MaxValue);
        }


        if (curCompRevisionIndex == -1)
        {
            TyphonEvent.EmitDataMvccChainWalk(transactionTSN, chainLenForEvent, 1);
            return new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(RevisionReadStatus.SnapshotInvisible);
        }

        {
            var compRevInfo = new ComponentInfo.CompRevInfo
            {
                Operations = ComponentInfo.OperationType.Undefined,
                CompRevTableFirstChunkId = compRevFirstChunkId,
                CurCompContentChunkId = curCompChunkId,
                CurRevisionIndex = curCompRevisionIndex,
                PrevCompContentChunkId = prevCompChunkId,
                PrevRevisionIndex = prevCompRevisionIndex,
                ReadCommitSequence = readCommitSequence,
            };

            // Tombstoned entity: carry the value (callers like UpdateComponent need revision metadata) but signal Deleted
            if (curCompChunkId == 0)
            {
                TyphonEvent.EmitDataMvccChainWalk(transactionTSN, chainLenForEvent, 2);
                return new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(compRevInfo, RevisionReadStatus.Deleted);
            }

            TyphonEvent.EmitDataMvccChainWalk(transactionTSN, chainLenForEvent, 0);
            return new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(compRevInfo);
        }
    }
}
