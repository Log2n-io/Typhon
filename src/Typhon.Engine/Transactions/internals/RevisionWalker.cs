// unset

using System;
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
    /// <param name="transactionTSN">The reader's snapshot TSN — entries with TSN &gt; this are invisible.</param>
    /// <param name="skipTimeout">Skip Stopwatch.GetTimestamp overhead for uncontended read paths (PTA).</param>
    /// <returns>
    /// <see cref="RevisionReadStatus.Success"/> with revision metadata on success;
    /// <see cref="RevisionReadStatus.SnapshotInvisible"/> if no committed entry is visible;
    /// <see cref="RevisionReadStatus.Deleted"/> if the latest visible entry is a tombstone (ComponentChunkId == 0).
    /// </returns>
    internal static Result<ComponentInfo.CompRevInfo, RevisionReadStatus> WalkChain(ref ChunkAccessor<PersistentStore> compRevTableAccessor, int compRevFirstChunkId,
        long transactionTSN, bool skipTimeout = false)
    {
        // ── Fast path: single-entry chain (common case for steady-state entities) ──
        // Avoids RevisionEnumerator construction, lock acquisition, WaitContext/Deadline creation.
        // Safe when skipTimeout=true (PTA path, no concurrent writers).
        if (skipTimeout)
        {
            ref var header = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId);
            if (header.ItemCount == 1)
            {
                // Single entry — read it directly from the root chunk
                var chunkContent = compRevTableAccessor.GetChunkAsSpan(compRevFirstChunkId);
                var elements = chunkContent.Slice(Unsafe.SizeOf<CompRevStorageHeader>()).Cast<byte, CompRevStorageElement>();
                ref var element = ref elements[header.FirstItemIndex];

                if (!element.IsVoid)
                {
                    bool isCommitted = (element.TSN > 0) && !element.IsolationFlag;
                    if (isCommitted && element.TSN <= transactionTSN)
                    {
                        var compRevInfo = new ComponentInfo.CompRevInfo
                        {
                            Operations = ComponentInfo.OperationType.Undefined,
                            CompRevTableFirstChunkId = compRevFirstChunkId,
                            CurCompContentChunkId = element.ComponentChunkId,
                            CurRevisionIndex = header.FirstItemIndex,
                            PrevCompContentChunkId = 0,
                            PrevRevisionIndex = -1,
                            ReadCommitSequence = header.CommitSequence,
                            ReadRevisionIndex = header.FirstItemIndex
                        };

                        return element.ComponentChunkId == 0
                            ? new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(compRevInfo, RevisionReadStatus.Deleted)
                            : new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(compRevInfo);
                    }
                }

                // Single entry but not visible — fall through to full walk (shouldn't happen for committed entities with valid TSN)
            }
        }

        // PTA / no-caller-context path: infinite deadline when skipping the timeout, else a fresh per-call timeout.
        var lockWc = skipTimeout
            ? new WaitContext(Deadline.Infinite, default)
            : WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);
        return WalkChainCore(ref compRevTableAccessor, compRevFirstChunkId, transactionTSN, lockWc);
    }

    /// <summary>
    /// Overload for the transactional resolve path: the caller supplies the chain-lock <see cref="WaitContext"/> (composed
    /// ONCE per transaction — see <c>Transaction.ChainLockWaitContext</c>), so <c>Stopwatch.GetTimestamp()</c> is not
    /// re-armed for every Versioned slot of every entity opened. Tries the optimistic single-entry fast path first (the
    /// steady-state shape: cleanup trims every chain to its head after commit); any concurrent locked mutation is detected
    /// by the exclusive-holder probe + content re-validation and falls back to the locked walk.
    /// </summary>
    internal static Result<ComponentInfo.CompRevInfo, RevisionReadStatus> WalkChain(ref ChunkAccessor<PersistentStore> compRevTableAccessor, int compRevFirstChunkId,
        long transactionTSN, WaitContext lockWc)
    {
        if (TryWalkSingleEntryOptimistic(ref compRevTableAccessor, compRevFirstChunkId, transactionTSN, out var fastResult))
        {
            return fastResult;
        }

        return WalkChainCore(ref compRevTableAccessor, compRevFirstChunkId, transactionTSN, lockWc);
    }

    /// <summary>
    /// Optimistic (seqlock-style) resolve of a single-entry chain — the steady-state shape, since post-commit cleanup trims every chain to its head.
    /// Reads ONLY the root chunk (stable while the entity is alive and the caller's epoch scope is held — no reclaimable overflow chunk is ever touched,
    /// which is what makes this safe where a full optimistic chain walk is not), then validates:
    /// <list type="bullet">
    /// <item>No exclusive holder before AND after the data reads — every chain mutator that can produce a torn read (AddCompRev, cleanup compaction,
    /// conflict-path prepare/publish) runs under the exclusive chain lock.</item>
    /// <item>Header quad (FirstItemIndex/ItemCount/ChainLength/LCRI), CommitSequence and the 12 element bytes re-read unchanged — catches a locked
    /// mutator session that ran entirely between the two probes, and the lock-FREE publish pass (AP-03: TSN re-stamp + IsolationFlag flip + CS/LCRI
    /// bump). If every compared byte is unchanged, the value read IS the current consistent state, so returning it is correct even if transient
    /// states existed in between.</item>
    /// </list>
    /// The lock-free publish can never mutate an entry this path ACCEPTS: publish targets its transaction's isolated entry, and an isolated (or torn
    /// mid-flip) element is rejected here and falls back to the locked walk.
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

        // Header quad at offset 8 (8-byte aligned): FirstItemIndex | ItemCount | ChainLength | LastCommitRevisionIndex — one atomic load.
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
            ReadRevisionIndex = firstItemIndex
        };

        result = w0 == 0
            ? new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(compRevInfo, RevisionReadStatus.Deleted)
            : new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(compRevInfo);
        return true;
    }

    private static Result<ComponentInfo.CompRevInfo, RevisionReadStatus> WalkChainCore(ref ChunkAccessor<PersistentStore> compRevTableAccessor, int compRevFirstChunkId,
        long transactionTSN, WaitContext lockWc)
    {
        // ── Full walk: handles multi-entry chains, voided entries, non-monotonic TSN ordering ──
        short prevCompRevisionIndex = -1;
        short curCompRevisionIndex = -1;
        int prevCompChunkId = 0;
        int curCompChunkId = 0;

        // CommitSequence and committed-entry count must be captured INSIDE the shared lock (held by RevisionEnumerator) so that the chain walk
        // observes a consistent chain state. Capturing outside the lock creates a race: cleanup or another commit can modify the chain between
        // capture and the lock acquisition, leaving values consistent with a state the chain walk never sees.
        int readCommitSequence;


        {
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, compRevFirstChunkId, false, true, lockWc);
            readCommitSequence = compRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId).CommitSequence;
            int totalCommitted = 0;
            int visibleOrdinal = 0;

            while (enumerator.MoveNext())
            {
                ref var element = ref enumerator.Current;

                // Skip voided entries (rolled-back revisions cleared by cleanup or explicit void)
                if (element.IsVoid)
                {
                    continue;
                }

                // Count ALL committed entries (visible and invisible) to compute the snapshot-isolated revision number.
                // Do NOT break on TSN > reader.TSN — entries in the chain are NOT guaranteed to be in monotonically increasing TSN order.
                bool isCommitted = (element.TSN > 0) && !element.IsolationFlag;
                if (isCommitted)
                {
                    totalCommitted++;
                }

                if (element.TSN > transactionTSN)
                {
                    continue;
                }

                // Update the current revision (and the previous) if a valid entry (tick == 0 means a rollbacked entry) and it's not an isolated one
                if (isCommitted)
                {
                    prevCompRevisionIndex = curCompRevisionIndex;
                    prevCompChunkId = curCompChunkId;
                    curCompRevisionIndex = (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
                    curCompChunkId = element.ComponentChunkId;
                    visibleOrdinal = totalCommitted;
                }

            }

            // Compute snapshot-isolated revision number: CS tracks total commits, totalCommitted tracks how many committed entries remain in the
            // chain (cleanup may have removed some). visibleOrdinal is the 1-based position of the visible entry among committed entries.
            readCommitSequence = readCommitSequence - totalCommitted + visibleOrdinal;
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
                ReadRevisionIndex = curCompRevisionIndex
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
