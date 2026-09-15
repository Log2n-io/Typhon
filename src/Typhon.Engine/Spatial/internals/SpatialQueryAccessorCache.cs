using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-thread warm <see cref="ChunkAccessor{TStore}"/>s for the cluster spatial query: one entry per cluster segment in use, kept across queries.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A query that builds its own accessor starts with an empty 32-slot page window, so every page it touches takes the slow path —
/// <c>LoadAndGet</c> → <c>RequestPageEpoch</c> (an <c>AccessEpoch</c> compare-and-swap) plus an interlocked <c>SlotRefCount</c> increment, undone by an
/// interlocked decrement in <c>Dispose</c> — on page records every worker shares. Measured on the SWG demo at x16: 5.3 page loads per query, about 100 ns
/// each. A worker's consecutive queries are spatially close, so a window kept across them is served from the MRU or SIMD path.</para>
/// <para><b>Why an entry outlives the epoch it was filled in.</b> A slot pins its page with <c>SlotRefCount</c>, and a page is evicted only at
/// <c>SlotRefCount == 0</c> (overview/03-storage.md, eviction predicate), so a slot's address stays valid whatever the epoch. The epoch tag protects only
/// pages nobody pins. The B+Tree's warm accessor drops its window on an epoch change; this cache must not, because the global epoch advances on every
/// outermost scope exit and a game typically opens one scope per actor.</para>
/// <para><b>What an address from a warm window is good for: until the next <c>GetChunkAddress</c> on that window.</b> A slot evicted from the window drops
/// its pin at once (the standalone path), and a page an earlier query loaded carries an old <c>AccessEpoch</c>, so after that neither a pin nor the caller's
/// epoch protects it. <c>ChunkAccessor</c>'s "valid for the enclosing EpochGuard" does not hold for these accessors. The enumerator never keeps an address
/// past its next cluster open.</para>
/// <para><b>Nesting and copies.</b> A query that finds its segment's entry rented takes another entry, so two live queries never share a window. A return must
/// carry the token its rent stamped, and bumps it (SQ-05's pattern): <c>GetEnumerator()</c> copies the enumerator, and neither a stale copy's return nor its
/// continued use may reach a window that has been handed back — the enumerator checks the token before each use.</para>
/// <para><b>How long pins live.</b> A free entry keeps its window's pages pinned — that is the point — until one of three things releases them:
/// <see cref="Release"/>, which an engine calls for its own page cache when that cache is under back-pressure and when the engine is disposed; the entry's
/// finalizer, when its thread dies and the thread-static cache goes with it; or recycling past <see cref="TrimAbove"/>. So at most <see cref="TrimAbove"/> ×
/// 32 pages per thread, and none that back-pressure cannot reclaim. A rented entry is in use and never released from outside. A release reaches only the
/// windows over the releasing engine's pages: another engine's are none of its business, and emptying them would only make their next query re-warm. An
/// entry a release emptied is refilled by its thread's next query rather than replaced, so re-warming allocates nothing.</para>
/// <para><b>Leaked rents.</b> A query drained to the end hands its entry back itself; one that is neither drained nor disposed keeps it. Past
/// <see cref="TrimAbove"/> with every pooled entry rented, a rent gets an overflow entry that is disposed on return rather than kept, so leaks cannot grow the
/// cache — and an overflow entry never returned releases its pins in its finalizer.</para>
/// </remarks>
internal sealed class SpatialQueryAccessorCache
{
    /// <summary>Pooled entries per thread. Past it the least recently rented free entry is recycled; with none free, a rent gets an overflow entry.</summary>
    internal const int TrimAbove = 8;

    private const int Free = 0;
    private const int Rented = 1;
    private const int Releasing = 2;

    internal sealed class Entry
    {
        internal ChunkAccessor<PersistentStore> Accessor;
        internal ChunkBasedSegment<PersistentStore> Segment;

        /// <summary>
        /// A promoted cell half's overlapping cluster ids, collected by the query that holds this entry. Grown on demand, kept with the entry.
        /// </summary>
        internal int[] TreeHits = new int[64];

        /// <summary>Stamped by each rent and bumped by each return: a rent is live only while its token is current.</summary>
        internal int Token;

        /// <summary>
        /// <c>Free</c>, <c>Rented</c> or <c>Releasing</c>, moved by compare-and-swap: <see cref="Release"/> claims free entries of other threads.
        /// </summary>
        internal int State;

        internal long LastRent;

        /// <summary>False for an overflow entry, which is disposed on return rather than kept.</summary>
        internal bool Pooled;

        /// <summary>The managed id of the thread whose cache made this entry, the only thread that rents it: its slot in a query tally (SO-02).</summary>
        internal int ThreadId;

        /// <summary>Releases the window's pins once nothing references the entry: its thread died, or an overflow rent was never returned.</summary>
        ~Entry() => Accessor.Dispose();
    }

    [ThreadStatic]
    // ReSharper disable once InconsistentNaming
    private static SpatialQueryAccessorCache _instance;

    // Every thread's cache, held weakly: ReleaseAll must reach other threads' free windows without keeping a dead thread's cache alive.
    private static readonly List<WeakReference<SpatialQueryAccessorCache>> Caches = [];
    private static readonly Lock CachesLock = new();

    private Entry[] _entries = new Entry[4];
    private int _count;
    private long _clock;

    // The owning thread's managed id, stamped on every entry this cache makes. The cache is thread-static, so it is built on the thread it serves.
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private SpatialQueryAccessorCache()
    {
        lock (CachesLock)
        {
            Caches.Add(new WeakReference<SpatialQueryAccessorCache>(this));
        }
    }

    internal static SpatialQueryAccessorCache Instance => _instance ??= new SpatialQueryAccessorCache();

    /// <summary>Pooled entries this thread's cache holds.</summary>
    internal int EntryCount => _count;

    /// <summary>Rent this thread's warm window over <paramref name="segment"/>. Must be called inside an epoch scope, like any accessor creation.</summary>
    internal Entry Rent(ChunkBasedSegment<PersistentStore> segment, out int token)
    {
        Entry oldestFree = null;
        Entry released = null;
        var entries = _entries;
        for (int i = 0; i < _count; i++)
        {
            var e = entries[i];

            // A filter: the claim below decides, so a stale read costs one failed compare-and-swap or one skipped entry.
            if (e.State != Free)
            {
                continue;
            }

            var held = e.Segment;
            if (held == segment)
            {
                if (TryClaim(e))
                {
                    // A release between the segment read and the claim left the entry empty: refill it.
                    return Take(e.Segment == segment ? e : Fill(e, segment), out token);
                }

                continue;
            }

            if (held == null)
            {
                released ??= e;
            }
            else if (oldestFree == null || e.LastRent < oldestFree.LastRent)
            {
                oldestFree = e;
            }
        }

        // An entry a release emptied is refilled before the cache grows or recycles: re-warming it allocates nothing, a new entry does. Only this thread
        // fills its entries, so once claimed it is still empty.
        if (released != null && TryClaim(released))
        {
            return Take(Fill(released, segment), out token);
        }

        return Take(Acquire(oldestFree, segment), out token);
    }

    /// <summary>
    /// Hand an entry back. Ignored unless it is still rented under <paramref name="token"/>; honoured, it bumps the token, so no copy of the rent can use the
    /// window again. An overflow entry is disposed instead of kept.
    /// </summary>
    /// <returns>True when the return was honoured: exactly one of the copies that carried a rent sees it, which is what lets a query add its tally once
    /// (SO-02).</returns>
    internal static bool Return(Entry entry, int token)
    {
        // Only the live rent passes the token check, and no other thread writes a rented entry's State: this reads this thread's own write.
        if (entry.Token != token || entry.State != Rented)
        {
            return false;
        }

        entry.Token++;
        if (!entry.Pooled)
        {
            entry.Accessor.Dispose();
            GC.SuppressFinalize(entry);
            return true;
        }

        // Release: a Release on another thread claims the entry by compare-and-swap and must find the window as this thread left it.
        Volatile.Write(ref entry.State, Free);
        return true;
    }

    /// <summary>
    /// Release every free window over <paramref name="mmf"/>'s pages, on every thread: dispose its accessor, dropping its pins. An engine calls this when its
    /// page cache is under back-pressure and when it is disposed — the two moments its pinned pages must become evictable. A rented entry is in use and left
    /// alone; a released one is refilled by its thread's next query. Windows over other page caches are untouched.
    /// </summary>
    internal static void Release(ManagedPagedMMF mmf)
    {
        lock (CachesLock)
        {
            for (int i = Caches.Count - 1; i >= 0; i--)
            {
                if (Caches[i].TryGetTarget(out var cache))
                {
                    cache.ReleaseFree(mmf);
                }
                else
                {
                    Caches.RemoveAt(i);
                }
            }
        }
    }

    // Runs on any thread, reading another thread's cache. Plain reads suffice for the array and its entries: storing an object reference is a release for
    // that object's fields and elements (.NET memory model), and reads through the reference cannot run ahead of it. A stale count or a not-yet-published
    // slot only skips an entry added meanwhile, which a later release reaches. Everything before the claim is a filter: the claim's compare-and-swap is the
    // acquire that pairs with the owner's release of Free, and the segment is read again once the entry is claimed and can no longer change.
    private void ReleaseFree(ManagedPagedMMF mmf)
    {
        var entries = _entries;
        var count = Math.Min(_count, entries.Length);
        for (int i = 0; i < count; i++)
        {
            var e = entries[i];
            if (e == null || e.State != Free || !IsOver(e.Segment, mmf) || Interlocked.CompareExchange(ref e.State, Releasing, Free) != Free)
            {
                continue;
            }

            if (IsOver(e.Segment, mmf))
            {
                e.Accessor.Dispose();
                e.Segment = null;
            }

            // Release: the owner's next claim must see the disposed accessor and the null segment, or it would skip the refill and use a dead window.
            Volatile.Write(ref e.State, Free);
        }
    }

    private static bool IsOver(ChunkBasedSegment<PersistentStore> segment, ManagedPagedMMF mmf) => segment != null && segment.Store.Mmf == mmf;

    private Entry Acquire(Entry oldestFree, ChunkBasedSegment<PersistentStore> segment)
    {
        if (_count < TrimAbove)
        {
            var entry = new Entry { Pooled = true, State = Rented, ThreadId = _threadId };
            if (_count == _entries.Length)
            {
                var grown = new Entry[_count * 2];
                Array.Copy(_entries, grown, _count);
                _entries = grown;
            }

            // Release reads these from another thread and must see the entry's State = Rented, never a default Free. Plain stores do that: storing the
            // reference publishes the fields behind it (.NET memory model), and a count seen ahead of its slot only makes Release skip the slot.
            _entries[_count] = entry;
            _count++;
            return Fill(entry, segment);
        }

        if (oldestFree != null && TryClaim(oldestFree))
        {
            // Releases the old window's pins first. Dispose skips the refcounts when that segment's page cache is already gone (a disposed engine).
            oldestFree.Accessor.Dispose();
            return Fill(oldestFree, segment);
        }

        // Every pooled entry is rented — nesting past TrimAbove, or rents never handed back. This entry serves the one query and is dropped on return.
        return Fill(new Entry { Pooled = false, State = Rented, ThreadId = _threadId }, segment);
    }

    private static Entry Fill(Entry entry, ChunkBasedSegment<PersistentStore> segment)
    {
        entry.Accessor = segment.CreateChunkAccessor();
        entry.Segment = segment;
        return entry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryClaim(Entry entry) => Interlocked.CompareExchange(ref entry.State, Rented, Free) == Free;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entry Take(Entry entry, out int token)
    {
        entry.LastRent = ++_clock;
        token = ++entry.Token;
        return entry;
    }
}
