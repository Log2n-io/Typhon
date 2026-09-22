using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// One migrant's EntityMap location patch, staged by the Migrate phase and applied by the EntityMapUpdate phase (#872 step 7, §5.4).
/// </summary>
/// <remarks>
/// <b>It carries results as well as inputs</b>, which is why <c>IRawBulkUpdater.Update</c> takes the entry by <c>ref</c>. The migration path does not merely
/// write the record: it needs the entity's own <c>BornTSN</c> and <c>DiedTSN</c>, read at the moment the record is patched and under the same bucket write
/// lock, to fold into the destination cluster's H1 visibility summary. Looking them up afterwards would be a second chain walk per migrant — the cost this
/// whole step exists to remove.
/// </remarks>
internal struct EntityLocationUpdate
{
    /// <summary>EntityKey — the 52-bit top half of the raw id, which is what the map is keyed by. NOT the full RawValue stored in cluster slots.</summary>
    public long EntityKey;

    /// <summary>The bucket <see cref="EntityKey"/> resolved to when the batch was staged, and the key the batch is sorted by.</summary>
    public int Bucket;

    public int DstChunkId;
    public int DstSlot;

    /// <summary>Set by the apply: the entity's own BornTSN, read under the bucket write lock.</summary>
    public long ObservedBornTsn;

    /// <summary>Set by the apply: the entity's DiedTSN, 0 when alive.</summary>
    public long ObservedDiedTsn;

    /// <summary>
    /// Set by the apply. False means the key was not in the map — the entity was destroyed in flight and the destination slot holds an orphan.
    /// </summary>
    public bool Found;
}

/// <summary>
/// Patches a <c>ClusterEntityRecord</c>'s location and reads its TSNs back out, for the bulk EntityMap update.
/// </summary>
/// <remarks>
/// The bulk twin of <c>ExecuteMigrations</c>'s old <c>ClusterLocationUpdater</c>, and it does the same four things: read <c>BornTSN</c> and <c>DiedTSN</c>
/// under the bucket's write lock, then overwrite the 4-byte ClusterChunkId and 1-byte SlotIndex without rewriting the rest of the record. The difference is
/// where the results go — into the caller's own entry rather than into a per-call struct — because one applier now serves a whole batch.
/// </remarks>
internal unsafe struct ClusterLocationBulkUpdater : IRawBulkUpdater<long, EntityLocationUpdate>
{
    public long KeyOf(in EntityLocationUpdate entry) => entry.EntityKey;

    public int BucketOf(in EntityLocationUpdate entry) => entry.Bucket;

    public void Update(ref EntityLocationUpdate entry, byte* valueBytes)
    {
        ref var header = ref ClusterEntityRecordAccessor.GetHeader(valueBytes);
        entry.ObservedBornTsn = header.BornTSN;
        entry.ObservedDiedTsn = header.DiedTSN;
        ClusterEntityRecordAccessor.SetClusterChunkId(valueBytes, entry.DstChunkId);
        ClusterEntityRecordAccessor.SetSlotIndex(valueBytes, (byte)entry.DstSlot);
        entry.Found = true;
    }
}

/// <summary>
/// Per-archetype staging for the tick fence's EntityMapUpdate phase: where the Migrate phase writes the location patches it used to apply inline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately a near-twin of <see cref="IndexUpdateStaging"/> rather than a shared generic.</b> The shapes rhyme — per-chunk buffers sized in
/// <c>Prepare</c>, sorted by the worker that filled them, merged and partitioned by the consuming phase — but what they hold does not. That one stages raw
/// bytes at a stride only the tree knows, because a key is 4, 8 or 64 bytes; this one stages a fixed struct with an <c>int</c> sort key. Unifying them would
/// mean giving this one an erased byte surface it has no use for, to save a merge loop that is twenty lines.
/// </para>
/// <para>
/// <b>Exists for every cluster-eligible archetype, indexed or not.</b> Unlike the index staging, which is built inside <c>InitializeIndexes</c> and is
/// therefore absent when an archetype has no indexed fields, every migrant needs its EntityMap entry repointed.
/// </para>
/// </remarks>
internal sealed class EntityMapUpdateStaging
{
    /// <summary>
    /// Default minimum expected entries per bucket before migration stages for the bulk phase instead of applying inline. See
    /// <c>RuntimeOptions.EntityMapBulkMinEntriesPerBucket</c>, which carries the measurement.
    /// </summary>
    /// <remarks>
    /// Lives here so the runtime option's default and the serial fence's fallback are ONE value. They were two, kept equal by a comment asking a human to
    /// remember — which is exactly how a hosted fence and a runtime-less one drift into different behaviour with nothing to catch it.
    /// </remarks>
    internal const float DefaultMinEntriesPerBucket = 1.0f;

    private const int InitialCapacity = 256;

    /// <summary>
    /// Counters are one per cache line. Each is written once per migrant from the Migrate worker that owns the chunk, so packing them would put sixteen
    /// concurrently-written counters on one line and ping-pong it for the whole phase — the same defect the index staging's padded counters exist to avoid.
    /// </summary>
    private const int CountersPerLine = 64 / sizeof(int);

    private EntityLocationUpdate[][] _buffers = [];

    // Per-chunk ping-pong partner for the radix sort, grown geometrically so the per-tick sort settles to zero allocation rather than reallocating whenever
    // a run exceeds the largest one that chunk has seen.
    private EntityLocationUpdate[][] _sortScratch = [];
    private int[] _counts = [];
    private int _chunkCapacity;

    // The merged, sorted batch, plus the ping-pong partner the pairwise merge alternates with. Reused tick over tick.
    private EntityLocationUpdate[] _merged = [];
    private EntityLocationUpdate[] _scratch = [];

    // Run boundaries within the ping-pong buffers, in ENTRIES.
    private int[] _runOffsets = new int[16];
    private int[] _runLengths = new int[16];
    private int[] _nextOffsets = new int[16];
    private int[] _nextLengths = new int[16];

    private int[] _partBoundaries = new int[16];

    /// <summary>This tick's merged, sorted batch. Valid only between the phase Prepare that set it and the next <see cref="BeginTick"/>.</summary>
    internal EntityLocationUpdate[] Prepared => _merged;

    /// <summary>Entries in <see cref="Prepared"/> this tick.</summary>
    internal int PreparedCount { get; private set; }

    /// <summary>Parts the batch was split into, or zero when nothing was staged.</summary>
    internal int PartCount { get; private set; }

    /// <summary>Entry-index boundaries of the parts: part <c>p</c> is <c>[Boundaries[p], Boundaries[p + 1])</c>.</summary>
    internal int[] Boundaries => _partBoundaries;

    /// <summary>
    /// Sizes the per-chunk buffers for this tick and clears their fill levels. Called from the Migrate phase's single-threaded <c>Prepare</c>.
    /// </summary>
    internal void BeginTick(int chunkCount)
    {
        var chunks = Math.Max(chunkCount, 1);
        if (_buffers.Length < chunks)
        {
            var grownBuffers = new EntityLocationUpdate[Math.Max(chunks, _buffers.Length * 2)][];
            Array.Copy(_buffers, grownBuffers, _buffers.Length);
            _buffers = grownBuffers;

            var grownScratch = new EntityLocationUpdate[grownBuffers.Length][];
            Array.Copy(_sortScratch, grownScratch, _sortScratch.Length);
            _sortScratch = grownScratch;

            var grownCounts = new int[grownBuffers.Length * CountersPerLine];
            Array.Copy(_counts, grownCounts, _counts.Length);
            _counts = grownCounts;
        }

        for (var c = 0; c < chunks; c++)
        {
            _buffers[c] ??= new EntityLocationUpdate[InitialCapacity];
            _counts[c * CountersPerLine] = 0;
        }

        _chunkCapacity = chunks;
        PartCount = 0;
        PreparedCount = 0;
    }

    /// <summary>Appends one migrant's patch to the calling worker's chunk buffer.</summary>
    /// <remarks>A chunk's buffer is owned exclusively by the worker running that chunk, so this needs no lock and no interlocked reservation.</remarks>
    internal void Add(int chunkIndex, in EntityLocationUpdate update)
    {
        // Bound-checked like ChunkSpan and SortChunk, not trusted like the callers assume. Past capacity the alternatives are an NRE on a slot BeginTick never
        // initialised, or — worse — a counter no later pass sums, which silently drops the migrant and leaves it with a stale EntityMap record.
        if ((uint)chunkIndex >= (uint)_chunkCapacity)
        {
            ThrowHelper.ThrowInvalidOp($"EntityMapUpdateStaging.Add: chunk {chunkIndex} is outside this tick's capacity of {_chunkCapacity}.");
        }

        var slot = chunkIndex * CountersPerLine;
        var buffer = _buffers[chunkIndex];
        var count = _counts[slot];

        if (count == buffer.Length)
        {
            var grown = new EntityLocationUpdate[buffer.Length * 2];
            Array.Copy(buffer, grown, count);
            _buffers[chunkIndex] = grown;
            buffer = grown;
        }

        buffer[count] = update;
        _counts[slot] = count + 1;
    }

    /// <summary>One chunk's staged entries — what the worker that produced them sorts before it leaves the chunk.</summary>
    internal Span<EntityLocationUpdate> ChunkSpan(int chunkIndex)
        => chunkIndex < _chunkCapacity ? _buffers[chunkIndex].AsSpan(0, _counts[chunkIndex * CountersPerLine]) : Span<EntityLocationUpdate>.Empty;

    /// <summary>Sorts one chunk's run by bucket, on the Migrate worker that filled it.</summary>
    /// <remarks>
    /// <para>
    /// <b>This is the sort that must not happen in the consuming phase's Prepare.</b> Step 6 measured the alternative on the index side: sorting the whole
    /// batch single-threaded left that phase 88 % serial and capped it at ~1.15x however many workers it was given. Sorting per chunk rides a parallel region
    /// that is already open and leaves the phase an <c>O(n log W)</c> merge.
    /// </para>
    /// <para>
    /// <b>Not the insertion sort this started as.</b> "The run is short" is true only when the chunk count is high: at one Migrate chunk the run is the WHOLE
    /// batch, and an <c>O(n²)</c> sort of 95 000 entries cost <b>455 ms</b> of Migrate CPU against 63 ms for the inline path it was meant to beat. The
    /// quadratic term is invisible at W = 16 and ruinous at W = 1, which is exactly the shape a microbenchmark on a small run would have missed. A key-array
    /// <c>Array.Sort</c> brought that to 29.8 ms.
    /// </para>
    /// <para>
    /// <b>Stability is not required here, unlike in the merge.</b> An entity migrates at most once per tick, so no two entries in the batch share a key and
    /// there is nothing for a tie-break to reorder. <see cref="MergeTwo"/> stays stable anyway, because it is what makes the merged batch a pure function of
    /// the runs rather than of the chunk count.
    /// </para>
    /// <para>
    /// <b><see cref="RadixSort"/> since #891.</b> The bucket index is below <c>LiveBucketCount</c>, so two or three digit passes cover it, each moving the
    /// 48-byte entry once, where introsort moved it ~<c>log n</c> times. In isolation 1.5x at chunk-run sizes and 4x on a whole batch
    /// (<c>RadixSortBenchmarks</c>); in situ at W = 8 the runs are ~50–150 entries and the two were within ±10 µs of CPU a tick of each other, so this
    /// conversion rests on the whole-batch case — the serial fence and W = 1, where the run IS the batch.
    /// </para>
    /// </remarks>
    /// <param name="chunkIndex">The chunk whose run to sort.</param>
    /// <param name="radixCounts">The calling worker's histogram scratch, <see cref="RadixSort.Buckets"/> ints. Owned by the exec system per chunk.</param>
    internal void SortChunk(int chunkIndex, Span<int> radixCounts)
    {
        if (chunkIndex >= _chunkCapacity)
        {
            return;
        }

        var count = _counts[chunkIndex * CountersPerLine];
        if (count < 2)
        {
            return;
        }

        // Geometric for the same reason as EnsureCapacity: a run that grows by one entry a tick must not reallocate this chunk's scratch every tick.
        var scratch = _sortScratch[chunkIndex];
        if (scratch == null || scratch.Length < count)
        {
            scratch = new EntityLocationUpdate[Math.Max(Math.Max(count, (scratch?.Length ?? 0) * 2), InitialCapacity)];
            _sortScratch[chunkIndex] = scratch;
        }

        RadixSort.Sort<EntityLocationUpdate, BucketKeyOf>(_buffers[chunkIndex].AsSpan(0, count), scratch, radixCounts);
    }

    /// <summary>The bucket sort's key: the bucket index, sign-flipped so the radix order is <see cref="int"/> order.</summary>
    private readonly struct BucketKeyOf : IRadixKey<EntityLocationUpdate>
    {
        public static ulong Key(in EntityLocationUpdate item) => RadixSort.SignedKey(item.Bucket);
    }

    /// <summary>Total entries staged this tick.</summary>
    internal int StagedCount()
    {
        var total = 0;
        for (var c = 0; c < _chunkCapacity; c++)
        {
            total += _counts[c * CountersPerLine];
        }

        return total;
    }

    /// <summary>
    /// Merges the per-chunk runs — each already bucket-sorted by the worker that produced it — into one sorted batch, and records how it was partitioned.
    /// </summary>
    /// <remarks>
    /// Pairwise linear passes rather than a W-way heap: same asymptotic cost, a much smaller constant, and no per-element data structure. The run count is the
    /// number of Migrate CHUNKS that staged anything, which the planner sizes from cost rather than from the worker count, so this term grows with the
    /// workload — it is where the phase's remaining serial cost lives.
    /// </remarks>
    internal int MergeAndPartition(int desiredParts)
    {
        PreparedCount = 0;
        PartCount = 0;

        var total = StagedCount();
        if (total == 0)
        {
            return 0;
        }

        EnsureCapacity(ref _merged, total);
        EnsureRunArrays(_chunkCapacity);

        // Pass 0 is the gather: lay the non-empty runs out back to back, recording where each begins.
        var dst = _merged;
        var runCount = 0;
        var offset = 0;
        for (var c = 0; c < _chunkCapacity; c++)
        {
            var n = _counts[c * CountersPerLine];
            if (n == 0)
            {
                continue;
            }

            Array.Copy(_buffers[c], 0, dst, offset, n);
            _runOffsets[runCount] = offset;
            _runLengths[runCount] = n;
            runCount++;
            offset += n;
        }

        var src = dst;
        if (runCount > 1)
        {
            EnsureCapacity(ref _scratch, total);
            var other = _scratch;

            while (runCount > 1)
            {
                var outCount = 0;
                var outOffset = 0;
                for (var r = 0; r < runCount; r += 2)
                {
                    if (r + 1 == runCount)
                    {
                        // Odd run out: carry it across unchanged so the next pass sees it as one run.
                        Array.Copy(src, _runOffsets[r], other, outOffset, _runLengths[r]);
                        _nextOffsets[outCount] = outOffset;
                        _nextLengths[outCount] = _runLengths[r];
                        outCount++;
                        break;
                    }

                    MergeTwo(src, _runOffsets[r], _runLengths[r], _runOffsets[r + 1], _runLengths[r + 1], other, outOffset);
                    _nextOffsets[outCount] = outOffset;
                    _nextLengths[outCount] = _runLengths[r] + _runLengths[r + 1];
                    outOffset += _nextLengths[outCount];
                    outCount++;
                }

                (src, other) = (other, src);
                (_runOffsets, _nextOffsets) = (_nextOffsets, _runOffsets);
                (_runLengths, _nextLengths) = (_nextLengths, _runLengths);
                runCount = outCount;
            }

            // Publish whichever side of the ping-pong holds the result by swapping references, not by copying it back.
            _merged = src;
            _scratch = other;
        }

        PreparedCount = total;

        var parts = Math.Max(1, desiredParts);
        if (_partBoundaries.Length < parts + 1)
        {
            _partBoundaries = new int[parts + 1];
        }

        return total;
    }

    /// <summary>Records the partition the map computed over <see cref="Prepared"/>.</summary>
    internal void SetPartCount(int parts) => PartCount = parts;

    /// <summary>Forgets this tick's prepared state so an empty next tick cannot re-dispatch it.</summary>
    internal void ClearPrepared()
    {
        PartCount = 0;
        PreparedCount = 0;
    }

    /// <summary>Stable two-way merge on the bucket index — taking from the left run on a tie is what keeps the batch independent of the chunk count.</summary>
    private static void MergeTwo(EntityLocationUpdate[] source, int aStart, int aLength, int bStart, int bLength, EntityLocationUpdate[] destination,
        int destinationStart)
    {
        var i = aStart;
        var j = bStart;
        var k = destinationStart;
        var aEnd = aStart + aLength;
        var bEnd = bStart + bLength;

        while (i < aEnd && j < bEnd)
        {
            destination[k++] = source[i].Bucket <= source[j].Bucket ? source[i++] : source[j++];
        }

        if (i < aEnd)
        {
            Array.Copy(source, i, destination, k, aEnd - i);
        }
        else if (j < bEnd)
        {
            Array.Copy(source, j, destination, k, bEnd - j);
        }
    }

    /// <summary>Grows geometrically, like every sibling buffer in this type.</summary>
    /// <remarks>
    /// Sizing to exactly <c>needed</c> would reallocate BOTH ping-pong buffers on any tick whose batch is one entry larger than the last — at the design's
    /// 58 000 migrants that is two ~2.3 MB LOH allocations per tick, on the phase whose entire purpose is removing per-entity cost.
    /// </remarks>
    private static void EnsureCapacity(ref EntityLocationUpdate[] buffer, int needed)
    {
        if (buffer.Length < needed)
        {
            buffer = new EntityLocationUpdate[Math.Max(Math.Max(needed, buffer.Length * 2), InitialCapacity)];
        }
    }

    private void EnsureRunArrays(int runs)
    {
        if (_runOffsets.Length >= runs + 1)
        {
            return;
        }

        var grown = Math.Max(runs + 1, _runOffsets.Length * 2);
        _runOffsets = new int[grown];
        _runLengths = new int[grown];
        _nextOffsets = new int[grown];
        _nextLengths = new int[grown];
    }
}
