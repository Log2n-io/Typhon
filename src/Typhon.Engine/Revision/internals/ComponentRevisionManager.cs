using JetBrains.Annotations;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

[PublicAPI]
internal ref struct ComponentRevisionManager
{
    internal const int CompRevChunkSize = 64;
    internal unsafe static readonly int CompRevCountInRoot = (CompRevChunkSize - sizeof(CompRevStorageHeader)) / sizeof(CompRevStorageElement);
    internal unsafe static readonly int CompRevCountInNext = (CompRevChunkSize / sizeof(CompRevStorageElement));

    internal ref struct ElementRevisionHandle
    {
        private ref ChunkAccessor<PersistentStore> _accessor;
        private readonly int _chunkId;
        private readonly bool _isFirst;
        private readonly short _elementIndex;

        public ElementRevisionHandle(ref ChunkAccessor<PersistentStore> accessor, int chunkId, bool isFirst, short elementIndex)
        {
            _accessor = ref accessor;
            _chunkId = chunkId;
            _isFirst = isFirst;
            _elementIndex = elementIndex;
        }

        // The resolved physical location of this revision element. Captured during PREPARE (where the locking walk is allowed) so the AP-01 PUBLISH pass
        // can reconstruct the handle without re-running the locking GetRevisionElement walk (AP-03: publish acquires no bounded-timeout lock).
        public int ChunkId => _chunkId;
        public bool IsFirst => _isFirst;
        public short ElementIndex => _elementIndex;

        public unsafe ref CompRevStorageElement Element
        {
            get
            {
                var headerSize = _isFirst ? sizeof(CompRevStorageHeader) : sizeof(int);
                return ref _accessor.GetChunkAsSpan(_chunkId, true).Slice(headerSize).Cast<byte, CompRevStorageElement>().Slice(_elementIndex, 1)[0];
            }
        }

        /// <summary>The publication act (VIS-04): the element stamped with the commit <paramref name="stamp"/>, its isolation flag cleared, in that order.</summary>
        public void Publish(long stamp) => Element.Publish(stamp);

    }

    /// <summary>
    /// The capture ticket for a hold of <paramref name="root"/>'s chain lock (PS-20, <see cref="PagedMMF.CaptureTicket"/>): taken before the lock is, and
    /// given to <see cref="EndChainLockHold"/> once it is released. <paramref name="root"/> is a chunk of the page cache.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static PagedMMF.CaptureTicket ChainLockTicket(ref ChunkAccessor<PersistentStore> accessor, ref CompRevStorageHeader root) =>
        accessor.Store.Mmf.TakeCaptureTicket(ref Unsafe.As<CompRevStorageHeader, byte>(ref root));

    /// <summary>Ends the chain-lock hold <paramref name="ticket"/> was taken for, after the lock is released (PS-20).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EndChainLockHold(ref ChunkAccessor<PersistentStore> accessor, in PagedMMF.CaptureTicket ticket) =>
        accessor.Store.Mmf.EndLockHold(in ticket);

    internal static ElementRevisionHandle GetRevisionElement(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId, short revisionIndex)
    {
        ref var firstHeader = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId);
        if (revisionIndex < CompRevCountInRoot)
        {
            return new ElementRevisionHandle(ref accessor, firstChunkId, true, revisionIndex);
        }

        var (chunkIndexInChain, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(revisionIndex);

        // Walk through the linked list until we find the chunk that is our starting point
        var nextChunkId = firstHeader.NextChunkId;

        var curChunkId = nextChunkId;
        var useLock = !firstHeader.Control.IsLockedByCurrentThread;
        var ticket = default(PagedMMF.CaptureTicket);
        if (useLock)
        {
            ticket = ChainLockTicket(ref accessor, ref firstHeader);
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);
            if (!firstHeader.Control.EnterSharedAccess(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("RevisionChain/GetElement", TimeoutOptions.Current.RevisionChainLockTimeout);
            }
        }
        // The walk fetches the overflow chunks, and a fetch can throw — a page-cache back-pressure timeout: the hold is released whatever happens, or the
        // next writer of the entity waits out its lock timeout (#1278).
        try
        {
            while (--chunkIndexInChain >= 0)
            {
                curChunkId = nextChunkId;
                nextChunkId = accessor.GetChunk<int>(nextChunkId);
            }
        }
        finally
        {
            if (useLock)
            {
                firstHeader.Control.ExitSharedAccess();
                EndChainLockHold(ref accessor, in ticket);
            }
        }

        return new ElementRevisionHandle(ref accessor, curChunkId, false, (short)indexInChunk);
    }

    // Chain-lock WaitContext for the internal cleanup/maintenance walks below. These are cold paths (deferred
    // compaction, rollback) — not a transaction's per-entity hot path — so a freshly-armed per-call deadline is fine.
    private static WaitContext CleanupLockWc => WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);

    internal static unsafe void AddCompRev(ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, long tsn, ushort uowId, bool isDelete,
        bool lockAlreadyHeld = false)
    {
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        var compContent = info.CompContentSegment;

        ref var firstHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevInfo.CompRevTableFirstChunkId, true);

        // Enter exclusive access for the Revision Table (skip if caller already holds the lock)
        var ticket = default(PagedMMF.CaptureTicket);
        if (!lockAlreadyHeld)
        {
            ticket = ChainLockTicket(ref compRevTableAccessor, ref firstHeader);
            // #1382: a first write waits for a contended chain briefly, then unwinds and moves its epoch instead of waiting with its pin; nothing is
            // written before the lock, so the write starts over clean (CHAIN-02).
            var restartable = PagedMMF.OpenRestartAllowed;
            var lockTimeout = TimeoutOptions.Current.RevisionChainLockTimeout;
            var wc = WaitContext.FromTimeout(restartable && PagedMMF.LockWaitBeforeRestart < lockTimeout ? PagedMMF.LockWaitBeforeRestart : lockTimeout);
            if (!firstHeader.Control.EnterExclusiveAccess(ref wc))
            {
                if (restartable)
                {
                    PagedMMF.RestartForLockWithin(TimeoutOptions.Current.RevisionChainLockTimeout);
                }

                ThrowHelper.ThrowLockTimeout("RevisionChain/AddRevision", TimeoutOptions.Current.RevisionChainLockTimeout);
            }
        }

        // Under the lock the append is not restartable (#1382): its steps write the chain one after the other, and a restart thrown between two of them
        // (a back-pressure round on a page) left it half-written for the retry. It waits as before, and only its timeout can interrupt it.
        var restartDepth = PagedMMF.RestartableOpenDepth;
        PagedMMF.RestartableOpenDepth = 0;
        try
        {
            // Check if we need to add one more chunk to the chain
            if (ComputeRevElementCount(firstHeader.ChainLength) == firstHeader.ItemCount)
            {
                GrowChain(info, compRevInfo.CompRevTableFirstChunkId, ref firstHeader);
            }

            // Add our new entry
            var newRevIndex = (short)(firstHeader.FirstItemIndex + firstHeader.ItemCount);
            var indexInChunk = GetRevisionLocation(ref compRevTableAccessor, compRevInfo.CompRevTableFirstChunkId, newRevIndex, out var curChunkId);

            Span<CompRevStorageElement> curChunkElements;

            // Still in the first chunk? The elements are right after the header
            if (compRevInfo.CompRevTableFirstChunkId == curChunkId)
            {
                curChunkElements = compRevTableAccessor.GetChunkAsSpan(compRevInfo.CompRevTableFirstChunkId, true)
                    .Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>();
            }

            // In another chunk, the subsequent ones have a one int header (the ID of the next chunk in the chain), then the elements
            else
            {
                curChunkElements = compRevTableAccessor.GetChunkAsSpan(curChunkId, true).Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
            }

            // Allocate a new component
            var componentChunkId = isDelete ? 0 : compContent.AllocateChunk(false, info.CompContentAccessor.ChangeSet);

            // Add our new entry
            curChunkElements[indexInChunk].TSN = tsn;
            curChunkElements[indexInChunk].IsolationFlag = true;
            curChunkElements[indexInChunk].UowId = uowId;
            curChunkElements[indexInChunk].ComponentChunkId = componentChunkId;

            // Update the compRevInfo
            compRevInfo.PrevCompContentChunkId = compRevInfo.CurCompContentChunkId;
            compRevInfo.PrevRevisionIndex = compRevInfo.CurRevisionIndex;
            compRevInfo.CurCompContentChunkId = componentChunkId;
            compRevInfo.CurRevisionIndex = newRevIndex;

            // One more item, update the header
            firstHeader.ItemCount++;
        }
        finally
        {
            PagedMMF.RestartableOpenDepth = restartDepth;
            if (!lockAlreadyHeld)
            {
                firstHeader.Control.ExitExclusiveAccess();
                EndChainLockHold(ref compRevTableAccessor, in ticket);
            }
        }
    }

    /// <summary>
    /// Turns this transaction's own, uncommitted revision — the one naming <paramref name="ownChunkId"/> — into its delete entry: content chunk 0, the
    /// mark of a tombstone. For a destroy that follows a write in the same transaction (REAP-02): the write's revision becomes the tombstone instead of a
    /// second one being added, and no committed element is left naming the chunk the destroy frees.
    /// </summary>
    /// <remarks>
    /// The element is found under the chain's exclusive lock — at <c>CurRevisionIndex</c> when it is still there, by its chunk id otherwise (a compaction
    /// may have moved it since the write). A commit that later finds another transaction committed in between relocates the tombstone as a tombstone.
    /// </remarks>
    /// <returns><c>false</c> when no uncommitted element names the chunk: nothing converted, and the caller must not free the chunk.</returns>
    internal static bool MakeOwnRevisionTombstone(ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, int ownChunkId)
    {
        ref var accessor = ref info.CompRevTableAccessor;
        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;
        ref var header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId, true);
        var ticket = ChainLockTicket(ref accessor, ref header);
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);
        if (!header.Control.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("RevisionChain/OwnTombstone", TimeoutOptions.Current.RevisionChainLockTimeout);
        }

        try
        {
            // Trusted only inside the chain and on our own, uncommitted element: a compaction may have shortened the chain and left stale bytes past it.
            header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId);
            var index = compRevInfo.CurRevisionIndex;
            var inChain = index >= header.FirstItemIndex && index < header.FirstItemIndex + header.ItemCount;
            if (!inChain || GetRevisionElement(ref accessor, firstChunkId, index).Element is not { IsolationFlag: true } current
                || current.ComponentChunkId != ownChunkId)
            {
                index = -1;
                for (var i = 0; i < header.ItemCount; i++)
                {
                    var candidate = (short)(header.FirstItemIndex + i);
                    ref var element = ref GetRevisionElement(ref accessor, firstChunkId, candidate).Element;
                    if (element.IsolationFlag && element.ComponentChunkId == ownChunkId)
                    {
                        index = candidate;
                        break;
                    }
                }
            }

            if (index < 0)
            {
                return false;
            }

            GetRevisionElement(ref accessor, firstChunkId, index).Element.ComponentChunkId = 0;
            compRevInfo.CurRevisionIndex = index;
            return true;
        }
        finally
        {
            header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId);
            header.Control.ExitExclusiveAccess();
            EndChainLockHold(ref accessor, in ticket);
        }
    }

    /// <summary>
    /// Voids this transaction's own pending entry (CHAIN-01): found by its identity under the chain's exclusive lock, wherever a compaction has moved it.
    /// For a rollback, which has nothing to fail into: the lock is waited for without a deadline, and the wait ends — a cleanup only tries the lock, a
    /// publish or a conflict-handler commit holding it releases it (AP-05's argument).
    /// </summary>
    /// <param name="info">The component's table info.</param>
    /// <param name="firstChunkId">The chain's root chunk.</param>
    /// <param name="ownChunkId">The content chunk this transaction's entry names; 0 for a delete entry.</param>
    /// <param name="tsn">This transaction's TSN, which identifies a delete entry.</param>
    /// <param name="hintIndex">Where the transaction last saw its entry; checked first.</param>
    /// <returns>
    /// <c>false</c> when the chain holds no pending entry of this transaction: nothing is voided, and the caller must not free <paramref name="ownChunkId"/> —
    /// an entry naming it, if any, is committed and not this transaction's to void (#1252).
    /// </returns>
    internal static bool VoidOwnPendingEntry(ComponentInfo info, int firstChunkId, int ownChunkId, long tsn, short hintIndex)
    {
        ref var accessor = ref info.CompRevTableAccessor;
        ref var header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId, true);

        // A rollback run while this thread holds the chain already — a conflict handler, under the lock its commit holds, disposing another transaction
        // that wrote the entity — voids under that lock: waiting for the lock it holds would never end.
        var held = header.Control.IsLockedByCurrentThread;
        var ticket = default(PagedMMF.CaptureTicket);
        if (!held)
        {
            ticket = ChainLockTicket(ref accessor, ref header);
            EnterChainLockForRollback(info, ref header, firstChunkId);
        }

        try
        {
            var index = FindOwnPendingEntry(ref accessor, firstChunkId, ownChunkId, tsn, hintIndex);
            if (index < 0)
            {
                return false;
            }

            VoidEntry(ref accessor, firstChunkId, index);
            return true;
        }
        finally
        {
            if (!held)
            {
                header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId);
                header.Control.ExitExclusiveAccess();
                EndChainLockHold(ref accessor, in ticket);
            }
        }
    }

    /// <summary>
    /// The chain's exclusive lock for a rollback's void, waited for without a deadline — a rollback has nothing to fail into — in slices, each one that runs
    /// out reported with the lock's holder (#1273). The wait ends unless the holder waits on this rollback: a conflict handler that, while its commit holds
    /// the chain's lock, waits on another transaction of the same entity. Handler code must not; the report names the thread that does.
    /// </summary>
    private static void EnterChainLockForRollback(ComponentInfo info, ref CompRevStorageHeader header, int firstChunkId)
    {
        var dbe = info.ComponentTable.DBE;
        var interval = dbe?.RollbackVoidReportInterval ?? TimeSpan.FromSeconds(30);
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var slice = WaitContext.FromTimeout(interval);
            if (header.Control.EnterExclusiveAccess(ref slice))
            {
                return;
            }

            dbe?.NoteRollbackVoidWaiting(header.Control.LockedByThreadId, Stopwatch.GetElapsedTime(start), firstChunkId);
        }
    }

    /// <summary>
    /// The index of this transaction's pending entry: isolated, and naming <paramref name="ownChunkId"/> — or, for a delete (<paramref name="ownChunkId"/>
    /// 0), naming no chunk and carrying <paramref name="tsn"/>. -1 when the chain holds none. The caller holds the chain's exclusive lock.
    /// </summary>
    /// <param name="accessor">Accessor for revision table chunks.</param>
    /// <param name="firstChunkId">The chain's root chunk.</param>
    /// <param name="ownChunkId">The content chunk this transaction's entry names; 0 for a delete entry.</param>
    /// <param name="tsn">This transaction's TSN, which identifies a delete entry.</param>
    /// <param name="hintIndex">Where the transaction last saw its entry; checked first, and trusted only inside the chain.</param>
    internal static short FindOwnPendingEntry(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId, int ownChunkId, long tsn, short hintIndex)
    {
        ref var header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId);
        if (hintIndex >= header.FirstItemIndex && hintIndex < header.FirstItemIndex + header.ItemCount
            && IsOwnPendingEntry(GetRevisionElement(ref accessor, firstChunkId, hintIndex).Element, ownChunkId, tsn))
        {
            return hintIndex;
        }

        // One walk: the enumerator takes no lock of its own under the one the caller holds, so its budget is never consulted.
        using var enumerator = new RevisionEnumerator(ref accessor, firstChunkId, false, true, CleanupLockWc);
        while (enumerator.MoveNext())
        {
            if (IsOwnPendingEntry(enumerator.Current, ownChunkId, tsn))
            {
                return (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
            }
        }

        return -1;
    }

    private static bool IsOwnPendingEntry(in CompRevStorageElement element, int ownChunkId, long tsn) =>
        element.IsolationFlag && element.ComponentChunkId == ownChunkId && (ownChunkId != 0 || element.TSN == tsn);

    /// <summary>
    /// An element's commit stamp by VIS-01: a published element's own; a flagged element's owner's, when that owner is registered on a commit card — past
    /// its commit point, not yet published. False for a void, for an element of <paramref name="self"/>, and for a flagged element whose owner is not
    /// committing (an uncommitted write, or a rollback's remains).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryResolveStamp(in CompRevStorageElement element, CommitCardTable cards, long self, out long stamp)
    {
        stamp = 0;
        if (element.IsVoid)
        {
            return false;
        }

        var tsn = element.TSN;
        if (!element.IsolationFlag)
        {
            stamp = tsn;
            return tsn > 0;
        }

        return tsn != self && cards != null && cards.TryGetStamp(tsn, out stamp);
    }

    /// <summary>
    /// The chain's newest revision by commit stamp (VIS-05), flagged ones resolved through their owner's card (<see cref="TryResolveStamp"/>), the
    /// entries of <paramref name="self"/> left out: its index, or -1 when there is none. Conflict detection's test (plan 06 §3.10): a newest stamp above
    /// a writer's snapshot is a commit it did not see. Takes the chain's shared lock unless the caller holds it.
    /// </summary>
    internal static short FindNewestByStamp(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId, CommitCardTable cards, long self,
        out long newestStamp)
    {
        newestStamp = 0;
        short newest = -1;
        using var enumerator = new RevisionEnumerator(ref accessor, firstChunkId, false, true, CleanupLockWc);
        while (enumerator.MoveNext())
        {
            if (TryResolveStamp(enumerator.Current, cards, self, out var stamp) && stamp > newestStamp)
            {
                newestStamp = stamp;
                newest = (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
            }
        }

        return newest;
    }

    /// <summary>
    /// The highest stamp among the chain's published revisions, the one at <paramref name="exceptIndex"/> left out; 0 when none. The publish's test for
    /// the cluster HEAD copy (VIS-05): a revision is copied only above every other published one. The caller holds the chain's exclusive lock.
    /// </summary>
    internal static long HighestPublishedStamp(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId, short exceptIndex) =>
        NewestPublished(ref accessor, firstChunkId, exceptIndex, out _);

    /// <summary>
    /// <see cref="NewestPublished"/> for a caller that holds no lock on the chain — PREPARE without a conflict handler: read under the chain's shared lock,
    /// which the enumerator takes on <paramref name="budgetOwner"/>'s wait budget (CX-06), and throws on its timeout. Unpublished entries, this
    /// transaction's own among them, are left out.
    /// </summary>
    internal static long NewestPublishedUnlocked(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId, Transaction budgetOwner, out int contentChunkId)
    {
        long highest = 0;
        contentChunkId = 0;
        using var enumerator = new RevisionEnumerator(ref accessor, firstChunkId, false, true, CleanupLockWc, budgetOwner);
        while (enumerator.MoveNext())
        {
            ref var element = ref enumerator.Current;
            if (!element.IsVoid && !element.IsolationFlag && element.TSN > highest)
            {
                highest = element.TSN;
                contentChunkId = element.ComponentChunkId;
            }
        }

        return highest;
    }

    /// <summary>
    /// <see cref="HighestPublishedStamp"/>, with the content chunk that revision names (0 for a tombstone, or when there is none): the value a publish
    /// moves the entity's index entries from (#1263). The caller holds the chain's exclusive lock.
    /// </summary>
    internal static unsafe long NewestPublished(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId, short exceptIndex, out int contentChunkId)
    {
        long highest = 0;
        contentChunkId = 0;

        // The chain in its root alone — the steady state, cleanup keeping a chain to its root (#1298): its elements read in place, no enumerator.
        ref var header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId);
        if (header.ChainLength == 1)
        {
            var elements = accessor.GetChunkAsSpan(firstChunkId).Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>();
            for (var i = 0; i < header.ItemCount; i++)
            {
                // The revision index as the enumerator numbers it (FirstItemIndex + position, unwrapped), the element where the circular root holds it.
                var revisionIndex = header.FirstItemIndex + i;
                ref var element = ref elements[revisionIndex % CompRevCountInRoot];
                if (revisionIndex != exceptIndex && !element.IsVoid && !element.IsolationFlag && element.TSN > highest)
                {
                    highest = element.TSN;
                    contentChunkId = element.ComponentChunkId;
                }
            }

            return highest;
        }

        using var enumerator = new RevisionEnumerator(ref accessor, firstChunkId, false, true, CleanupLockWc);
        while (enumerator.MoveNext())
        {
            ref var element = ref enumerator.Current;
            if (!element.IsVoid && !element.IsolationFlag && element.TSN > highest
                && (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex) != exceptIndex)
            {
                highest = element.TSN;
                contentChunkId = element.ComponentChunkId;
            }
        }

        return highest;
    }

    /// <summary>
    /// Drops from <paramref name="heads"/> (<c>EntityPK → root</c>, from <see cref="EnumerateVersionedChainHeads(ComponentTable, ushort?, bool, HashSet{int})"/>)
    /// every chain whose newest published revision is a tombstone (<c>ComponentChunkId == 0</c>): a destroyed entity whose chain its cleanup had not
    /// released yet. A rebuild from chain heads placed it as a live entity (#1231). A chain with no published revision is kept, as before. Returns how many
    /// were dropped; their chains stay allocated, for the destroyed-entity cleanup or the orphan sweep.
    /// </summary>
    internal static int DropTombstonedHeads(ComponentTable table, Dictionary<long, int> heads)
    {
        if (heads.Count == 0)
        {
            return 0;
        }

        List<long> dropped = null;
        var accessor = table.CompRevTableSegment.CreateChunkAccessor();
        try
        {
            foreach (var (pk, root) in heads)
            {
                long newest = 0;
                var tombstone = false;
                using (var enumerator = new RevisionEnumerator(ref accessor, root, false, true, CleanupLockWc))
                {
                    while (enumerator.MoveNext())
                    {
                        ref var element = ref enumerator.Current;
                        if (!element.IsVoid && !element.IsolationFlag && element.TSN > newest)
                        {
                            newest = element.TSN;
                            tombstone = element.ComponentChunkId == 0;
                        }
                    }
                }

                if (tombstone)
                {
                    (dropped ??= []).Add(pk);
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        if (dropped == null)
        {
            return 0;
        }

        foreach (var pk in dropped)
        {
            heads.Remove(pk);
        }

        return dropped.Count;
    }

    /// <summary>
    /// Voids the entry at <paramref name="index"/>, and drops the chain's item count only when it is the chain's last entry: a void elsewhere is a hole —
    /// readers skip it, the next compaction drops it — and dropping the count would drop the chain's last entry instead: another transaction's revision,
    /// appended after this one (#1233). The caller holds the chain's exclusive lock.
    /// </summary>
    internal static void VoidEntry(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId, short index)
    {
        ref var header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId, true);
        var last = index == header.FirstItemIndex + header.ItemCount - 1;
        GetRevisionElement(ref accessor, firstChunkId, index).Element.Void();
        if (last)
        {
            header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId, true);
            --header.ItemCount;
        }
    }

    internal static unsafe int AllocCompRevStorage(ComponentInfo info, long tsn, ushort uowId, int firstChunkId, long pk)
    {
        var chunkId = info.CompRevTableSegment.AllocateChunk(false, info.CompRevTableAccessor.ChangeSet);
        var chunkSpan = info.CompRevTableAccessor.GetChunkAsSpan(chunkId, true);

        ref var header = ref chunkSpan.Cast<byte, CompRevStorageHeader>()[0];

        // Initialize the header
        header.NextChunkId = 0;
        header.Control = default;
        header.FirstItemIndex = 0;
        header.ItemCount = 1;
        header.ChainLength = 1;
        header.Reserved = 0;
        header.CommitSequence = 1;
        header.EntityPK = pk;

        // Initialize the first element
        var elements = chunkSpan.Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>();
        elements[0].TSN = tsn;
        elements[0].IsolationFlag = true;                                  // Isolate this revision from the rest of the database (other transactions)
        elements[0].UowId = uowId;
        elements[0].ComponentChunkId = firstChunkId;

        return chunkId;
    }

    /// <summary>
    /// Core cleanup logic for a component's revision chain. Removes all entries older than <paramref name="nextMinTSN"/>,
    /// releases unused component chunks, and defragments the remaining revisions.
    /// </summary>
    /// <param name="ct">The component table owning the entity</param>
    /// <param name="firstChunkId">The first chunk ID of the revision chain</param>
    /// <param name="nextMinTSN">The minimal TSN to keep revisions</param>
    /// <param name="compRevTableAccessor">Accessor for the revision table segment</param>
    /// <param name="compContentAccessor">Accessor for the component content segment</param>
    /// <param name="deferredChunkFrees">Optional list collecting content chunks to free in a later pass; when <c>null</c>, freed chunks are released immediately</param>
    /// <returns><c>true</c> if the component is fully deleted (single tombstone remaining), <c>false</c> otherwise</returns>
    /// <remarks>
    /// <para>This method is transaction-agnostic and can be called from both the transaction commit path and the lazy cleanup path.</para>
    /// <para>A <b>sentinel</b> revision is preserved when the first kept entry has TSN &gt; nextMinTSN: this is the last committed
    /// revision before the cutoff, needed because active transactions at MinTSN may have cached its content chunk ID.</para>
    /// <para>All or nothing up to the root's rewrite (REAP-02, #1308): the walk writes nothing of the live chain and frees nothing, so a throw in it — a
    /// back-pressure timeout on an overflow chunk's page — leaves the chain as it was. The frees follow the rewrite, and cannot throw when deferred: the
    /// list's room is taken before. The compacted chain is the root alone (#1298): a compaction that would keep more is put off, the chain unchanged.</para>
    /// </remarks>
    internal static unsafe bool CleanUpUnusedEntriesCore(ComponentTable ct, int firstChunkId, long nextMinTSN,
        ref ChunkAccessor<PersistentStore> compRevTableAccessor, ref ChunkAccessor<PersistentStore> compContentAccessor,
        List<DeferredCleanupManager.DeferredChunkFreeEntry> deferredChunkFrees = null)
    {
        ref var firstChunkHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId);

        // Phase 6: Data:MVCC:VersionCleanup span — covers the compaction work for one entity's revision chain.
        var versionCleanupScope = TyphonEvent.BeginDataMvccVersionCleanup(firstChunkHeader.EntityPK);

        // Create a temporary chunk to store the cleaned-up content of the first chunk (we can't overwrite the first chunk right away)
        Span<byte> tempChunk = stackalloc byte[CompRevChunkSize];
        tempChunk.Clear();
        tempChunk.Split(out Span<CompRevStorageHeader> tempFirstHeader, out Span<CompRevStorageElement> tempElements);
        tempFirstHeader[0].ChainLength = 1;
        tempFirstHeader[0].CommitSequence = firstChunkHeader.CommitSequence;
        tempFirstHeader[0].EntityPK = firstChunkHeader.EntityPK;
        var curNextChunkId = tempChunk.Slice(0, sizeof(int)).Cast<byte, int>();
        var curDestElements = tempElements;
        var curDestIndex = 0;
        var curDestIndexInChunk = 0;
        var skipCount = 0;
        var hasCollections = ct.HasCollections;

        // Every overflow chunk of the old chain — the ChainLength - 1 chunks linked from the first: the compacted chain is the first chunk plus fresh ones,
        // so all of them go, once the first chunk names the new chain (REAP-02). Exactly that many links: the last chunk's own link is not trusted to be 0.
        // Following the enumeration instead missed every one of them when the chain started at index 0.
        var chainLength = firstChunkHeader.ChainLength;
        Span<int> chunksToFree = (chainLength < 128) ? stackalloc int[chainLength] : new int[chainLength];
        var chunksToFreeCount = 0;
        for (int next = firstChunkHeader.NextChunkId, left = chainLength - 1; next != 0 && left-- > 0;)
        {
            chunksToFree[chunksToFreeCount++] = next;
            next = compRevTableAccessor.GetChunk<int>(next);   // an overflow chunk's first field is its own next-chunk id
        }

        // The content chunks the compaction frees, freed once the root names the new chain — at most one per element — and the new chain's overflow chunks,
        // given back if the walk throws: the new chain is never longer than the old one, which starts at an offset when the new one starts at 0.
        int itemCount = firstChunkHeader.ItemCount;
        Span<int> contentFrees = (itemCount <= 512) ? stackalloc int[itemCount] : new int[itemCount];
        var contentFreeCount = 0;
        var stepProbe = ct.DBE?.CompactionStepProbe;
        Span<byte> destSpan;

        try
        {
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, firstChunkId, false, true, CleanupLockWc);
            var maxSkipCount = firstChunkHeader.ItemCount;

            // Sentinel: the highest-stamp committed revision below the cutoff (VIS-06) — the one the oldest snapshot reads. A skip candidate with a higher
            // stamp supersedes it and frees its chunk; one with a lower stamp is freed itself: chain order is no version order (VIS-05), and keeping the
            // last candidate in chain order freed the newest revision of a chain whose writers published out of order ([A(12), B(10)], cutoff 13).
            // The final sentinel is either emitted to the compacted output or freed, depending on the first kept entry.
            CompRevStorageElement sentinelEntry = default;
            int sentinelCompChunkId = 0;
            bool hasSentinel = false;
            bool sentinelKept = false;
            bool inSkipPhase = true;

            while (enumerator.MoveNext())
            {
                stepProbe?.Invoke(firstChunkId);

                // A void — a rolled-back entry, or a conflict's orphan — is not an entry (CHAIN-01): dropped wherever it is, and it counts for nothing. Kept,
                // it ended the skip phase as the chain's last entry and the sentinel, the newest committed revision below the cutoff, was freed as no longer
                // needed.
                if (enumerator.Current.IsVoid)
                {
                    skipCount++;
                    continue;
                }

                // A committed entry below the cutoff and older than the sentinel is superseded for every snapshot wherever it sits, chain order being no
                // version order (VIS-05): a last-writer-wins commit publishes its entry in place, ahead of the commit it won against. The count guard does
                // not keep it — the sentinel stays instead — and past the skip phase it is not copied.
                var superseded = hasSentinel && !enumerator.Current.IsolationFlag && enumerator.Current.TSN > 0 && enumerator.Current.TSN < nextMinTSN
                                 && enumerator.Current.TSN < sentinelEntry.TSN;

                // Skip phase: remove entries older than the cutoff, track the last committed one as sentinel. Active uncommitted entries (IsolationFlag=true)
                // must NOT enter the skip phase — they belong to transactions that will commit later. Freeing them would corrupt their data.
                // IsolationFlag check is before maxSkipCount decrement to avoid wasting the guard count.
                if (inSkipPhase && !enumerator.Current.IsolationFlag && (--maxSkipCount > 0 || superseded) && (enumerator.Current.TSN < nextMinTSN))
                {
                    var revChunkId = enumerator.Current.ComponentChunkId;
                    bool isCommitted = (enumerator.Current.TSN > 0);

                    if (isCommitted)
                    {
                        if (hasSentinel && enumerator.Current.TSN < sentinelEntry.TSN)
                        {
                            // Superseded by the sentinel, which is newer.
                            PendContentFree(contentFrees, ref contentFreeCount, revChunkId);
                        }
                        else
                        {
                            // Supersede the previous sentinel: free/defer its chunk, promote the current entry.
                            if (hasSentinel)
                            {
                                PendContentFree(contentFrees, ref contentFreeCount, sentinelCompChunkId);
                            }

                            sentinelEntry = enumerator.Current;
                            sentinelCompChunkId = revChunkId;
                            hasSentinel = true;
                        }
                    }
                    else
                    {
                        // No TSN, yet not void: no such entry is written. Freed like a void's chunk would be (0, a no-op).
                        PendContentFree(contentFrees, ref contentFreeCount, revChunkId);
                    }

                    skipCount++;
                    continue;
                }

                // Transition out of skip phase: resolve sentinel
                if (inSkipPhase)
                {
                    inSkipPhase = false;
                    if (hasSentinel)
                    {
                        if (enumerator.Current.TSN >= nextMinTSN || enumerator.Current.IsolationFlag || enumerator.Current.TSN < sentinelEntry.TSN)
                        {
                            // Keep sentinel: the first kept entry is at or beyond the cutoff, OR is an active uncommitted entry (IsolationFlag=true) invisible
                            // to readers, OR is older than the sentinel (a lower stamp, VIS-06) — the sentinel provides the read baseline for transactions at
                            // MinTSN.
                            if (curDestIndexInChunk == curDestElements.Length)
                            {
                                goto Deferred;
                            }

                            curDestElements[curDestIndexInChunk++] = sentinelEntry;
                            tempFirstHeader[0].ItemCount++;
                            curDestIndex++;
                            skipCount--; // Sentinel was counted as skipped but is being kept
                            sentinelKept = true;
                        }
                        else
                        {
                            // Sentinel not needed: the first kept entry is visible to all remaining transactions
                            PendContentFree(contentFrees, ref contentFreeCount, sentinelCompChunkId);
                        }
                    }
                }

                if (sentinelKept && superseded)
                {
                    PendContentFree(contentFrees, ref contentFreeCount, enumerator.Current.ComponentChunkId);
                    skipCount++;
                    continue;
                }

                // Copy entry to compacted output — into the root alone (#1298): what the compaction keeps past it, it would put in fresh chunks after the
                // checkpoint's barrier, and a crash that wrote the root and not them recovered an older revision, the newest being in the lost chunk.
                // Put off instead, to a pass that finds fewer entries to keep: nothing has been written or freed yet.
                if (curDestIndexInChunk == curDestElements.Length)
                {
                    goto Deferred;
                }

                curDestElements[curDestIndexInChunk++] = enumerator.Current;
                tempFirstHeader[0].ItemCount++;
                curDestIndex++;
            }

            // Every entry was skipped — what followed the last committed one was voids: the sentinel is the chain's newest revision, and stays. The count
            // guard above keeps the last entry, but only when it is no void.
            if (inSkipPhase && hasSentinel)
            {
                if (curDestIndexInChunk == curDestElements.Length)
                {
                    goto Deferred;
                }

                curDestElements[curDestIndexInChunk] = sentinelEntry;
                tempFirstHeader[0].ItemCount++;
                skipCount--;
            }

            // Nothing but voids: the chain keeps one, the shape the recovery scrub gives a chain with no committed revision — readers find nothing in it,
            // and a chain is never empty.
            if (tempFirstHeader[0].ItemCount == 0)
            {
                tempElements[0] = default;
                tempFirstHeader[0].ItemCount = 1;
                skipCount--;
            }

            // The room for every deferred free, and the root's page, taken before the rewrite: past it, nothing may throw.
            deferredChunkFrees?.EnsureCapacity(deferredChunkFrees.Count + contentFreeCount + chunksToFreeCount);
            destSpan = compRevTableAccessor.GetChunkAsSpan(firstChunkId, true);
        }
        catch
        {
            // The live chain is as it was: the walk wrote nothing of it and allocated nothing.
            versionCleanupScope.Dispose();
            throw;
        }

        // Cleanup does NOT increment CommitSequence. CS represents the total number of commits to this entity, and cleanup removing old entries
        // doesn't constitute a new commit — a changed CS is a handler-less commit's conflict signal. The commit path's CurRevisionIndex re-validation
        // (FindRevisionIndexByChunkId) handles stale indices caused by chain compaction.

        // Copy the compacted data to the first chunk, but SKIP the Control field (offset 4, size 4)
        // to avoid zeroing it. The caller holds the exclusive lock and the Control must remain intact.
        var controlFieldEnd = sizeof(int) + sizeof(int); // NextChunkId (4) + Control (4) = 8
        tempChunk.Slice(0, sizeof(int)).CopyTo(destSpan.Slice(0, sizeof(int)));
        tempChunk.Slice(controlFieldEnd).CopyTo(destSpan.Slice(controlFieldEnd));

        // The content chunks of the entries the new chain dropped, now that nothing names them.
        for (var i = 0; i < contentFreeCount; i++)
        {
            DeferOrFreeContentChunk(ct, ref compContentAccessor, contentFrees[i], hasCollections, deferredChunkFrees);
        }

        // The old overflow chunks, now that nothing names them: deferred with the content frees, past every transaction alive now — a rollback or a
        // conflict resolution can still write through a handle it took into the old layout before this pass took the lock. Immediately only without a
        // deferral list (a caller with no live transactions). Their collection buffers are not theirs: a revision chunk holds elements, and each
        // content chunk's buffers went with it above.
        for (var i = 0; i < chunksToFreeCount; i++)
        {
            if (deferredChunkFrees != null)
            {
                deferredChunkFrees.Add(new DeferredCleanupManager.DeferredChunkFreeEntry { Table = ct, ChunkId = chunksToFree[i], Revision = true });
            }
            else
            {
                ct.CompRevTableSegment.FreeChunk(chunksToFree[i]);
            }
        }

        // Phase 6: capture entries freed (== skipCount, capped at u16) before disposing the span.
        versionCleanupScope.EntriesFreed = (ushort)Math.Min(skipCount, ushort.MaxValue);
        versionCleanupScope.Dispose();

        // Is the component totally deleted? Return true, otherwise false
        return (tempFirstHeader[0].ItemCount == 1 && tempElements[0].ComponentChunkId == 0);

        // What the compaction would keep does not fit the root (#1298): the chain stays as it is, for a later pass — the next commit to the entity queues one.
        Deferred:
        Interlocked.Increment(ref CompactionsDeferred);
        versionCleanupScope.Dispose();
        return false;
    }

    /// <summary>Compactions put off because what they would keep does not fit the chain's root (#1298). Diagnostic.</summary>
    internal static long CompactionsDeferred;

    /// <summary>
    /// Recovery scrub (03-recovery.md §6, D1): collapses one entity's revision chain to exactly its <b>HEAD</b> — the highest-TSN committed
    /// (<see cref="CompRevStorageElement.IsolationFlag"/>==0, non-void) element — freeing every other element's content chunk and every overflow table chunk,
    /// then writing the head alone into the first chunk (<c>ItemCount=1, ChainLength=1, NextChunkId=0, FirstItemIndex=0</c>, UowId cleared).
    /// Unlike <see cref="CleanUpUnusedEntriesCore"/> it keeps <b>no sentinel</b>: a crash leaves no active readers (D1 — the history horizon resets at crash),
    /// so the read baseline a sentinel protects does not exist. Uncommitted (IsolationFlag==1) elements belong to transactions whose commit marker never became
    /// durable; their content is freed. Single-threaded recovery use only — no concurrent access, so the chunk <c>Control</c> lock is neither held nor needed
    /// (the field is preserved by writing individual header fields, never the whole header).
    /// </summary>
    /// <returns>
    /// <c>true</c> (informational — no caller action required) if the chain had <b>no committed head</b> (only voided/uncommitted elements) and was reduced to
    /// a single void element so the entity reads as invisible. <c>false</c> if a committed head was kept — including a <b>tombstone</b> head
    /// (<c>ComponentChunkId == 0</c>), the canonical "deleted entity" state, filtered by the read path. Either way the chain is left valid (ItemCount==1) and
    /// the caller need not touch the EntityMap.
    /// </returns>
    internal static unsafe bool ScrubChainToHead(ComponentTable ct, int firstChunkId, ref ChunkAccessor<PersistentStore> compRevTableAccessor,
        ref ChunkAccessor<PersistentStore> compContentAccessor, List<int> droppedContent, HashSet<int> staleContent, out long headTsn,
        HashSet<int> liveRoots = null)
    {
        headTsn = 0;   // max committed TSN surviving in this chain (0 if no committed head) — fed to NextFreeTSN recovery (RB-05)
        ref var firstHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId);
        var hasCollections = ct.HasCollections;
        var chainLength = firstHeader.ChainLength;
        var commitSequence = firstHeader.CommitSequence;
        var entityPk = firstHeader.EntityPK;

        // A root whose header is none — a torn page — is left as it is: sized from it, the spans below overran the stack (review of #1251).
        if (!HasRootHeader(ref firstHeader))
        {
            return false;
        }

        // (1) Collect every overflow table chunk (the whole chain except the first chunk) by following the NextChunkId
        // linkage. After scrub the head lives alone in the first chunk, so all overflow chunks are freed unconditionally.
        Span<int> overflowChunks = (chainLength < 128) ? stackalloc int[chainLength] : new int[chainLength];
        var overflowCount = 0;
        {
            // ChainLength counts the root: ChainLength - 1 overflow chunks, no more. A torn chain's link past them — its last overflow chunk's page as of
            // before, naming the chunk it was next to in a previous life — reached another chain's live root, which this freed (#1251). Nor into a live
            // root within them: a root page as of before a compaction names, as its overflow, the chunk since reused as that root.
            var next = firstHeader.NextChunkId;
            var capacity = ct.CompRevTableSegment.ChunkCapacity;
            while (next > 0 && next < capacity && overflowCount < chainLength - 1 && (liveRoots == null || !liveRoots.Contains(next)))
            {
                overflowChunks[overflowCount++] = next;
                next = compRevTableAccessor.GetChunk<CompRevStorageHeader>(next).NextChunkId;
            }
        }

        // (2) Walk the elements to find the head (highest-TSN committed element); collect every non-head content chunk.
        CompRevStorageElement head = default;
        var hasHead = false;
        Span<int> contentToFree = (firstHeader.ItemCount < 128) ? stackalloc int[firstHeader.ItemCount] : new int[firstHeader.ItemCount];
        var contentCount = 0;
        {
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, firstChunkId, false, true, CleanupLockWc);
            while (enumerator.MoveNext())
            {
                ref var el = ref enumerator.Current;
                if (el.IsVoid)
                {
                    continue;
                }

                if (el.IsolationFlag)
                {
                    // Uncommitted at crash → rolled back; free its content (a delete entry has ComponentChunkId==0 → no-op).
                    contentToFree[contentCount++] = el.ComponentChunkId;
                    continue;
                }

                // A later element of the same TSN wins: it is the replay's, appended over a chain the data file holds as of after the transaction while
                // its content is as of before. The two are the same revision; only the replay's content is the transaction's (#1251).
                if (!hasHead || el.TSN >= head.TSN)
                {
                    if (hasHead)
                    {
                        // The loser of a tie is the torn on-disk element: its content chunk's bytes may be another revision's, and its collection
                        // buffer ids with them — freed without releasing those; the replay's twin rebuilt the collection.
                        if (el.TSN == head.TSN && head.ComponentChunkId != 0)
                        {
                            staleContent.Add(head.ComponentChunkId);
                        }

                        contentToFree[contentCount++] = head.ComponentChunkId;
                    }
                    head = el;
                    hasHead = true;
                }
                else
                {
                    contentToFree[contentCount++] = el.ComponentChunkId;
                }
            }
        }

        // (3) Free all overflow table chunks AFTER the enumerator is disposed (the circular buffer must not be mutated mid-walk — the use-after-free
        // discipline of CleanUpUnusedEntriesCore). The non-head content chunks are handed to the caller, which frees each once every chain of the table is
        // scrubbed, and only if no surviving head names it: a torn element can name a chunk another chain's head holds (#1251).
        for (var i = 0; i < contentCount; i++)
        {
            if (contentToFree[i] != 0)
            {
                droppedContent.Add(contentToFree[i]);
            }
        }
        for (var i = 0; i < overflowCount; i++)
        {
            ct.CompRevTableSegment.FreeChunk(overflowChunks[i]);
        }

        // (4) Rewrite the first chunk to hold exactly the head. Write individual fields (NEVER the whole header) so the
        // Control field at offset 4 is preserved — same discipline as CleanUpUnusedEntriesCore.
        var destSpan = compRevTableAccessor.GetChunkAsSpan(firstChunkId, true);
        destSpan.Split(out Span<CompRevStorageHeader> destHeader, out Span<CompRevStorageElement> destElements);
        destHeader[0].NextChunkId = 0;
        destHeader[0].FirstItemIndex = 0;
        destHeader[0].ChainLength = 1;
        destHeader[0].CommitSequence = commitSequence;
        destHeader[0].EntityPK = entityPk;

        if (!hasHead)
        {
            // No committed revision survived (only voided / uncommitted elements — their transactions never became durable).
            // Leave a single VOID element so the entity reads as invisible/deleted via the normal chain walk. The root chunk +
            // EntityMap entry remain — a rare, harmless residual (a checkpointed chunk that held only uncommitted revisions);
            // it is not orphan-reclaimed because it is still EntityMap-reachable.
            destElements[0] = default;                       // all-zero ⇒ IsVoid ⇒ invisible to readers
            destHeader[0].ItemCount = 1;
            destHeader[0].FirstItemIndex = 0;
            destHeader[0].Reserved = 0;
            return true;
        }

        headTsn = head.TSN;             // RB-05: the surviving head's TSN — NextFreeTSN must be advanced past it on recovery
        head.UowId = 0;                 // §6: UowId cleared — the writing transaction is meaningless post-crash (registry is volatile)
        head.IsolationFlag = false;
        destElements[0] = head;
        destHeader[0].ItemCount = 1;
        destHeader[0].Reserved = 0;

        // A committed head — live or tombstone — is kept as the single surviving element. The entity stays in the EntityMap;
        // a tombstone is filtered by the read path. Only the no-committed-head case above drops the entity.
        return false;
    }

    /// <summary>
    /// Scans a Versioned component table's <see cref="ComponentTable.CompRevTableSegment"/> for revision-chain heads — allocated chunks that are NOT the
    /// <see cref="CompRevStorageHeader.NextChunkId"/> overflow target of another chunk — filtered to <paramref name="archetypeId"/> (the PK's low 16 bits).
    /// Returns <c>EntityPK → first(root)-chunk-id</c>.
    /// Two passes:
    /// (1) collect the overflow set
    /// (2) heads = allocated ∧ not-overflow ∧ archetype-match. Shared by EntityMap rebuild (<see cref="DatabaseEngine"/>) and recovery scrub
    /// (03-recovery.md §6) so the two scans never drift. Returns an empty map for a null / non-Versioned / empty table.
    /// </summary>
    /// <remarks>
    /// <paramref name="archetypeId"/> is a FILTER, and omitting it means "every archetype". The crash rebuild passes one because it runs per archetype;
    /// the schema-migration backfill (<c>ComponentTable.PopulateNewIndexes</c>) must not, because a ComponentTable's index is shared by every archetype
    /// holding the component and a filtered backfill would leave the others unindexed (#670).
    /// </remarks>
    internal static Dictionary<long, int> EnumerateVersionedChainHeads(ComponentTable table, ushort? archetypeId = null, bool acceptNamedRoots = false,
        HashSet<int> acceptedNamed = null)
    {
        if (table?.CompRevTableSegment == null || table.StorageMode != StorageMode.Versioned)
        {
            return [];
        }

        return EnumerateVersionedChainHeads(table.CompRevTableSegment, archetypeId, acceptNamedRoots, acceptedNamed);
    }

    /// <summary>
    /// The chain roots of a revision segment, keyed by EntityPK: the allocated chunks no other chunk names as its next — and, with
    /// <paramref name="acceptNamedRoots"/>, a named chunk with a root's header whose EntityPK no unnamed chunk has.
    /// </summary>
    /// <remarks>
    /// The data file holds each page as of its last capture, so a chunk freed by a compaction can still be on disk, allocated and naming as its next a
    /// chunk the engine has since reused as a live chain's root: excluded, that root lost its entity its chain (#1251). Only the crash rebuild of a
    /// cluster EntityMap accepts named roots, and it adopts one only for the EntityPK of an entity its clusters hold: an overflow chunk's bytes can read
    /// as a root's header (its first element's TSN modulo 2^16 as the chain length, its second element as the EntityPK), and walked, scrubbed or reset
    /// as a root, it corrupted the chain it belongs to (review of #1251). Read through a scan accessor: the segment may be larger than the cache.
    /// </remarks>
    /// <param name="segment">The revision segment.</param>
    /// <param name="archetypeId">The archetype whose roots are returned, or <c>null</c> for every root.</param>
    /// <param name="acceptNamedRoots">Whether a named chunk with a root's header is its entity's root when no unnamed chunk is.</param>
    /// <param name="acceptedNamed">When given, receives the named chunks returned.</param>
    internal static unsafe Dictionary<long, int> EnumerateVersionedChainHeads(ChunkBasedSegment<PersistentStore> segment, ushort? archetypeId,
        bool acceptNamedRoots, HashSet<int> acceptedNamed = null)
    {
        var heads = new Dictionary<long, int>();
        var capacity = segment.ChunkCapacity;
        if (capacity == 0 || segment.AllocatedChunkCount == 0)
        {
            return heads;
        }

        var accessor = segment.CreateScanAccessor();
        try
        {
            // Pass 1: every chunk referenced as another chunk's NextChunkId is an overflow chunk, not a head.
            var overflowSet = new HashSet<int>();
            foreach (var chunkId in segment.AllocatedChunks())
            {
                if (segment.IsChunkHeld(chunkId))
                {
                    continue;
                }

                var next = accessor.GetChunk<CompRevStorageHeader>(chunkId).NextChunkId;
                if (next != 0)
                {
                    overflowSet.Add(next);
                }
            }

            // Pass 2: heads = allocated chunks not in the overflow set, owned by this archetype (or all of them when unfiltered); then the named chunks
            // with a root's header, for the entities left without one.
            List<(long Pk, int Chunk)> namedRoots = null;
            foreach (var chunkId in segment.AllocatedChunks())
            {
                if (segment.IsChunkHeld(chunkId))
                {
                    continue;
                }

                var pk = accessor.GetChunk<CompRevStorageHeader>(chunkId).EntityPK;
                if (archetypeId.HasValue && EntityId.FromRaw(pk).ArchetypeId != archetypeId.Value)
                {
                    continue;
                }

                if (!overflowSet.Contains(chunkId))
                {
                    heads[pk] = chunkId;
                }
                else if (acceptNamedRoots && IsChainRoot(ref accessor, segment, chunkId))
                {
                    (namedRoots ??= []).Add((pk, chunkId));
                }
            }

            if (namedRoots != null)
            {
                foreach (var (pk, chunkId) in namedRoots)
                {
                    if (heads.TryAdd(pk, chunkId))
                    {
                        acceptedNamed?.Add(chunkId);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return heads;
    }

    /// <summary>
    /// Frees every chain lock a crash left held in the data file (PS-20). A capture can copy a held lock word, and only the checkpoint after the release
    /// rewrites it: a crash before that one leaves the hold on disk, its holder gone. Run on a crash open as the table loads — before the HEAD rebuild's
    /// walks, the log replay's revisions and the scrub, each of which takes chain locks and would wait for the dead holder. Every unnamed chunk is
    /// reset: a root — a destroyed entity's chain still awaiting release included — or a chunk no chain will link, whose bytes no one reads (an overflow
    /// chunk whose predecessor's page is as of before its growth). Filtered by a root's header check, a root whose overflow chunk the disk has as free
    /// kept its hold, and the open failed (review of #1251). A named chunk is left as it is — an overflow chunk's bytes at the lock's offset are an
    /// element's — and one the EntityMap rebuild adopts as its entity's root is reset as it is adopted (<see cref="ResetChainLocks"/>). A chunk is
    /// dirtied only when its word was set. Single-threaded.
    /// </summary>
    /// <returns>The number of chunks whose lock word was set.</returns>
    internal static unsafe int ResetChainLocksAfterCrash(ChunkBasedSegment<PersistentStore> segment, DatabaseEngine dbe) =>
        LockWordRecovery.ResetRootLocks(segment, dbe, ChainLockOffset, sizeof(AccessControlSmall),
            static (ref ChunkAccessor<PersistentStore> _, ChunkBasedSegment<PersistentStore> _, int _) => true);

    /// <summary>Clears the chain lock words of <paramref name="roots"/>: named chunks a crash open adopted as roots (see above). Single-threaded.</summary>
    /// <returns>The number of roots whose lock word was set.</returns>
    internal static unsafe int ResetChainLocks(ChunkBasedSegment<PersistentStore> segment, DatabaseEngine dbe, IEnumerable<int> roots) =>
        LockWordRecovery.ResetLocks(segment, dbe, ChainLockOffset, sizeof(AccessControlSmall), roots);

    private static readonly int ChainLockOffset = ComputeChainLockOffset();

    private static int ComputeChainLockOffset()
    {
        var header = default(CompRevStorageHeader);
        return (int)Unsafe.ByteOffset(ref Unsafe.As<CompRevStorageHeader, byte>(ref header), ref Unsafe.As<AccessControlSmall, byte>(ref header.Control));
    }

    /// <summary>The chunks of <paramref name="candidates"/> with a root's header and links (<see cref="IsChainRoot"/>). Inside an epoch scope.</summary>
    internal static List<int> FilterChainRoots(ChunkBasedSegment<PersistentStore> segment, IEnumerable<int> candidates)
    {
        var roots = new List<int>();
        var scan = segment.CreateScanAccessor();
        try
        {
            foreach (var chunkId in candidates)
            {
                if (IsChainRoot(ref scan, segment, chunkId))
                {
                    roots.Add(chunkId);
                }
            }
        }
        finally
        {
            scan.Dispose();
        }

        return roots;
    }

    // A root's header, as far as its own bytes tell — what a walk or a scrub of its chain is sized from (IsChainRoot also checks its links).
    private static bool HasRootHeader(ref CompRevStorageHeader header) => header.ChainLength >= 1 && header.ItemCount >= 0
        && header.ItemCount <= ComputeRevElementCount(header.ChainLength) && header.FirstItemIndex >= 0;

    /// <summary>
    /// Whether an unnamed chunk is a chain root and not an overflow chunk whose predecessor's link a crash lost: a plausible header, and its
    /// <c>ChainLength - 1</c> links all through allocated chunks.
    /// </summary>
    private static unsafe bool IsChainRoot(ref ChunkAccessor<PersistentStore> scan, ChunkBasedSegment<PersistentStore> segment, int chunkId)
    {
        ref var header = ref scan.GetChunk<CompRevStorageHeader>(chunkId);
        int length = header.ChainLength, items = header.ItemCount, first = header.FirstItemIndex, next = header.NextChunkId;
        if (length < 1 || length > segment.AllocatedChunkCount || items < 0 || items > ComputeRevElementCount(length) || first < 0 || header.EntityPK == 0)
        {
            return false;
        }

        for (var left = length - 1; left > 0; left--)
        {
            if (next <= 0 || next >= segment.ChunkCapacity || !segment.IsChunkLiveUnpinned(next))
            {
                return false;
            }

            next = *(int*)scan.GetChunkAddress(next);
        }

        return true;
    }

    /// <summary>
    /// Frees the content chunks the scrub dropped from <paramref name="table"/>'s chains — releasing their collection buffers — except those a surviving
    /// head names. A chain the data file holds as of after a transaction, its content as of before, names chunks the disk had as free, and the live engine
    /// may have reused one that another chain holds: freed through the first chain, that other chain's head read whatever took the chunk next (#1251).
    /// </summary>
    /// <remarks>A chunk in <paramref name="withoutRelease"/> — taken by the reservation (free on disk), or the content of a torn element a twin replaced —
    /// is freed without releasing its collection buffers: its bytes may predate it, so the buffer ids it holds are not its own.</remarks>
    internal static void FreeDroppedContent(ComponentTable table, HashSet<int> roots, List<int> droppedContent, HashSet<int> withoutRelease,
        ref ChunkAccessor<PersistentStore> revAccessor, ref ChunkAccessor<PersistentStore> contentAccessor, Action batchPoint = null)
    {
        if (droppedContent.Count == 0)
        {
            return;
        }

        var heads = new HashSet<int>(roots.Count);
        var sinceBatchPoint = 0;
        foreach (var rootChunkId in roots)
        {
            BatchPointEvery(ref sinceBatchPoint, batchPoint, ref revAccessor, ref contentAccessor);
            revAccessor.GetChunkAsSpan(rootChunkId).Split(out Span<CompRevStorageHeader> _, out Span<CompRevStorageElement> els);
            if (els[0].ComponentChunkId != 0)
            {
                heads.Add(els[0].ComponentChunkId);
            }
        }

        var freed = new HashSet<int>();
        var contentSeg = table.ComponentSegment;
        foreach (var chunkId in droppedContent)
        {
            BatchPointEvery(ref sinceBatchPoint, batchPoint, ref revAccessor, ref contentAccessor);
            if (!heads.Contains(chunkId) && freed.Add(chunkId) && (uint)chunkId < (uint)contentSeg.ChunkCapacity && contentSeg.IsChunkLiveUnpinned(chunkId))
            {
                if (withoutRelease != null && withoutRelease.Contains(chunkId))
                {
                    contentSeg.FreeChunk(chunkId);
                }
                else
                {
                    FreeCompContentChunk(table, ref contentAccessor, chunkId, table.HasCollections);
                }
            }
        }
    }

    /// <summary>
    /// On a crash open, before the log replay: marks allocated every chunk the chains of <paramref name="roots"/> — the live records' — name: their overflow
    /// chunks and their elements' content. A chunk's allocation bit is on its own page, so a chain the data file holds as of after a transaction can name
    /// chunks the disk has as free; the replay then handed them out again, and two chains shared a chunk — an entity read another's value, or zeros (#1251).
    /// The scrub and its orphan sweep free what no surviving chain names. An overflow chunk handed out was also cleared: its chain ended early, and the
    /// replay's next growth of it threw — the crash open failed. Returns the chunks reserved.
    /// </summary>
    internal static int ReserveChainChunksAfterCrash(ComponentTable table, IEnumerable<int> roots, HashSet<int> reservedContent)
    {
        var revSeg = table.CompRevTableSegment;
        var contentSeg = table.ComponentSegment;
        var reserved = 0;
        var accessor = revSeg.CreateScanAccessor();
        try
        {
            foreach (var rootChunkId in roots)
            {
                ref var header = ref accessor.GetChunk<CompRevStorageHeader>(rootChunkId);
                if (!HasRootHeader(ref header))
                {
                    continue;
                }

                // Every element slot of every chunk of the chain, read without its lock: a root found by the scan can be an overflow chunk whose bytes at
                // the lock's offset are an element's, read as a hold (review of #1251). A slot past the chain's items reserves a chunk the orphan sweep
                // frees again.
                int length = header.ChainLength, next = header.NextChunkId;
                accessor.GetChunkAsSpan(rootChunkId).Split(out Span<CompRevStorageHeader> _, out Span<CompRevStorageElement> rootElements);
                reserved += ReserveContent(contentSeg, rootElements, reservedContent);
                for (var left = length - 1; left > 0 && next > 0 && next < revSeg.ChunkCapacity; left--)
                {
                    if (!revSeg.IsChunkAllocatedUnpinned(next))
                    {
                        revSeg.ReserveChunk(next);
                        reserved++;
                    }

                    var chunk = accessor.GetChunkAsSpan(next);
                    reserved += ReserveContent(contentSeg, chunk.Slice(sizeof(int)).Cast<byte, CompRevStorageElement>().Slice(0, CompRevCountInNext),
                        reservedContent);
                    next = chunk.Cast<byte, int>()[0];
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return reserved;
    }

    /// <summary>
    /// On a crash open, before the log replay: collapses to its newest reachable committed revision each chain of <paramref name="roots"/> — the live
    /// records' — whose links end before its length. Its root's page reached the disk after a growth, the page of the chunk that links to the new one
    /// before it: the replay then appended past the last reachable chunk — into a chunk no walk reaches, or the reserved chunk 0 — the scrub kept a stale
    /// revision, and the next growth threw (#1297). A link to a chunk the disk has as free, or to another live record's root, is torn the same way: the free
    /// chunk was allocated after the bitmap page's capture, the root's header read as elements made a committed tombstone with a TSN past every replayed
    /// one. The revisions in the chunks past a torn link were written after the last completed checkpoint's barrier — an allocation before it reached the
    /// disk with that cycle — so the replay brings them back; a compaction that moved older revisions into fresh chunks after the barrier is the residual
    /// this cannot repair (#1298). Read through a scan accessor, a collapsed root written through an epoch one. Recovery only, single-threaded; returns the
    /// chains collapsed.
    /// </summary>
    internal static int CollapseTornChainsBeforeReplay(ComponentTable table, HashSet<int> roots, ChangeSet changeSet)
    {
        var revSeg = table.CompRevTableSegment;
        var capacity = revSeg.ChunkCapacity;
        var reachable = new List<int>();
        var collapsed = 0;
        var accessor = revSeg.CreateScanAccessor();
        var writer = revSeg.CreateChunkAccessor(changeSet);
        try
        {
            foreach (var root in roots)
            {
                ref var header = ref accessor.GetChunk<CompRevStorageHeader>(root);
                if (!HasRootHeader(ref header))
                {
                    continue;
                }

                int length = header.ChainLength, items = header.ItemCount, first = header.FirstItemIndex, next = header.NextChunkId;
                reachable.Clear();
                reachable.Add(root);
                for (var left = length - 1; left > 0; left--)
                {
                    if (next <= 0 || next >= capacity || !revSeg.IsChunkAllocatedUnpinned(next) || roots.Contains(next))
                    {
                        break;
                    }

                    reachable.Add(next);
                    next = accessor.GetChunk<int>(next);
                }

                if (reachable.Count == length)
                {
                    continue;
                }

                // The newest committed live revision in a reachable chunk.
                var span = ComputeRevElementCount(length);
                CompRevStorageElement head = default;
                var found = false;
                for (var i = 0; i < items; i++)
                {
                    var (chunkInChain, indexInChunk) = CompRevStorageHeader.GetRevisionLocation((first + i) % span);
                    if (chunkInChain >= reachable.Count)
                    {
                        continue;
                    }

                    var element = ElementOf(ref accessor, reachable[chunkInChain], chunkInChain == 0, indexInChunk);
                    if (!element.IsVoid && !element.IsolationFlag && (!found || element.TSN >= head.TSN))
                    {
                        head = element;
                        found = true;
                    }
                }

                // The root alone holds it — fields one by one, never the whole header, so the lock word is kept (as ScrubChainToHead).
                writer.GetChunkAsSpan(root, true).Split(out Span<CompRevStorageHeader> h, out Span<CompRevStorageElement> elements);
                h[0].NextChunkId = 0;
                h[0].FirstItemIndex = 0;
                h[0].ChainLength = 1;
                h[0].ItemCount = 1;
                h[0].Reserved = 0;
                if (found)
                {
                    head.UowId = 0;
                }

                elements[0] = found ? head : default;
                collapsed++;
            }
        }
        finally
        {
            accessor.Dispose();
            writer.Dispose();
        }

        return collapsed;
    }

    private static CompRevStorageElement ElementOf(ref ChunkAccessor<PersistentStore> accessor, int chunkId, bool isRoot, int index)
    {
        var chunk = accessor.GetChunkAsSpan(chunkId);
        if (isRoot)
        {
            chunk.Split(out Span<CompRevStorageHeader> _, out Span<CompRevStorageElement> rootElements);
            return rootElements[index];
        }

        return chunk.Slice(sizeof(int)).Cast<byte, CompRevStorageElement>()[index];
    }

    private static int ReserveContent(ChunkBasedSegment<PersistentStore> contentSeg, Span<CompRevStorageElement> elements, HashSet<int> reservedContent)
    {
        var reserved = 0;
        foreach (ref var element in elements)
        {
            var cc = element.ComponentChunkId;
            if (cc > 0 && cc < contentSeg.ChunkCapacity && !contentSeg.IsChunkAllocatedUnpinned(cc))
            {
                contentSeg.ReserveChunk(cc);
                reservedContent.Add(cc);
                reserved++;
            }
        }

        return reserved;
    }

    /// <summary>
    /// Recovery orphan sweep (03-recovery.md §6): after a table's chains have been scrubbed to their heads, free every allocated
    /// chunk no longer reachable — a revision-table chunk that is not a chain root, or a content chunk that no surviving head
    /// references. These are chunks leaked by pre-crash operations interrupted between allocation and linking. Post-scrub each
    /// chain is exactly its root (NextChunkId==0), so <paramref name="reachableRoots"/> (the chains' root chunk ids) is the full
    /// set of reachable revision-table chunks, and the roots' single head elements yield the only reachable content chunks.
    /// Orphan content is freed with a plain <c>FreeChunk</c> (NOT <see cref="FreeCompContentChunk"/>): a never-linked chunk's
    /// collection-buffer ids are unreliable, so its buffers are not released here — the crash open's collection sweep, which runs after, frees
    /// what nothing names (DatabaseEngine.SweepCollectionOrphans, #1307). Reserved chunk 0 (the null sentinel) reports unallocated, so it is never swept.
    /// </summary>
    internal static void SweepTableOrphans(ComponentTable table, HashSet<int> reachableRoots, ref ChunkAccessor<PersistentStore> revAccessor,
        ref ChunkAccessor<PersistentStore> contentAccessor, Action batchPoint = null)
    {
        var revSeg = table.CompRevTableSegment;
        var contentSeg = table.ComponentSegment;
        if (revSeg == null || contentSeg == null)
        {
            return;
        }

        // Reachable content = the single head element's ComponentChunkId of each surviving chain root.
        var reachableContent = new HashSet<int>(reachableRoots.Count);
        var sinceBatchPoint = 0;
        foreach (var rootChunkId in reachableRoots)
        {
            BatchPointEvery(ref sinceBatchPoint, batchPoint, ref revAccessor, ref contentAccessor);
            revAccessor.GetChunkAsSpan(rootChunkId).Split(out Span<CompRevStorageHeader> _, out Span<CompRevStorageElement> els);
            var cc = els[0].ComponentChunkId;
            if (cc != 0)
            {
                reachableContent.Add(cc);
            }
        }

        // Free orphan revision-table chunks (allocated, not a chain root) — the allocated chunks walked page by page (EP-02, #1183).
        foreach (var chunkId in revSeg.AllocatedChunks())
        {
            if (!revSeg.IsChunkHeld(chunkId) && !reachableRoots.Contains(chunkId))
            {
                BatchPointEvery(ref sinceBatchPoint, batchPoint, ref revAccessor, ref contentAccessor);
                revSeg.FreeChunk(chunkId);
            }
        }

        // Free orphan content chunks (allocated, referenced by no surviving head).
        foreach (var chunkId in contentSeg.AllocatedChunks())
        {
            if (!contentSeg.IsChunkHeld(chunkId) && !reachableContent.Contains(chunkId))
            {
                BatchPointEvery(ref sinceBatchPoint, batchPoint, ref revAccessor, ref contentAccessor);
                contentSeg.FreeChunk(chunkId);
            }
        }
    }

    // A crash open's batch point every DatabaseEngine.RecoveryBatchSize steps of a scrub walk (#1183, CK-08): the accessors' writes committed first — their
    // dirty slots released to the checkpoint, the pins of the slots they evicted returned. No address the walk took outlives its step.
    private static void BatchPointEvery(ref int since, Action batchPoint, ref ChunkAccessor<PersistentStore> revAccessor,
        ref ChunkAccessor<PersistentStore> contentAccessor)
    {
        if (batchPoint == null || ++since <= DatabaseEngine.RecoveryBatchSize)
        {
            return;
        }

        revAccessor.CommitChanges();
        contentAccessor.CommitChanges();
        batchPoint();
        since = 1;
    }

    /// <summary>A content chunk the compaction frees once the root names the new chain (#1308); 0 names none.</summary>
    private static void PendContentFree(Span<int> frees, ref int count, int chunkId)
    {
        if (chunkId != 0)
        {
            frees[count++] = chunkId;
        }
    }

    /// <summary>
    /// Either defers content chunk freeing (when <paramref name="deferredChunkFrees"/> is non-null) or frees immediately.
    /// The deferred path collects entries for later freeing when all referencing transactions have departed.
    /// The immediate path is used during the transaction commit path where the caller is the tail.
    /// </summary>
    private static void DeferOrFreeContentChunk(ComponentTable ct, ref ChunkAccessor<PersistentStore> compContentAccessor, int chunkId, bool hasCollections,
        List<DeferredCleanupManager.DeferredChunkFreeEntry> deferredChunkFrees)
    {
        if (chunkId == 0)
        {
            return;
        }

        if (deferredChunkFrees != null)
        {
            deferredChunkFrees.Add(new DeferredCleanupManager.DeferredChunkFreeEntry { Table = ct, ChunkId = chunkId });
            return;
        }

        FreeCompContentChunk(ct, ref compContentAccessor, chunkId, hasCollections);
    }

    /// <summary>
    /// Searches the revision chain for an entry matching the given identifiers and returns its absolute revision index. Used to recover from stale cached
    /// indices after chain compaction.
    /// </summary>
    /// <param name="compRevTableAccessor">Accessor for revision table chunks</param>
    /// <param name="firstChunkId">First chunk of the revision chain</param>
    /// <param name="componentChunkId">Content chunk ID to match (unique for non-delete entries)</param>
    /// <param name="tsn">Transaction TSN — used as discriminator for delete entries (ComponentChunkId = 0)</param>
    /// <returns>The absolute revision index if found; -1 otherwise.</returns>
    internal static short FindRevisionIndexByChunkId(ref ChunkAccessor<PersistentStore> compRevTableAccessor, int firstChunkId, int componentChunkId, long tsn = 0)
    {
        using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, firstChunkId, false, true, CleanupLockWc);
        while (enumerator.MoveNext())
        {
            if (componentChunkId != 0)
            {
                // Non-delete: ComponentChunkId is unique per allocation — unambiguous match
                if (enumerator.Current.ComponentChunkId == componentChunkId)
                {
                    return (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
                }
            }
            else if (enumerator.Current.IsolationFlag && enumerator.Current.TSN == tsn)
            {
                // Delete entry: ComponentChunkId = 0, disambiguate by TSN + uncommitted flag
                return (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
            }
        }
        return -1;
    }

    /// <summary>
    /// Releases a component content chunk and its associated collection buffers.
    /// </summary>
    private static void FreeCompContentChunk(ComponentTable ct, ref ChunkAccessor<PersistentStore> compContentAccessor, int chunkId, bool hasCollections)
    {
        if (chunkId == 0)
        {
            return;
        }

        if (hasCollections)
        {
            foreach (var f in ct.CollectionFields)
            {
                // The descriptor's offset is within the component struct; content chunks prefix it with ComponentOverhead
                // (Versioned = 0, SingleVersion = 8 for the entity PK). Cluster slots have no overhead.
                var bufferId = compContentAccessor.GetChunkAsReadOnlySpan(chunkId)
                    .Slice(ct.ComponentOverhead + f.OffsetInComponentStorage).Cast<byte, int>()[0];

                // 0: the field never got a buffer. Released, it decremented the reserved chunk 0 as a reference count, under chunk 0's exclusive lock —
                // the crash-open scrub's path, which then waited on whatever hold the data file had kept on chunk 0 (#1291).
                if (bufferId == 0)
                {
                    continue;
                }

                var collAccessor = f.Vsbs.Segment.CreateChunkAccessor();
                f.Vsbs.BufferRelease(bufferId, ref collAccessor);
                collAccessor.Dispose();
            }
        }

        ct.ComponentSegment.FreeChunk(chunkId);
    }

    private static void GrowChain(ComponentInfo info, int firstChunkId, ref CompRevStorageHeader firstHeader)
    {
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        var compRevTable = info.CompRevTableSegment;

        // Special case, the first revision is in the first chunk, we need to walk to the end of the chain and add a new chunk there
        if (firstHeader.FirstItemIndex < CompRevCountInRoot)
        {
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, firstChunkId, false, false, CleanupLockWc);
            enumerator.StepToChunk(firstHeader.ChainLength - 1, false);         // Walk to the last chunk in the chain
            enumerator.NextChunkId = compRevTable.AllocateChunk(true, info.CompRevTableAccessor.ChangeSet); // Allocated, clear content to make sure the next chunk ID is 0, set as next
            compRevTableAccessor.DirtyChunk(enumerator.CurChunkId);
            firstHeader.ChainLength++;
        }
        else
        {
            // Locate the first index in the chain, we add a chunk just before it
            var (firstChunkInChain, firstItemIndexInChunk) = CompRevStorageHeader.GetRevisionLocation(firstHeader.FirstItemIndex);
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, firstChunkId, false, false, CleanupLockWc);
            enumerator.StepToChunk(firstChunkInChain-1, false);                 // In a circular buffer, the chunk before the first is the last one

            // Get the ID of the first chunk in the chain
            var firstChunkIndexInChain = enumerator.NextChunkId;

            // Add a new chunk after the last in the chain
            var newChunkId = compRevTable.AllocateChunk(true, info.CompRevTableAccessor.ChangeSet); // Clear content to make sure the next chunk ID is 0

            // Copy the elements from the first chunk to the new chunk, then link it: both fetches can throw (a back-pressure timeout), and linked first,
            // a throw left the chain last → new → 0 with its length and first index unchanged — the retry grew it again from the orphan (#1382).
            var newChunkElements = compRevTableAccessor.GetChunkAsSpan(newChunkId, true).Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
            var firstChunkElements = compRevTableAccessor.GetChunkAsSpan(firstChunkIndexInChain, true).Slice(sizeof(int))
                .Cast<byte, CompRevStorageElement>();
            firstChunkElements.Slice(0, firstItemIndexInChunk).CopyTo(newChunkElements);
            enumerator.NextChunkId = newChunkId;
            compRevTableAccessor.DirtyChunk(enumerator.CurChunkId);

            firstHeader.ChainLength++;                                              // One more item in the chain
            firstHeader.FirstItemIndex += (short)CompRevCountInNext; // We added a chunk before, the first item index gets shifted
        }
        compRevTableAccessor.DirtyChunk(firstChunkId);
    }

    private static int ComputeRevElementCount(int chainLength) => CompRevCountInRoot + ((chainLength - 1) * CompRevCountInNext);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe short GetRevisionLocation(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId, short revisionIndex, out int resChunkId)
    {
        if (revisionIndex < CompRevCountInRoot)
        {
            resChunkId = firstChunkId;
            return revisionIndex;
        }

        (int chunkIndexInChain, int indexInChunk) = CompRevStorageHeader.GetRevisionLocation(revisionIndex);

        // Walk through the linked list until we find the chunk that is our starting point
        var header = (CompRevStorageHeader*)accessor.GetChunkAddress(firstChunkId);
        resChunkId = header->NextChunkId;

        var first = header;
        var useLock = !first->Control.IsLockedByCurrentThread;
        var ticket = default(PagedMMF.CaptureTicket);
        if (useLock)
        {
            ticket = ChainLockTicket(ref accessor, ref *first);
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);
            if (!first->Control.EnterSharedAccess(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("RevisionChain/GetLocation", TimeoutOptions.Current.RevisionChainLockTimeout);
            }
        }
        while (--chunkIndexInChain != 0)
        {
            resChunkId = *(int*)accessor.GetChunkAddress(resChunkId);
        }
        if (useLock)
        {
            first->Control.ExitSharedAccess();
            EndChainLockHold(ref accessor, in ticket);
        }

        return (short)indexInChunk;
    }
}
