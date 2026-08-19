// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Typhon.Engine.Internals;

[PublicAPI]
public class ChangeSet
{
    private readonly PagedMMF _owner;
    // Per-page count of IncrementDirty calls registered THROUGH this ChangeSet. Each call to AddByMemPageIndex (first-time per
    // page) or RegisterReDirty (subsequent re-dirty) bumps this counter AND calls PagedMMF.IncrementDirty. The exact count is the
    // source of truth for ReleaseDirtyMarks and Reset, so both can decrement using the same conservation-respecting
    // primitive (PagedMMF.DecrementDirty) — NOT the racing cap-to-1 primitive (DecrementDirtyToMin) that used to live here.
    // See claude/research/Durability/DCManagementRace.md (#385) and ADR-NNN for the full rationale.
    private readonly Dictionary<int, int> _marksByPage;
    private Task _saveTask;

    // Deferred eviction queue: when a ChunkAccessor<PersistentStore> slot is evicted, SlotRefCount and ACW
    // decrements are deferred here until CommitChanges/Dispose. This lives on ChangeSet (a class)
    // rather than ChunkAccessor<PersistentStore> (a struct) to keep the struct blittable for JIT inlining.
    // The sign bit of each entry encodes dirty (1) vs clean (0) for ACW handling.
    private List<int> _deferredEvictions;

    // ── DEBUG concurrent-mutation detector (#705 T5 / #400) ─────────────────────────────────────────────────────────────────────────────────────────────────
    // Managed thread id currently inside a mutating method, 0 when none; plus a re-entrancy depth for that thread. NOT an owner-thread assert: in Deferred and
    // GroupCommit the UoW deliberately SHARES one ChangeSet across every transaction it creates (UnitOfWork.cs:64-66), so this object has no owner thread and
    // an owner assert would fire on correct code. What is never legal is two threads mutating it AT THE SAME TIME — `_marksByPage` is a plain Dictionary and
    // `_deferredEvictions` a plain List, so concurrent mutation can lose entries, mis-count marks, or corrupt the bucket chain outright. That is #400's
    // mechanism, and it was silent in 36 of 40 runs.
    private int _mutatorThreadId;
    private int _mutationDepth;

    public ChangeSet(PagedMMF owner)
    {
        _owner = owner;
        _marksByPage = new Dictionary<int, int>();
    }

    /// <summary>
    /// Marks entry to a mutating method; throws naming BOTH threads if another is already inside. Compiled out entirely in Release.
    /// </summary>
    /// <remarks>
    /// A sequential hand-off between threads is legal and must not fire — only overlap is the defect — so this tracks residency rather than ownership. The
    /// depth counter exists because the mutating methods call one another (<c>Reset</c> → the per-page decrement loop), and a re-entrant call from the thread
    /// already inside is not a race.
    /// </remarks>
    [System.Diagnostics.Conditional("DEBUG")]
    private void EnterMutation(string member)
    {
        var me = Environment.CurrentManagedThreadId;
        var prev = System.Threading.Interlocked.CompareExchange(ref _mutatorThreadId, me, 0);
        if (prev == 0)
        {
            _mutationDepth = 1;
            return;
        }

        if (prev == me)
        {
            _mutationDepth++;
            return;
        }

        throw new InvalidOperationException(
            $"ChangeSet concurrent mutation: thread {me} entered {member} while thread {prev} was still inside a mutating method. This ChangeSet is shared "
            + "across the UnitOfWork's transactions (Deferred/GroupCommit), and its backing Dictionary/List are not thread-safe — concurrent mutation loses "
            + "dirty marks or corrupts the map (#400). Sequential hand-off between threads is fine; overlap is not.");
    }

    /// <summary>Marks exit from a mutating method. Compiled out entirely in Release.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void ExitMutation()
    {
        if (_mutatorThreadId != Environment.CurrentManagedThreadId)
        {
            return; // a losing thread that threw on entry never took residency
        }

        if (--_mutationDepth <= 0)
        {
            _mutationDepth = 0;
            System.Threading.Interlocked.Exchange(ref _mutatorThreadId, 0);
        }
    }

    /// <summary>
    /// Enqueue a deferred SlotRefCount decrement (and optionally ACW decrement) for an evicted slot.
    /// The sign bit encodes dirty (needs ACW decrement) vs clean (SlotRefCount only).
    /// </summary>
    internal void DeferEviction(int entry)
    {
        EnterMutation(nameof(DeferEviction));
        try
        {
            _deferredEvictions ??= new List<int>(16);
            _deferredEvictions.Add(entry);
        }
        finally
        {
            ExitMutation();
        }
    }

    /// <summary>
    /// Flush all deferred eviction decrements (SlotRefCount + ACW for dirty slots).
    /// Called by <see cref="ChunkAccessor{PersistentStore}.CommitChanges"/> and <see cref="ChunkAccessor{PersistentStore}.Dispose"/>.
    /// </summary>
    internal void FlushDeferredEvictions()
    {
        if (_deferredEvictions == null || _deferredEvictions.Count == 0)
        {
            return;
        }

        EnterMutation(nameof(FlushDeferredEvictions));
        try
        {
            foreach (var entry in _deferredEvictions)
            {
                var memIdx = entry & 0x7FFFFFFF;
                if (entry < 0)
                {
                    _owner.DecrementActiveChunkWriters(memIdx);
                }
                _owner.DecrementSlotRefCount(memIdx);
            }
            _deferredEvictions.Clear();
        }
        finally
        {
            ExitMutation();
        }
    }

    /// <summary>
    /// Mark a page as dirty by its memory page index (first registration). Calls <see cref="PagedMMF.IncrementDirty"/> exactly
    /// once and tracks the page with a per-page mark count of 1. Subsequent calls for the same page are no-ops; callers that
    /// need to register an additional dirty mark (CP-04 re-dirty defence) must call <see cref="RegisterReDirty"/> instead.
    /// </summary>
    /// <returns><c>true</c> if this was the first registration for this page in this ChangeSet; <c>false</c> if already tracked.</returns>
    public bool AddByMemPageIndex(int memPageIndex)
    {
        bool firstRegistration;
        EnterMutation(nameof(AddByMemPageIndex));
        try
        {
            firstRegistration = _marksByPage.TryAdd(memPageIndex, 1);
        }
        finally
        {
            ExitMutation();
        }

        if (firstRegistration)
        {
            _owner.IncrementDirty(memPageIndex);   // takes a mark AND records the modification
            return true;
        }

        // Already tracked, so no second mark — but the caller has just modified the page again, and that MUST be
        // recorded. Only the counted mark is idempotent here; the writeback obligation never is. Skipping it was a real
        // lost write: a checkpoint that captured and discharged this page between the first registration and now would
        // leave the page looking clean while holding bytes that reached no disk, and the next eviction would drop them.
        _owner.MarkPageModified(memPageIndex);
        return false;
    }

    /// <summary>
    /// Register an additional IncrementDirty for a page already tracked by this ChangeSet — the CP-04 "re-dirty" pattern.
    /// Bumps the per-page mark count and calls <see cref="PagedMMF.IncrementDirty"/>, both as one logical step from the
    /// ChangeSet's accounting perspective. Used by <see cref="ChunkAccessor{T}.MarkSlotDirty"/> and
    /// <see cref="ChunkBasedSegment{T}.AllocateChunk(ChangeSet, ref ChunkAccessor{T})"/> when an already-tracked page is re-dirtied within the same UoW —
    /// previously these sites called <c>_store.IncrementDirty</c> directly, which left the increment "untracked" and forced
    /// <see cref="ReleaseDirtyMarks"/> to use a non-conservation cap-to-1 (the source of the #385 race).
    /// </summary>
    /// <remarks>
    /// If the page is NOT already tracked (caller forgot to call <see cref="AddByMemPageIndex"/> first), this method treats
    /// the call as a fresh registration — defensive behaviour so that an out-of-order call still produces a balanced mark.
    /// </remarks>
    internal void RegisterReDirty(int memPageIndex)
    {
        EnterMutation(nameof(RegisterReDirty));
        try
        {
            if (_marksByPage.TryGetValue(memPageIndex, out var n))
            {
                _marksByPage[memPageIndex] = n + 1;
            }
            else
            {
                _marksByPage[memPageIndex] = 1;
            }
        }
        finally
        {
            ExitMutation();
        }
        _owner.IncrementDirty(memPageIndex);
    }

    public void SaveChanges() => SaveChangesAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    public Task SaveChangesAsync()
    {
        if (_marksByPage.Count == 0)
        {
            return Task.CompletedTask;
        }

        // The structural-write path (bootstrap / schema evolution / segment growth / recovery replay), distinct from the user-data UoW path — which
        // is drained by the checkpoint and never calls SaveChanges.
        //
        // Release our marks HERE, not in SavePages. SavePages writes the pages and discharges their writeback debt; it has no idea how many marks
        // this ChangeSet took, and the old arrangement — where it decremented once per page — silently leaked N-1 on any page this set had
        // re-dirtied. Owner-scoped release keeps the counter conserved: we took N, we release N, and the pages stay protected until SavePages'
        // fsync clears the debt.
        var pages = _marksByPage.Keys.ToArray();
        ReleaseDirtyMarks();
        _saveTask = _owner.SavePages(pages);
        return _saveTask;
    }

    /// <summary>
    /// Releases <b>every</b> <c>DirtyCounter</c> mark this ChangeSet took — exactly <c>N</c> per page, matching the <c>N</c>
    /// it registered. After this returns the ChangeSet owes nothing and tracks nothing; further dirtying re-registers from
    /// scratch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to leave one mark per page behind, deliberately, so that the page stayed non-evictable until a checkpoint
    /// wrote it. That is what made the counter leak: the marks arrive once per unit of work, the checkpoint acks once per
    /// <i>cycle</i>, and K units of work inside one cycle therefore strand K-1 marks that nothing will ever release. The
    /// page becomes permanently unevictable, the cache fills with pages that are clean on disk, and the engine dies on
    /// page-cache backpressure (#824). Releasing N-1 of N is not a conservation rule — it is a slow leak with a rule's
    /// shape.
    /// </para>
    /// <para>
    /// Releasing all N is safe now because eviction protection for unwritten bytes no longer rides on this counter: the
    /// page's writeback generation carries it, and only a durable write discharges it. So the page stays put until
    /// it has actually been written, which is the guarantee the retained mark was approximating.
    /// </para>
    /// </remarks>
    public void ReleaseDirtyMarks()
    {
        if (_marksByPage.Count == 0)
        {
            return;
        }

        EnterMutation(nameof(ReleaseDirtyMarks));
        try
        {
            foreach (var kv in _marksByPage)
            {
                _owner.DecrementDirtyByDelta(kv.Key, kv.Value);
            }
            _marksByPage.Clear();
        }
        finally
        {
            ExitMutation();
        }
    }

    /// <summary>
    /// Undoes every dirty mark this ChangeSet took (transaction rollback). Identical accounting to
    /// <see cref="ReleaseDirtyMarks"/> — the two differ only in intent, and both are exact.
    /// </summary>
    /// <remarks>
    /// Rollback does NOT clear the page's writeback debt, and must not: the bytes were modified in place before the
    /// rollback decided to abandon them, so the page on disk is stale either way and still has to be rewritten.
    /// </remarks>
    public void Reset()
    {
        ReleaseDirtyMarks();
        _saveTask = null;
    }

    /// <summary>
    /// Clear ChangeSet state for reuse via <see cref="PagedMMF.RentChangeSet"/> / <see cref="PagedMMF.ReturnChangeSet"/>.
    /// Caller must guarantee dirty marks have already been resolved (via <see cref="SaveChangesAsync"/> /
    /// <see cref="ReleaseDirtyMarks"/> / <see cref="Reset"/>) before clearing — this only zeroes the local
    /// tracking buffers without touching DirtyCounter / ACW / SlotRefCount on owner pages.
    /// </summary>
    /// <remarks>
    /// The DEBUG check below turns that "must" into something that fails. Dropping the map while it still holds marks
    /// leaks every one of them: the pages stay non-evictable for the life of the process and nothing anywhere records
    /// that they should not be. It is the exact shape of #824, it is silent, and a pooled ChangeSet is returned thousands
    /// of times a second at 60 Hz — so a caller that forgets once forgets constantly.
    /// </remarks>
    internal void ClearForReuse()
    {
        System.Diagnostics.Debug.Assert(_marksByPage.Count == 0,
            $"ChangeSet returned to the pool still holding marks on {_marksByPage.Count} page(s). Release them first "
            + "(ReleaseDirtyMarks / Reset / SaveChangesAsync) — clearing the map here does not return them, it strands them (PS-05).");

        _marksByPage.Clear();
        _deferredEvictions?.Clear();
        _saveTask = null;
    }
}