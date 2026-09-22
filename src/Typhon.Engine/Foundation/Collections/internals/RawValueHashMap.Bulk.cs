using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Applies one staged entry to the value bytes the map holds for its key, for <c>UpdateValuesBulk</c> (#872 step 7).
/// </summary>
/// <remarks>
/// <para>
/// <b>A struct type parameter, never an interface instance.</b> The JIT monomorphises the bulk loop on it, so <see cref="KeyOf"/> and <see cref="Update"/>
/// inline and nothing dispatches per entry — the same reason <c>ILeafApplier</c> and <see cref="IRawValueUpdater"/> are shaped this way. An interface
/// reference would put a virtual call on the innermost loop of the term §5.4 expects to dominate the re-clustering budget.
/// </para>
/// <para>
/// The entry carries its own parameters, which is the difference from <see cref="IRawValueUpdater"/>: that one is constructed per call with the new values
/// as fields, whereas one bulk applier serves a whole batch and reads what it needs out of each entry.
/// </para>
/// </remarks>
internal unsafe interface IRawBulkUpdater<TKey, TEntry>
    where TKey : unmanaged, IEquatable<TKey>
    where TEntry : struct
{
    /// <summary>The map key this entry addresses.</summary>
    TKey KeyOf(in TEntry entry);

    /// <summary>
    /// The bucket index the caller sorted this entry by — what <see cref="RawValuePagedHashMap{TKey,TStore}.BucketIndexOf"/> returned when the batch was
    /// built.
    /// </summary>
    /// <remarks>
    /// <b>The batch is sorted by a value the caller already holds, so making the map re-derive it is pure waste.</b> Without this the bulk loop hashes every
    /// key a second time purely to find where one bucket's run ends — one <c>XxHash32</c> per entity on the innermost path, undoing part of the amortisation
    /// the sort was for. Debug builds check the value against the map's own resolve, so trusting the caller costs nothing in confidence.
    /// </remarks>
    int BucketOf(in TEntry entry);

    /// <summary>Mutates the value bytes in place. Must not change the entry's size or position — see <c>UpdateValuesBulk</c>'s hint-cache note.</summary>
    /// <remarks>
    /// The entry is passed by <c>ref</c> so the applier can write per-entry RESULTS back into the caller's own batch — whether the key was found, and
    /// anything read out of the record under the bucket's write lock. Migration needs exactly that: it folds the migrated entity's <c>BornTSN</c> and
    /// <c>DiedTSN</c> into the destination cluster's visibility summary, and those must be read at the moment the record is patched rather than looked up
    /// again afterwards.
    /// </remarks>
    void Update(ref TEntry entry, byte* valueBytes);
}

unsafe partial class RawValuePagedHashMap<TKey, TStore>
    where TKey : unmanaged, IEquatable<TKey>
    where TStore : struct, IPageStore
{
    /// <summary>
    /// Live bucket count — with a batch size, this is what predicts whether a bulk apply will find any runs to amortise over.
    /// </summary>
    /// <remarks>
    /// The whole gain of <see cref="UpdateValuesBulk{TEntry,TUpdater}"/> is paid for by entries that SHARE a bucket. Expected entries per touched bucket is
    /// <c>batchSize / bucketCount</c>, so a batch much smaller than the bucket count produces runs of one and amortises nothing — while still paying the
    /// staging, the sort, the merge and the partition. Callers that can choose need this number to make that call.
    /// </remarks>
    internal int LiveBucketCount
    {
        get
        {
            var (_, _, bucketCount) = UnpackMeta(PackedMeta);
            return bucketCount;
        }
    }

    /// <summary>
    /// The bucket index <paramref name="key"/> currently resolves to — the sort key for <see cref="UpdateValuesBulk{TEntry,TUpdater}"/>.
    /// </summary>
    /// <remarks>
    /// Exposed because the caller has to sort by it and the resolve is the map's business, not the caller's: <c>ResolveBucket</c> picks a finer modulus for
    /// buckets already split this round, so "hash mod bucketCount" is not the same function and would order the batch wrongly for exactly the buckets that
    /// have moved. Reads <c>PackedMeta</c> per call, which is what makes the ordering only as stable as the absence of concurrent inserts — the contract
    /// <see cref="UpdateValuesBulk{TEntry,TUpdater}"/> states.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int BucketIndexOf(TKey key)
    {
        var (level, next, _) = UnpackMeta(PackedMeta);
        return ResolveBucket(ComputeHash(key), level, next, N0);
    }

    /// <summary>
    /// Splits a bucket-sorted batch into parts that own disjoint buckets: split by count, then advance each cut to the next bucket change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The B+Tree twin needs a descent; this does not.</b> <c>PartitionByLeafBoundaries</c> has to descend the tree to discover where a leaf's key range
    /// ends, because a leaf edge is not visible in the batch. Here the bucket index <i>is</i> the sort key, so the boundary is simply where it changes — two
    /// hashes per cut, no traversal. Parts therefore own disjoint buckets by construction, which is the half of <c>AC-7.3</c> that a later check could only
    /// discover rather than guarantee.
    /// </para>
    /// <para>
    /// <b>A bucket is never split across parts, so a clustered batch balances badly and is meant to.</b> Migration's keys are dense in few buckets; the
    /// alternative — splitting a bucket's run across workers — would put two workers in one bucket chunk, which is precisely what the exclusivity argument
    /// forbids. Imbalance is reported by the benchmark rather than tuned away.
    /// </para>
    /// </remarks>
    /// <returns>The number of parts written; part <c>p</c> is entries <c>[boundaries[p], boundaries[p + 1])</c>.</returns>
    internal int PartitionByBucketRuns<TEntry, TUpdater>(ReadOnlySpan<TEntry> sortedByBucket, int desiredParts, Span<int> boundaries)
        where TEntry : struct
        where TUpdater : struct, IRawBulkUpdater<TKey, TEntry>
    {
        // `desiredParts` is validated as well as `boundaries`: at zero or below it slips past a room check phrased in terms of `desiredParts + 1` and then
        // writes boundaries[1] anyway, which is out of range for a one-element span and answers "1 part" to a request for none.
        if (desiredParts <= 0)
        {
            ThrowHelper.ThrowInvalidOp($"PartitionByBucketRuns needs at least one part, got {desiredParts}.");
        }

        if (boundaries.Length < desiredParts + 1)
        {
            ThrowHelper.ThrowInvalidOp(
                $"PartitionByBucketRuns needs room for {desiredParts + 1} offsets ({desiredParts} part starts plus the trailing end) but was given "
                + $"{boundaries.Length}.");
        }

        boundaries[0] = 0;
        if (sortedByBucket.Length == 0)
        {
            return 0;
        }

        if (desiredParts <= 1)
        {
            boundaries[1] = sortedByBucket.Length;
            return 1;
        }

        TUpdater applier = default;
        AssertSortedByBucket<TEntry, TUpdater>(sortedByBucket);

        var parts = 1;
        var lastBoundary = 0;
        for (var p = 1; p < desiredParts; p++)
        {
            // From the ORIGINAL length rather than from what is left, so one long run does not drag every later boundary with it.
            var want = (int)((long)sortedByBucket.Length * p / desiredParts);
            if (want <= lastBoundary)
            {
                continue;   // a previous run already reached past this nominal cut
            }

            var cut = want;
            var previous = applier.BucketOf(sortedByBucket[cut - 1]);
            while (cut < sortedByBucket.Length && applier.BucketOf(sortedByBucket[cut]) == previous)
            {
                cut++;
            }

            if (cut >= sortedByBucket.Length)
            {
                break;      // the run consumed the tail; an empty part would only cost a dispatch
            }

            boundaries[parts++] = cut;
            lastBoundary = cut;
        }

        boundaries[parts] = sortedByBucket.Length;
        return parts;
    }

    /// <summary>
    /// Applies a bucket-sorted batch, amortising the hash, the bucket resolve, the directory lookup, the dirty-mark and the latch pair across each bucket's
    /// run of entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this actually saves, stated precisely.</b> <see cref="TryUpdateInPlace{TUpdater}"/> is already minimal in what it WRITES — the 5 bytes that
    /// change — and §5.4's point is that it is not minimal in what it PAYS to get there: a hash, a <c>PackedMeta</c> read, a <c>ResolveBucket</c>, a
    /// directory lookup, a <c>GetChunkAddress(_, true)</c> dirty-mark and an OLC lock/unlock pair, all per entity. Sorting by bucket collapses each of those
    /// to once per bucket, and the chain is walked once per bucket instead of once per entry.
    /// </para>
    /// <para>
    /// <b>The inner probe is linear in the run, and that is not the win.</b> Matching a chain entry against the run costs O(R), so the scan itself is
    /// O(chainLength × R) — the same as R separate chain walks. The gain is the fixed per-entity overhead above it, which is why <c>AC-7.5</c> asks for
    /// ns/entity as an OUTPUT rather than setting a speedup threshold: if the chain walk dominates, sorting buys little and the honest answer is the number.
    /// R is bounded by the bucket's entry count and is 1-2 in the shapes migration produces.
    /// </para>
    /// <para>
    /// <b>Concurrent inserts invalidate the batch, so they throw rather than corrupt.</b> The caller sorted using bucket indices read from a particular
    /// <c>PackedMeta</c>; a split changes the modulus for buckets past <c>next</c>, so the same key resolves elsewhere and the batch is no longer sorted by
    /// anything. <see cref="TryUpdateInPlace{TUpdater}"/> can simply retry because it holds one key; this cannot, because it does not own the buffer it was
    /// handed. Inside the tick fence's exclusive window (<c>EW-01</c>) no insert can run, which is where this is meant to be called; outside one, a loud
    /// failure is the only correct answer.
    /// </para>
    /// <para>
    /// <b>The OLC write lock is kept, deliberately.</b> §5.5 licenses shedding it inside the window, and a later step that guarantees the window may. Step 7
    /// ships this un-wired, so it has to be correct when called outside one — and the benchmark reports the latch's cost, so that decision gets a number
    /// rather than an argument.
    /// </para>
    /// <para>
    /// <b>Positions do not move, which is what keeps the location-hint cache valid.</b> A hint is a packed <c>(chunkId, index)</c>; this writes value bytes
    /// only, never touching <c>EntryCount</c>, key order or chunk membership, so every hint that was correct before is correct after. <c>AC-7.2</c> asserts
    /// that rather than trusting this paragraph.
    /// </para>
    /// </remarks>
    /// <returns>How many entries were found and updated. A batch key absent from the map is skipped, not an error — the same forgiving semantics
    /// <see cref="TryUpdateInPlace{TUpdater}"/> has, and the difference from <paramref name="sortedByBucket"/>'s length is how a caller detects it.</returns>
    public int UpdateValuesBulk<TEntry, TUpdater>(Span<TEntry> sortedByBucket, ref ChunkAccessor<TStore> accessor)
        where TEntry : struct
        where TUpdater : struct, IRawBulkUpdater<TKey, TEntry>
    {
        _fenceWindow?.NoteMutation("EntityMap.UpdateValuesBulk");

        if (sortedByBucket.Length == 0)
        {
            return 0;
        }

        TUpdater applier = default;
        var packed = PackedMeta;

        AssertSortedByBucket<TEntry, TUpdater>(sortedByBucket);

        var updated = 0;
        var runStart = 0;

        while (runStart < sortedByBucket.Length)
        {
            var bucket = applier.BucketOf(sortedByBucket[runStart]);
            var runEnd = runStart + 1;
            while (runEnd < sortedByBucket.Length && applier.BucketOf(sortedByBucket[runEnd]) == bucket)
            {
                runEnd++;
            }

            updated += ApplyBucketRun<TEntry, TUpdater>(sortedByBucket[runStart..runEnd], bucket, packed, ref applier, ref accessor);
            runStart = runEnd;
        }

        return updated;
    }

    /// <summary>Applies one bucket's run under a single lock and a single chain walk.</summary>
    private int ApplyBucketRun<TEntry, TUpdater>(Span<TEntry> run, int bucket, long packed, ref TUpdater applier, ref ChunkAccessor<TStore> accessor)
        where TEntry : struct
        where TUpdater : struct, IRawBulkUpdater<TKey, TEntry>
    {
        while (true)
        {
            if (PackedMeta != packed)
            {
                ThrowHelper.ThrowInvalidOp(
                    "UpdateValuesBulk saw the map resize mid-batch. The batch was sorted by bucket indices read before the split, so those indices no longer "
                    + "name the buckets the keys resolve to and applying them would write the wrong chains. Call this inside the tick fence's exclusive "
                    + "window (EW-01), where no insert can run, or re-sort and retry.");
            }

            var rootChunkId = GetBucketChunkId(bucket, ref accessor);
            var rootAddr = accessor.GetChunkAddress(rootChunkId, true);
            ref var header = ref GetHeader(rootAddr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (PackedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            var updated = 0;
            try
            {
                var chunkId = rootChunkId;
                while (chunkId != -1)
                {
                    // Re-fetched rather than reused across the walk: GetChunkAddress can evict and reload pages, so a pointer taken before the previous hop
                    // may no longer address this chunk. UpdateInChainCallback has the same discipline.
                    var addr = accessor.GetChunkAddress(chunkId, true);
                    ref readonly var chainHeader = ref GetHeader(addr);
                    var keys = KeysPtr(addr);
                    var count = chainHeader.EntryCount;

                    // No break on a match, and no early exit once the run is exhausted. A batch may legitimately carry the same key twice, and a
                    // TryUpdateInPlace loop over it would apply both in order and report two successes; stopping at the first match would silently make it
                    // FIRST-wins and drop the second, which is precisely the identity AC-7.1 asserts. The cost is O(R) per chain entry either way.
                    for (var e = 0; e < count; e++)
                    {
                        var key = keys[e];
                        for (var r = 0; r < run.Length; r++)
                        {
                            if (applier.KeyOf(run[r]).Equals(key))
                            {
                                applier.Update(ref run[r], ValueAt(addr, e));
                                updated++;
                            }
                        }
                    }

                    chunkId = chainHeader.OverflowChunkId;
                }
            }
            finally
            {
                // Unlocked in a `finally`, and through a latch RE-RESOLVED from the chunk id rather than through the local taken before the walk. Both halves
                // are borrowed from TryUpdateValueAt, which paid for them: a user-supplied applier can throw, and a bucket left write-locked is permanent —
                // ReadVersion returns 0 for every reader and TryWriteLock refuses every writer, with no retry able to clear it. The re-resolve is because
                // GetChunkAddress in the walk can evict the root's page and reuse the slot, leaving the original `ref` addressing something else. A bulk call
                // walks far more chunks than the per-entity path, so the window this closes is correspondingly wider.
                new OlcLatch(ref GetHeader(accessor.GetChunkAddress(rootChunkId, true)).OlcVersion).WriteUnlock();
            }

            return updated;
        }
    }

    /// <summary>
    /// Asserts the batch really is ascending by bucket index, which every part of this file's disjointness and amortisation argument assumes.
    /// </summary>
    /// <remarks>
    /// Debug-only, and for the same reason the erased B+Tree surface's stride check is: the batch is assembled by engine code from
    /// <see cref="BucketIndexOf"/>, there is no user-input path, and an unsorted batch is a programming error a Debug suite run catches. It is worth
    /// asserting at all because the failure is silent — an unsorted batch still updates every key it finds, just with none of the amortisation and with parts
    /// that no longer own disjoint buckets.
    /// </remarks>
    [Conditional("DEBUG")]
    private void AssertSortedByBucket<TEntry, TUpdater>(ReadOnlySpan<TEntry> entries)
        where TEntry : struct
        where TUpdater : struct, IRawBulkUpdater<TKey, TEntry>
    {
        TUpdater applier = default;
        for (var i = 0; i < entries.Length; i++)
        {
            // The caller's cached bucket is CHECKED against the map's own resolve, not merely trusted. A wrong one is silent otherwise: run boundaries move,
            // the apply latches a bucket the key is not in and finds nothing, and the only symptom is a return count the caller may never look at.
            var carried = applier.BucketOf(entries[i]);
            var resolved = BucketIndexOf(applier.KeyOf(entries[i]));
            if (carried != resolved || (i > 0 && carried < applier.BucketOf(entries[i - 1])))
            {
                FailBatchOrder(i, carried, resolved);
            }
        }
    }

    /// <summary>Builds the failure message only once the check has already failed.</summary>
    /// <remarks>
    /// <para>
    /// <b>Separated from the check because <c>Debug.Assert</c>'s message is not always lazy.</b> The single-interpolated-string overload binds to
    /// <c>AssertInterpolatedStringHandler</c> and formats nothing while the condition holds — but CONCATENATING an interpolated string with anything else
    /// produces an ordinary <see cref="string"/> first, so the handler never applies and every call allocates. That is what this looked like when written
    /// inline: <c>AC-7.4</c>'s zero-allocation test reported 575 152 bytes for a 1 000-entry batch, on a path that runs once per migrant per tick.
    /// </para>
    /// <para>
    /// <b>Throws rather than <c>Debug.Fail</c>s</b>, per the reasoning <c>TickContext</c> records: <c>Fail</c> terminates the process uncatchably, so in
    /// Debug — the configuration the whole suite runs in — a single bad batch would abort the test host and lose every fixture with no attribution, instead
    /// of producing one red test naming the entry. The throw is still Debug-only: the sole caller is <c>[Conditional("DEBUG")]</c>.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailBatchOrder(int index, int carried, int resolved)
        => ThrowHelper.ThrowInvalidOp(
            $"UpdateValuesBulk batch is bad at entry {index}: it carries bucket {carried}, its key resolves to {resolved}, and the batch must be "
            + "non-decreasing by bucket. Either it was built against a different map state, BucketOf does not return what BucketIndexOf returned, or it "
            + "was never sorted.");
}
