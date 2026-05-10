// unset

using System;
using System.Runtime.CompilerServices;

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
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, compRevFirstChunkId, false, true, skipTimeout);
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
