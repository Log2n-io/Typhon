using JetBrains.Annotations;
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
    // source of truth for ReleaseExcessDirtyMarks and Reset, so both can decrement using the same conservation-respecting
    // primitive (PagedMMF.DecrementDirty) — NOT the racing cap-to-1 primitive (DecrementDirtyToMin) that used to live here.
    // See claude/research/Durability/DCManagementRace.md (#385) and ADR-NNN for the full rationale.
    private readonly Dictionary<int, int> _marksByPage;
    private Task _saveTask;

    // Deferred eviction queue: when a ChunkAccessor<PersistentStore> slot is evicted, SlotRefCount and ACW
    // decrements are deferred here until CommitChanges/Dispose. This lives on ChangeSet (a class)
    // rather than ChunkAccessor<PersistentStore> (a struct) to keep the struct blittable for JIT inlining.
    // The sign bit of each entry encodes dirty (1) vs clean (0) for ACW handling.
    private List<int> _deferredEvictions;

    public ChangeSet(PagedMMF owner)
    {
        _owner = owner;
        _marksByPage = new Dictionary<int, int>();
    }

    /// <summary>
    /// Enqueue a deferred SlotRefCount decrement (and optionally ACW decrement) for an evicted slot.
    /// The sign bit encodes dirty (needs ACW decrement) vs clean (SlotRefCount only).
    /// </summary>
    internal void DeferEviction(int entry)
    {
        _deferredEvictions ??= new List<int>(16);
        _deferredEvictions.Add(entry);
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

    /// <summary>
    /// Mark a page as dirty by its memory page index (first registration). Calls <see cref="PagedMMF.IncrementDirty"/> exactly
    /// once and tracks the page with a per-page mark count of 1. Subsequent calls for the same page are no-ops; callers that
    /// need to register an additional dirty mark (CP-04 re-dirty defence) must call <see cref="RegisterReDirty"/> instead.
    /// </summary>
    /// <returns><c>true</c> if this was the first registration for this page in this ChangeSet; <c>false</c> if already tracked.</returns>
    public bool AddByMemPageIndex(int memPageIndex)
    {
        if (!_marksByPage.TryAdd(memPageIndex, 1))
        {
            return false;
        }

        _owner.IncrementDirty(memPageIndex);
        return true;
    }

    /// <summary>
    /// Register an additional IncrementDirty for a page already tracked by this ChangeSet — the CP-04 "re-dirty" pattern.
    /// Bumps the per-page mark count and calls <see cref="PagedMMF.IncrementDirty"/>, both as one logical step from the
    /// ChangeSet's accounting perspective. Used by <see cref="ChunkAccessor{T}.MarkSlotDirty"/> and
    /// <see cref="ChunkBasedSegment{T}.AllocateChunk"/> when an already-tracked page is re-dirtied within the same UoW —
    /// previously these sites called <c>_store.IncrementDirty</c> directly, which left the increment "untracked" and forced
    /// <see cref="ReleaseExcessDirtyMarks"/> to use a non-conservation cap-to-1 (the source of the #385 race).
    /// </summary>
    /// <remarks>
    /// If the page is NOT already tracked (caller forgot to call <see cref="AddByMemPageIndex"/> first), this method treats
    /// the call as a fresh registration — defensive behaviour so that an out-of-order call still produces a balanced mark.
    /// </remarks>
    internal void RegisterReDirty(int memPageIndex)
    {
        if (_marksByPage.TryGetValue(memPageIndex, out var n))
        {
            _marksByPage[memPageIndex] = n + 1;
        }
        else
        {
            _marksByPage[memPageIndex] = 1;
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

        // SavePages writes each page once and decrements DC once per page in its continuation. This is the synchronous structural-write path
        // (bootstrap / schema evolution / segment growth / recovery replay), distinct from the user-data UoW path — which is drained by the
        // checkpoint and never calls SaveChanges. AddByMemPageIndex's first-add is balanced by SavePages's single decrement; structural ChangeSets
        // are one-shot (created, saved, discarded), so they don't run ReleaseExcessDirtyMarks and any surplus RegisterReDirty marks are harmless.
        var pages = _marksByPage.Keys.ToArray();
        _marksByPage.Clear();
        _saveTask = _owner.SavePages(pages);
        return _saveTask;
    }

    /// <summary>
    /// Drains the excess <c>DirtyCounter</c> marks tracked by this ChangeSet, leaving exactly one mark per page for the next
    /// checkpoint cycle to ack. Replaces the previous cap-to-1 implementation (<c>DecrementDirtyToMin(p, 1)</c>) which raced
    /// with the background checkpoint's <see cref="PagedMMF.DecrementDirty"/> and caused the lost-write durability bug
    /// captured in issue #385.
    /// <para>
    /// For user-data (UoW) pages, <see cref="SaveChangesAsync"/> is never called — the checkpoint writes them, so WAL records replace the need
    /// for per-UoW dirty page writeback. However, <see cref="AddByMemPageIndex"/> and <see cref="RegisterReDirty"/> still call <c>IncrementDirty</c>
    /// for every mark, so without this release hot pages would accumulate a DirtyCounter equal to the number of UoWs (and
    /// re-dirty events) that touched them — permanently unevictable by the page cache clock-sweep.
    /// </para>
    /// <para>
    /// Conservation property (this is the FIX): for a page with tracked mark count <c>N</c>, we issue exactly
    /// <c>(N - 1)</c> <see cref="PagedMMF.DecrementDirty"/> calls. After this method runs, the page contributes exactly
    /// one outstanding mark from this UoW. The next checkpoint cycle's single <c>DecrementDirty</c> brings DC back to its
    /// pre-UoW baseline. Both decrement operations are now the same primitive, so they cannot over-decrement under any
    /// thread interleaving.
    /// </para>
    /// </summary>
    public void ReleaseExcessDirtyMarks()
    {
        if (_marksByPage.Count == 0)
        {
            return;
        }

        foreach (var kv in _marksByPage)
        {
            var excess = kv.Value - 1;
            if (excess > 0)
            {
                _owner.DecrementDirtyByDelta(kv.Key, excess);
            }
        }
        _marksByPage.Clear();
    }

    /// <summary>
    /// Undo all dirty marks tracked by this ChangeSet (used on transaction rollback). For each tracked page, calls
    /// <see cref="PagedMMF.DecrementDirty"/> exactly <c>N</c> times where <c>N</c> is the tracked mark count — fully reverses
    /// every <see cref="AddByMemPageIndex"/> + <see cref="RegisterReDirty"/> the UoW issued. Unlike the prior implementation
    /// (which decremented once per page and intentionally left excess marks behind for "next checkpoint" cleanup), this is now
    /// fully conservation-respecting.
    /// </summary>
    public void Reset()
    {
        foreach (var kv in _marksByPage)
        {
            _owner.DecrementDirtyByDelta(kv.Key, kv.Value);
        }
        _marksByPage.Clear();
        _saveTask = null;
    }

    /// <summary>
    /// Clear ChangeSet state for reuse via <see cref="PagedMMF.RentChangeSet"/> / <see cref="PagedMMF.ReturnChangeSet"/>.
    /// Caller must guarantee dirty marks have already been resolved (via <see cref="SaveChangesAsync"/> /
    /// <see cref="ReleaseExcessDirtyMarks"/> / <see cref="Reset"/>) before clearing — this only zeroes the local
    /// tracking buffers without touching DirtyCounter / ACW / SlotRefCount on owner pages.
    /// </summary>
    internal void ClearForReuse()
    {
        _marksByPage.Clear();
        _deferredEvictions?.Clear();
        _saveTask = null;
    }
}