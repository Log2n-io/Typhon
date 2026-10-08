// unset

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Abstract base class for hash maps. Provides meta management, bucket addressing, bucket resolution, split lock, and factory scaffolding.
/// Concrete class <see cref="PagedHashMap{TKey,TValue,TStore}"/> provides JIT-specialized hash functions via sizeof(TKey) branching.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bucket addressing is arithmetic (#1205).</b> Bucket <c>b</c> lives at chunk <c>b + 1</c> of the segment — chunk 0 is the meta — so a lookup touches
/// one chunk, the bucket's, at any size, and the map holds no directory. It used to: bucket chunk ids lived in directory chunks, 57 reached from the meta
/// and the rest through a linked list walked on every lookup, counted in 16 bits — O(buckets) per lookup, and a hard wall at 4 194 240 buckets.
/// </para>
/// <para>
/// <b>Overflow chunks live above the buckets.</b> They come from the segment's allocator, which an allocation floor keeps a runway above the bucket
/// frontier, and every overflow chunk records the bucket that owns it. A split takes the frontier chunk for the bucket it creates: free, it reserves it;
/// held by an overflow chunk allocated before the floor passed it, it moves that chunk elsewhere under its owner's latch; anything else (a chunk reserved
/// by a writer that has not linked it yet) and the split is skipped, for a later insert to retry. See <see cref="ClaimBucketChunk"/>.
/// </para>
/// <para>
/// <b>The hash state is the bucket count.</b> Level and split pointer derive from it, so nothing caps them below the bucket count's own limit,
/// <see cref="MaxBucketCount"/>: about 2³⁰ buckets, 3–7 billion entries depending on the entry size. Past it the map stops splitting and chains grow;
/// what finally refuses is the segment, whose allocator throws <see cref="ResourceExhaustedException"/> when its chunk ids run out.
/// </para>
/// </remarks>
internal abstract unsafe class PagedHashMapBase<TStore> where TStore : struct, IPageStore
{
    // ═══════════════════════════════════════════════════════════════════════
    // Fields
    // ═══════════════════════════════════════════════════════════════════════

    private readonly ChunkBasedSegment<TStore> _segment;

    /// <summary>Initial bucket count (power of 2). Immutable after construction.</summary>
    private readonly int _n0;

    /// <summary>log₂ of <see cref="_n0"/>.</summary>
    private readonly int _n0Shift;

    /// <summary>
    /// The hash state: the bucket count. Written with release semantics after a split has written the new bucket, read with acquire semantics before a
    /// bucket is resolved (<see cref="ReadMeta"/>), so a reader that resolves to the new bucket sees its content on x64 and arm64 alike. Compared for equality
    /// to detect a split between two reads.
    /// </summary>
    private long _packedMeta;

    /// <summary>Total entry count. Updated via <see cref="Interlocked"/>.</summary>
    protected long _entryCount;

    /// <summary>The entry count the meta chunk holds: what <see cref="FlushMeta"/> compares against, so a flush with nothing new writes nothing.</summary>
    private long _persistedEntryCount;

    // The bucket count last written to the meta chunk, by its one writer (the split-lock holder). Behind the live count only when a split's persist
    // faulted after it published: FlushMeta then catches it up, under the split lock.
    private long _persistedBucketCount;

    // After a split met a missing resource (ResourceExhausted, page-cache back-pressure): no split is tried again until the entry count reaches this, so
    // the writes past the threshold do not each retry it and pay for the failure again.
    private long _splitRetryAtEntries;

    /// <summary>CAS spin lock for split serialization: 0=free, 1=held.</summary>
    protected int SplitLock;

    /// <summary>Diagnostic: total splits performed.</summary>
    internal long _splitCount;

    // Insert-cost diagnostics (#1098). Both sit on paths that already allocate a chunk or rewrite a whole bucket chain, so one interlocked add beside them is
    // not measurable. They are the two numbers that decide whether a parallel bulk insert can partition this map by bucket: a chained overflow chunk is
    // bucket-local and therefore partition-safe, whereas a split rewrites the round-robin bucket `Next` — NOT the bucket being inserted into — plus the meta,
    // so it crosses every partition at once. Nothing counted either.

    /// <summary>Diagnostic: overflow chunks chained onto a full bucket. Bucket-local, so partition-safe.</summary>
    internal long _overflowChunksChained;

    /// <summary>Diagnostic: entries re-hashed and rewritten by <c>ExecuteSplit</c> — the O(batch) term in a bulk insert.</summary>
    internal long _splitEntriesRehashed;

    /// <summary>Diagnostic: overflow chunks a split moved out of the chunk its new bucket takes (<see cref="ClaimBucketChunk"/>).</summary>
    internal long _overflowChunksRelocated;

    /// <summary>Diagnostic: splits skipped because the new bucket's chunk was held by a writer that had not linked it yet.</summary>
    internal long _splitsDeferred;

    /// <summary>Diagnostic: split batches cut short by a resource that was not there — the segment at its chunk-id cap, the page cache full.</summary>
    internal long _splitsFailed;

    /// <summary>Called as the bucket count is persisted — in a split, after the count is published; a test throws from it. Test seam.</summary>
    internal Action PersistBucketCountProbe;

    /// <summary>
    /// The frontier chunk the last deferred split found held and linked to nothing, or 0. Until its holder links it (it then carries an owner tag, and can
    /// be moved) or frees it, a split can do nothing: an insert that finds the frontier still blocked skips the attempt — the split lock, the claim, and
    /// the back-pressure wait — instead of repeating them on every insert.
    /// </summary>
    private int _blockedFrontierChunk;

    /// <summary>Diagnostic: OLC read restarts due to version mismatch.</summary>
    internal long _olcRestarts;

    /// <summary>Diagnostic: write lock spin contention.</summary>
    internal long _writeLockFailures;

    /// <summary>Maximum load factor before triggering a split.</summary>
    private const double MaxLoadFactor = 0.75;

    /// <summary>
    /// The same threshold, readable by the derived bulk-insert path so its refusal is phrased against the number that actually gates a split (#1100).
    /// </summary>
    /// <remarks>
    /// A second literal would be the classic drift: raise the threshold here and a bulk insert would refuse batches the per-insert path accepts, or worse,
    /// accept batches that then split inside a region built on nothing splitting.
    /// </remarks>
    protected const double MaxLoadFactorForBulk = MaxLoadFactor;

    /// <summary>
    /// The most buckets a map holds: 2³⁰. Bucket <c>b</c> is chunk <c>b + 1</c>, an <c>int</c>, and the finer modulus a lookup resolves with,
    /// <c>2 · N0 · 2^level</c>, stays at most 2³⁰. At the cap a map stops splitting; inserts still succeed, into longer chains.
    /// </summary>
    internal const int MaxBucketCount = PagedHashMapMeta.MaxBucketCount;

    /// <summary>The bucket count this map stops splitting at: <see cref="MaxBucketCount"/>, lowered by a test to reach the cap without 2³⁰ buckets.</summary>
    protected int BucketCap { get; private set; } = MaxBucketCount;

    /// <summary>Lowers the split cap of this map. Test seam: what a map does at <see cref="MaxBucketCount"/>, provable on a small one.</summary>
    internal void SetBucketCapForTest(int cap) => BucketCap = Math.Clamp(cap, _n0, MaxBucketCount);

    /// <summary>Whether this hash map supports multiple values per key via VSBS buffer indirection.</summary>
    protected readonly bool _allowMultiple;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    protected PagedHashMapBase(ChunkBasedSegment<TStore> segment, int n0, bool allowMultiple = false)
    {
        Debug.Assert(segment != null);
        Debug.Assert(n0 > 0 && BitOperations.IsPow2(n0), "N0 must be a positive power of 2");
        Debug.Assert(segment.Stride >= 64 && BitOperations.IsPow2(segment.Stride), "LinearHash requires stride >= 64 and power of 2");

        _segment = segment;
        _n0 = n0;
        _n0Shift = BitOperations.Log2((uint)n0);
        _allowMultiple = allowMultiple;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Initial bucket count (immutable, power of 2).</summary>
    public int N0 => _n0;

    /// <summary>The backing segment.</summary>
    public ChunkBasedSegment<TStore> Segment => _segment;

    /// <summary>Whether this hash map supports multiple values per key via VSBS buffer indirection.</summary>
    public bool AllowMultiple => _allowMultiple;

    /// <summary>Total entries across all buckets.</summary>
    public long EntryCount => _entryCount;

    /// <summary>Current bucket count.</summary>
    public int BucketCount => (int)Volatile.Read(ref _packedMeta);

    /// <summary>Load factor: entries / (bucketCount × bucketCapacity).</summary>
    public double LoadFactor
    {
        get
        {
            long entries = _entryCount;
            return (double)entries / ((long)BucketCount * BucketCapacity);
        }
    }

    /// <summary>Number of entries a single bucket chunk can hold.</summary>
    public abstract int BucketCapacity { get; }

    // ═══════════════════════════════════════════════════════════════════════
    // Hash state — level and split pointer derive from the bucket count
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The linear-hashing state of <paramref name="bucketCount"/> buckets: <c>bucketCount = N0 · 2^level + next</c> with <c>0 ≤ next &lt; N0 · 2^level</c>.
    /// </summary>
    /// <remarks>
    /// The level and the split pointer used to be stored, packed beside the count in 8 and 24 bits; the 24-bit split pointer was masked without a check, so
    /// past 2²⁵ buckets it wrapped and lookups resolved to the wrong bucket. Both are functions of the count, which is now all the state holds (#1205).
    /// </remarks>
    internal static (int Level, int Next) HashStateOf(long bucketCount, int n0)
    {
        Debug.Assert(bucketCount >= n0 && BitOperations.IsPow2(n0));
        var level = BitOperations.Log2((ulong)bucketCount >> BitOperations.Log2((uint)n0));
        return (level, (int)(bucketCount - ((long)n0 << level)));
    }

    /// <summary>The hash state <paramref name="packed"/> (the bucket count, as read from <see cref="_packedMeta"/>) describes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected (int Level, int Next, int BucketCount) UnpackMeta(long packed)
    {
        var level = BitOperations.Log2((ulong)packed >> _n0Shift);
        return (level, (int)(packed - ((long)_n0 << level)), (int)packed);
    }

    /// <summary>
    /// Resolve a hash to a bucket index using bitmask arithmetic (no modulo).
    /// If the bucket has already been split this round (bucket &lt; next), the finer modulus is used.
    /// </summary>
    /// <remarks>
    /// The finer modulus is at most 2³⁰: below <see cref="MaxBucketCount"/> the base modulus is at most 2²⁹, and at it the split pointer is 0.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ResolveBucket(uint hash, int level, int next, int n0)
    {
        uint mod = (uint)n0 << level;                 // N0 × 2^Level (always power of 2)
        uint bucket = hash & (mod - 1);               // bitmask: 1 AND instruction

        if (bucket < (uint)next)
        {
            // This bucket already split this round — use finer modulus
            bucket = hash & ((mod << 1) - 1);
        }

        return (int)bucket;
    }

    /// <summary>
    /// Read the hash state, with acquire semantics: a reader that goes on to read the bucket it resolves sees what the split that created it wrote.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected (int Level, int Next, int BucketCount) ReadMeta() => UnpackMeta(Volatile.Read(ref _packedMeta));

    /// <summary>The hash state as a single comparable value, read with acquire semantics.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected long ReadPackedMeta() => Volatile.Read(ref _packedMeta);

    // ═══════════════════════════════════════════════════════════════════════
    // Bucket addressing
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The chunk of bucket <paramref name="bucketId"/>: its position, plus one for the meta.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static int BucketChunkId(int bucketId) => bucketId + 1;

    /// <summary>
    /// Get the chunk ID of the primary bucket for a given bucket index. Arithmetic (#1205); the accessor is not consulted, and kept in the signature so a
    /// bucket resolve reads the same at every call site.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected int GetBucketChunkId(int bucketId, ref ChunkAccessor<TStore> accessor) => bucketId + 1;

    /// <summary>
    /// Marks the overflow chunk <paramref name="header"/> belongs to as owned by bucket <paramref name="bucketId"/>. Every overflow chunk linked into a chain
    /// carries its owner, which is what lets a split move it out of the chunk the next bucket takes (<see cref="ClaimBucketChunk"/>). Overflow chunks are
    /// never latched, so the field the primary uses for its latch is free for it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static void TagOverflowOwner(ref PagedHashMapBucketHeader header, int bucketId) => header.OlcVersion = bucketId + 1;

    /// <summary>
    /// How far above the bucket frontier the segment's allocation floor sits: room for the frontier to advance before it reaches a chunk the allocator
    /// handed out after the floor last moved. A sixteenth of the map, between 64 and 4 096 chunks: small for a small map, whose segment it would otherwise
    /// make grow, and ample for a large one.
    /// </summary>
    private static int RunwayFor(long bucketCount) => (int)Math.Clamp(bucketCount >> 4, 64, 4096);

    /// <summary>The allocation floor for <paramref name="bucketCount"/> buckets: past the frontier (chunk <c>bucketCount + 1</c>) by the runway.</summary>
    private static int AllocationFloorFor(long bucketCount)
        => (int)Math.Min(ChunkBasedSegment<TStore>.MaxChunkCount, bucketCount + 1 + RunwayFor(bucketCount));

    /// <summary>
    /// Publishes <paramref name="bucketCount"/> as the hash state, with release semantics — everything the split wrote first is visible to a reader that sees
    /// it — and raises the segment's allocation floor with the frontier.
    /// </summary>
    protected void PublishBucketCount(int bucketCount)
    {
        Volatile.Write(ref _packedMeta, bucketCount);
        _segment.RaiseAllocationFloor(AllocationFloorFor(bucketCount));
    }

    /// <summary>
    /// Takes chunk <c>newBucketId + 1</c> for the bucket a split is about to create, before the split takes any latch. Returns false — the split is then
    /// skipped and a later insert retries it — when the chunk is held by a writer that has not linked it into a chain yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chunk is free, normally: the segment's allocation floor sits a runway above the frontier, so nothing allocated since the floor last moved lies
    /// here. It is reserved (<see cref="ChunkBasedSegment{TStore}.TryReserveChunk"/>). Otherwise it holds an overflow chunk allocated before the floor passed
    /// it, and it is moved: a chunk is reserved elsewhere (outside every latch, IXW-07), the owner's chain is latched and walked to the chunk's
    /// predecessor, the chunk copied over and the predecessor relinked, then the owner unlatched with a version bump, so a reader that walked the old link
    /// retries. The owner is read from the chunk itself (<see cref="TagOverflowOwner"/>) and confirmed by the walk: a stale owner — the chunk unlinked and
    /// reused meanwhile — finds no predecessor and defers.
    /// </para>
    /// <para>
    /// What is never waited for is a chunk allocated and not yet linked — reserved by another writer, or linked and then freed. Its holder will link or free
    /// it, but nothing bounds when, so the split defers instead (<see cref="_splitsDeferred"/>). The calling thread's own reservation is checked first: a
    /// chunk it holds unused is taken back.
    /// </para>
    /// </remarks>
    protected bool ClaimBucketChunk(int newBucketId, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet, ChunkReservation<TStore> reservation)
    {
        var chunkId = BucketChunkId(newBucketId);

        // The frontier stays below the floor, and the floor within the segment: grow before the chunk is touched, with no latch held.
        var floor = AllocationFloorFor((long)newBucketId + 1);
        if (_segment.ChunkCapacity <= floor)
        {
            _segment.EnsureCapacity(floor + 1, changeSet);
            if (_segment.ChunkCapacity <= chunkId)
            {
                ThrowHelper.ThrowResourceExhausted("Collections/PagedHashMap/Split", ResourceType.Memory, newBucketId, _segment.ChunkCapacity - 1);
            }
        }

        if (ChunkReservation<TStore>.TryTakeReserved(_segment, chunkId) || _segment.TryReserveChunk(chunkId, changeSet, ref accessor))
        {
            return true;
        }

        if (TryRelocateOverflowChunk(chunkId, ref accessor, changeSet, reservation))
        {
            Interlocked.Increment(ref _overflowChunksRelocated);
            return true;
        }

        // Held and linked to nothing: remember it, so inserts skip the split until its holder links or frees it (see _blockedFrontierChunk). A chunk
        // that carries a tag deferred for another reason — a stale tag, an owner latched — and the next insert simply tries again.
        if (accessor.GetChunkReadOnly<PagedHashMapBucketHeader>(chunkId).OlcVersion == 0)
        {
            Volatile.Write(ref _blockedFrontierChunk, chunkId);
        }

        Interlocked.Increment(ref _splitsDeferred);
        return false;
    }

    /// <summary>Moves the overflow chunk at <paramref name="chunkId"/> elsewhere, leaving <paramref name="chunkId"/> allocated and unlinked. See
    /// <see cref="ClaimBucketChunk"/>.</summary>
    private bool TryRelocateOverflowChunk(int chunkId, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet, ChunkReservation<TStore> reservation)
    {
        var bucketCount = BucketCount;
        var owner = accessor.GetChunkReadOnly<PagedHashMapBucketHeader>(chunkId).OlcVersion - 1;
        if (owner < 0 || owner >= bucketCount)
        {
            return false;   // linked to no chain: a writer holds it unlinked
        }

        // The new home, reserved and pinned before the owner is latched (IXW-07). Taken from the reservation under the latch, or left there for the split.
        reservation.Fill(Math.Max(1, reservation.Available), changeSet, ref accessor);

        var headChunkId = BucketChunkId(owner);
        var latch = new OlcLatch(ref accessor.GetChunk<PagedHashMapBucketHeader>(headChunkId, true).OlcVersion);
        var spins = 0;
        while (!latch.TryWriteLock())
        {
            if (++spins > 4096)
            {
                return false;   // a busy bucket: defer rather than wait
            }

            Thread.SpinWait(8);
            latch = new OlcLatch(ref accessor.GetChunk<PagedHashMapBucketHeader>(headChunkId, true).OlcVersion);
        }

        var written = false;
        try
        {
            // The predecessor, if the chunk is still in this chain: the walk is bounded by the segment's capacity, a cycle being a torn chain.
            var capacity = _segment.ChunkCapacity;
            var predecessor = headChunkId;
            var found = false;
            for (var walk = 0; walk <= capacity; walk++)
            {
                var next = accessor.GetChunkReadOnly<PagedHashMapBucketHeader>(predecessor).OverflowChunkId;
                if (next == chunkId)
                {
                    found = true;
                    break;
                }

                if ((uint)next >= (uint)capacity)
                {
                    break;
                }

                predecessor = next;
            }

            if (!found)
            {
                new OlcLatch(ref accessor.GetChunk<PagedHashMapBucketHeader>(headChunkId, true).OlcVersion).AbortWriteLock();
                return false;
            }

            reservation.Pin(chunkId, ref accessor);
            reservation.Pin(predecessor, ref accessor);
            var target = ChunkReservation<TStore>.AllocateUnderLatch(_segment, changeSet, ref accessor);

            written = true;
            Unsafe.CopyBlock(accessor.GetChunkAddress(target, true), accessor.GetChunkAddress(chunkId), (uint)_segment.Stride);
            Volatile.Write(ref accessor.GetChunk<PagedHashMapBucketHeader>(predecessor, true).OverflowChunkId, target);   // release: the copy first

            // The chunk now belongs to the split: clear its owner, so nothing reads it as linked to the chain it left.
            accessor.GetChunk<PagedHashMapBucketHeader>(chunkId, true).OlcVersion = 0;

            new OlcLatch(ref accessor.GetChunk<PagedHashMapBucketHeader>(headChunkId, true).OlcVersion).WriteUnlock();
            return true;
        }
        catch
        {
            try
            {
                var release = new OlcLatch(ref accessor.GetChunk<PagedHashMapBucketHeader>(headChunkId, true).OlcVersion);
                if (written)
                {
                    release.WriteUnlock();
                }
                else
                {
                    release.AbortWriteLock();
                }
            }
            catch
            {
                // The fault already propagating is the one the caller has to see.
            }

            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Split lock
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Try to acquire the split lock (non-blocking). Returns false if another thread is splitting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireSplitLock() => Interlocked.CompareExchange(ref SplitLock, 1, 0) == 0;

    /// <summary>
    /// Release the split lock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReleaseSplitLock() => Volatile.Write(ref SplitLock, 0);

    // ═══════════════════════════════════════════════════════════════════════
    // Write support
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Spin-waits until write lock is acquired on the given OLC version field.
    /// Two-phase spin policy matching BTree: 64 tight PAUSE spins, then yield-capped SpinWait.
    /// </summary>
    protected void SpinUntilWriteLock(ref int olcVersion)
    {
        var latch = new OlcLatch(ref olcVersion);
        if (latch.TryWriteLock())
        {
            return;
        }

        // Phase 1: tight PAUSE spin — covers typical latch hold time
        for (int i = 0; i < 64; i++)
        {
            Interlocked.Increment(ref _writeLockFailures);
            Thread.SpinWait(1);
            if (latch.TryWriteLock())
            {
                return;
            }
        }

        // Phase 2: yield-capped SpinWait — sleep1Threshold: -1 avoids 15 ms Windows timer-tick
        SpinWait spin = default;
        do
        {
            Interlocked.Increment(ref _writeLockFailures);
            spin.SpinOnce(-1);
        }
        while (!latch.TryWriteLock());
    }

    /// <summary>
    /// Heuristic check: is the load factor above the split threshold? False at <see cref="MaxBucketCount"/>.
    /// Non-atomic read of entry count and meta — harmless: at worst one unnecessary or skipped split.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldSplit()
    {
        var bucketCount = BucketCount;
        long entries = _entryCount;
        return bucketCount < BucketCap && (double)entries / ((long)bucketCount * BucketCapacity) > MaxLoadFactor;
    }

    /// <summary>The most splits one insert makes while it holds the split lock: enough to catch up with concurrent inserters, short enough not to stall the
    /// one inserter that pays for them.</summary>
    private const int MaxSplitsPerCall = 16;

    /// <summary>
    /// The load factor past which an inserter that finds the split lock taken waits for it instead of moving on: the splits are not keeping up, and every
    /// further insert lengthens the chains a lookup walks.
    /// </summary>
    private const double SplitBackPressureLoadFactor = 4.0 / 3 * MaxLoadFactor;

    /// <summary>How long, in <see cref="SpinWait"/> rounds, an inserter waits for the split lock under split back-pressure before inserting on.</summary>
    private const int MaxSplitLockWaitSpins = 256;

    /// <summary>
    /// If load factor exceeds threshold, try to acquire split lock and execute splits while it does, up to <see cref="MaxSplitsPerCall"/>.
    /// Double-checks after acquiring lock to avoid unnecessary splits. Called at the start of a write, before the write takes effect.
    /// </summary>
    /// <remarks>
    /// One split per insert, made only when the lock is free, falls behind concurrent inserters: measured with eight, the load factor climbed to 1.6 against
    /// a 0.75 threshold, and the chains a lookup walks with it. So the thread that holds the lock splits until the map is back under the threshold (bounded),
    /// and a third past the threshold an inserter that finds the lock taken waits for it rather than adding to the backlog — the inserts are then paced by the
    /// splits, which is what keeps a lookup at one chunk. A third above the threshold, measured: with seven entries per bucket and eight inserters the load
    /// then holds at 1.0 with no loss of insert throughput (3.8M/s), where twice the threshold let it sit at 1.5.
    /// </remarks>
    protected void TrySplitIfNeeded(ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        if (!ShouldSplit() || Volatile.Read(ref _entryCount) < Volatile.Read(ref _splitRetryAtEntries))
        {
            return;
        }

        var blocked = Volatile.Read(ref _blockedFrontierChunk);
        if (blocked != 0)
        {
            if (blocked == BucketChunkId(BucketCount) && _segment.IsChunkAllocated(blocked)
                && accessor.GetChunkReadOnly<PagedHashMapBucketHeader>(blocked).OlcVersion == 0)
            {
                return;   // still held, still unlinked: nothing a split can do yet
            }

            Interlocked.CompareExchange(ref _blockedFrontierChunk, 0, blocked);
        }

        if (!TryAcquireSplitLock())
        {
            if (!IsLoadPast(SplitBackPressureLoadFactor))
            {
                return;
            }

            // Bounded: the inserter holds its epoch while it waits, and a wait the splitter could depend on — its own page-cache back-pressure needs
            // evictions an old epoch forbids — must end on its own.
            SpinWait spin = default;
            var spins = 0;
            while (!TryAcquireSplitLock())
            {
                if (++spins > MaxSplitLockWaitSpins || !IsLoadPast(SplitBackPressureLoadFactor))
                {
                    return;   // the splitter caught up meanwhile, or the wait is over: insert on
                }

                spin.SpinOnce(-1);
            }
        }

        try
        {
            for (var i = 0; i < MaxSplitsPerCall && ShouldSplit(); i++)
            {
                if (!ExecuteSplit(ref accessor, changeSet))
                {
                    break;
                }

                Interlocked.Increment(ref _splitCount);
            }
        }
        catch (Exception e) when (e is ResourceExhaustedException or PageCacheBackpressureTimeoutException)
        {
            // A split is upkeep: one that cannot get its chunk or its pages now is retried by a later write, and the write that triggered it goes on. Any
            // other fault propagates, and fails a write that has not taken effect yet — the split runs first. A segment out of chunk ids still refuses:
            // the next write that needs a chunk throws before it appends.
            Interlocked.Increment(ref _splitsFailed);
            Volatile.Write(ref _splitRetryAtEntries, Volatile.Read(ref _entryCount) + SplitRetryAfterEntries);
        }
        finally
        {
            ReleaseSplitLock();
        }
    }

    /// <summary>How many more entries a map takes after a split met a missing resource before it tries a split again.</summary>
    private const int SplitRetryAfterEntries = 256;

    /// <summary>Whether the load factor is past <paramref name="loadFactor"/>, and the map can still split.</summary>
    private bool IsLoadPast(double loadFactor)
    {
        var bucketCount = BucketCount;
        return bucketCount < BucketCap && (double)_entryCount / ((long)bucketCount * BucketCapacity) > loadFactor;
    }

    /// <summary>
    /// Advance the linear hash state until a batch of <paramref name="additionalEntries"/> further inserts cannot trigger a split, and return how many
    /// splits that took (#1100).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this is for, and it is not a saving.</b> A parallel insert region can be partitioned by bucket only if no insert inside it changes the
    /// structure. A split does: it takes the global split lock, splits the round-robin bucket <c>Next</c> rather than the bucket being inserted into, and
    /// rewrites the meta — so it crosses every partition at once. Running the splits here, serially, before the region opens, leaves the parallel inserts
    /// with nothing but bucket-local appends. The entries relocated are the same either way; what is bought is partition safety.
    /// </para>
    /// <para>
    /// <b>Why <see cref="EnsureCapacity"/> cannot do this, measured rather than reasoned.</b> That method grows the segment and leaves
    /// <see cref="_packedMeta"/> alone, and <c>ShouldSplit</c> reads the bucket count out of <see cref="_packedMeta"/> — so it changes the COST of a split
    /// and not whether one happens. Called before a 3 000-spawn concentrated burst it produced figures identical in every column: 189 splits, 1 769 entries
    /// rehashed, 162 overflow chunks, 445 buckets. <c>PreSizingTheEntityMapDoesNotRemoveItsSplits</c> pins that, because the opposite was asserted in a
    /// design note first and believed for a while.
    /// </para>
    /// <para>
    /// <b>Serial by contract.</b> The caller must hold the structure quiescent — this is the fence's <c>Prepare</c>, before any worker is admitted. The
    /// split lock is still taken per split, so a concurrent caller cannot corrupt anything, but it could leave the state short of the target and the
    /// return value then understates what happened. The bound exists for the same reason the per-insert path has one: a load factor computed from a
    /// corrupt count must not spin forever. A split that is deferred (<see cref="ClaimBucketChunk"/>) ends the advance the same way.
    /// </para>
    /// </remarks>
    /// <param name="additionalEntries">Entries the caller is about to insert.</param>
    /// <param name="changeSet">Change set for the chunk writes the splits perform.</param>
    /// <returns>Splits performed. Zero means the state already had room for the batch.</returns>
    public int AdvanceHashStateFor(int additionalEntries, ChangeSet changeSet = null)
    {
        if (additionalEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(additionalEntries), additionalEntries, "A batch size cannot be negative.");
        }

        // Pre-allocate first: every split below would otherwise be able to trigger a page-level grow, which is exactly what EnsureCapacity is for. The two
        // are complements, not alternatives — that is the distinction the corrected note above turns on.
        EnsureCapacity(Interlocked.Read(ref _entryCount) + additionalEntries, changeSet);

        var performed = 0;

        // One split per iteration, each admitting BucketCapacity * MaxLoadFactor more entries, so the count is bounded by the batch. The +2 is slack for
        // the load factor landing exactly on the threshold.
        var ceiling = (int)((additionalEntries / Math.Max(1.0, BucketCapacity * MaxLoadFactor)) + 2);
        while (performed < ceiling && WouldSplitAfter(additionalEntries))
        {
            if (!TryAcquireSplitLock())
            {
                // Someone else is splitting, which means the caller broke the quiescence contract. Stop rather than spin: the state is still correct, and
                // the shortfall shows up as a split inside the parallel region, which that region asserts against.
                break;
            }

            bool split;
            try
            {
                var accessor = _segment.CreateChunkAccessor(changeSet);
                try
                {
                    split = ExecuteSplit(ref accessor, changeSet);
                }
                finally
                {
                    accessor.Dispose();
                }

                if (split)
                {
                    Interlocked.Increment(ref _splitCount);
                    performed++;
                }
            }
            finally
            {
                ReleaseSplitLock();
            }

            if (!split)
            {
                break;
            }
        }

        return performed;
    }

    /// <summary>Whether the load factor would be over the threshold once <paramref name="additionalEntries"/> more entries are in.</summary>
    /// <remarks>
    /// The same arithmetic as <c>ShouldSplit</c>, asked about a future count rather than the present one. Kept beside it deliberately: if the threshold or
    /// the formula ever changes, the two must change together or a pre-advance will leave a region that splits anyway.
    /// </remarks>
    private bool WouldSplitAfter(int additionalEntries)
    {
        var bucketCount = BucketCount;
        var entries = Interlocked.Read(ref _entryCount) + additionalEntries;
        return bucketCount < BucketCap && (double)entries / ((long)bucketCount * BucketCapacity) > MaxLoadFactor;
    }

    /// <summary>
    /// Grow the backing segment so the splits <paramref name="totalEntries"/> entries call for find their chunks present: the buckets the load factor needs,
    /// the runway above them, and room for overflow. Does NOT advance the linear hash state — entry redistribution happens via per-insert
    /// <see cref="ExecuteSplit"/>; to do that, use <see cref="AdvanceHashStateFor"/>.
    /// </summary>
    /// <remarks>
    /// Proportional to the request (#1205). It used to round the bucket target up to the next power of two and allocate a directory for it, so one commit of
    /// 64 spawns into an archetype of 11M entities asked for 4 194 304 buckets at once — 0.5 GiB of segment and 32 768 directory chunks — and the 16-bit
    /// directory count wrapped to 0.
    /// </remarks>
    public void EnsureCapacity(long totalEntries, ChangeSet changeSet = null)
    {
        var targetBuckets = Math.Clamp((long)(totalEntries / (BucketCapacity * MaxLoadFactor)) + 1, _n0, MaxBucketCount);
        if (targetBuckets <= BucketCount)
        {
            return;
        }

        // The buckets and their runway, plus an eighth for overflow chunks, which live above them.
        var chunks = Math.Min(ChunkBasedSegment<TStore>.MaxChunkCount, AllocationFloorFor(targetBuckets) + (targetBuckets >> 3) + 1);
        _segment.EnsureCapacity((int)chunks, changeSet);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test helpers (internal for InternalsVisibleTo)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Clear the insert-cost diagnostics so one burst can be counted in isolation from the engine's own start-up inserts (#1098). Reset only on a quiescent
    /// map — the counters cannot be cleared atomically with respect to a live writer.
    /// </summary>
    internal void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _splitCount, 0);
        Interlocked.Exchange(ref _olcRestarts, 0);
        Interlocked.Exchange(ref _writeLockFailures, 0);
        Interlocked.Exchange(ref _overflowChunksChained, 0);
        Interlocked.Exchange(ref _splitEntriesRehashed, 0);
        Interlocked.Exchange(ref _overflowChunksRelocated, 0);
        Interlocked.Exchange(ref _splitsDeferred, 0);
        Interlocked.Exchange(ref _splitsFailed, 0);
    }

    /// <summary>Test-accessible wrapper for <see cref="GetBucketChunkId"/>.</summary>
    internal int GetBucketChunkIdForTest(int bucketId, ref ChunkAccessor<TStore> accessor) => GetBucketChunkId(bucketId, ref accessor);

    /// <summary>Persists the bucket count and the entry count, on a quiescent map. Test seam.</summary>
    internal void FlushMetaForTest(ref ChunkAccessor<TStore> accessor)
    {
        PersistBucketCount(ref accessor);
        PersistEntryCount(ref accessor);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Meta persistence
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Persists the bucket count to chunk 0. Its one writer is the thread holding the split lock — the split that published it, or a quiescent rebuild: a
    /// second writer reading the count outside the lock could store an older one over a newer, and a stale persisted count leaves the last bucket
    /// unreachable after a reopen, its chunk then read as an overflow chunk at the frontier (#1205).
    /// </summary>
    protected void PersistBucketCount(ref ChunkAccessor<TStore> accessor)
    {
        PersistBucketCountProbe?.Invoke();
        var bucketCount = Volatile.Read(ref _packedMeta);
        ref var meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);
        meta.BucketCount = bucketCount;
        Volatile.Write(ref _persistedBucketCount, bucketCount);
    }

    /// <summary>Persists the entry count to chunk 0. Its one writer is <see cref="FlushMeta"/>'s caller — the checkpoint — or a quiescent rebuild.</summary>
    protected void PersistEntryCount(ref ChunkAccessor<TStore> accessor)
    {
        var entryCount = Volatile.Read(ref _entryCount);
        ref var meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);
        meta.EntryCount = entryCount;
        _persistedEntryCount = entryCount;
    }

    /// <summary>
    /// Persists the entry count to the meta chunk when it changed since the last flush, so the next <see cref="InitializeOpen"/> reads the right total
    /// without walking every chain. The checkpoint calls it on every cycle — a despawn changes the count and nothing else the checkpoint looks at — and a
    /// cycle with nothing new writes nothing. The bucket count is the splits' (<see cref="PersistBucketCount"/>): this writes it only when a split's own
    /// persist faulted after it published, and then under the split lock — still its one writer.
    /// </summary>
    /// <param name="changeSet">The checkpoint's: a write the change set does not record is not written back.</param>
    /// <returns>Whether it wrote: the caller then owns a change to save.</returns>
    public bool FlushMeta(ChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        var entriesStale = Volatile.Read(ref _entryCount) != _persistedEntryCount;
        var bucketsStale = Volatile.Read(ref _persistedBucketCount) != BucketCount;
        if (!entriesStale && !bucketsStale)
        {
            return false;
        }

        var wrote = false;
        var accessor = _segment.CreateChunkAccessor(changeSet);
        try
        {
            if (entriesStale)
            {
                PersistEntryCount(ref accessor);
                wrote = true;
            }

            // Not taken: a split holds the lock, and persists the count it publishes.
            if (bucketsStale && TryAcquireSplitLock())
            {
                try
                {
                    PersistBucketCount(ref accessor);
                    wrote = true;
                }
                finally
                {
                    ReleaseSplitLock();
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return wrote;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Create / Open scaffolding (called by concrete factory methods)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initialize a new linear hash map: reserve chunk 0 (meta) and the initial bucket chunks, and write the initial meta state.
    /// </summary>
    protected void InitializeCreate(int initialBuckets, ChangeSet changeSet)
    {
        Debug.Assert(initialBuckets > 0 && BitOperations.IsPow2(initialBuckets));

        // Reserve chunk 0 for the meta and clear it
        _segment.ReserveChunk(0, true, changeSet);

        InitializeEmptyState(initialBuckets, changeSet);
    }

    /// <summary>
    /// Reserve the initial bucket chunks <c>1 … N0</c> and write the empty meta state, assuming chunk 0 (the meta chunk) is already reserved and every other
    /// chunk free. Shared by <see cref="InitializeCreate"/> (fresh map) and <see cref="ClearForRebuild"/> (crash-recovery reset of an existing map whose
    /// chunk 0 is preserved).
    /// </summary>
    private void InitializeEmptyState(int initialBuckets, ChangeSet changeSet)
    {
        Debug.Assert(initialBuckets > 0 && BitOperations.IsPow2(initialBuckets));

        // Nothing allocates while a map is created or reset, so the floor can be set outright — and lowered, by a reset.
        _segment.ResetAllocationFloor(AllocationFloorFor(initialBuckets));
        _segment.EnsureCapacity(AllocationFloorFor(initialBuckets) + 1, changeSet);

        var accessor = _segment.CreateChunkAccessor(changeSet);
        try
        {
            for (var b = 0; b < initialBuckets; b++)
            {
                var chunkId = BucketChunkId(b);
                if (!_segment.TryReserveChunk(chunkId, changeSet, ref accessor))
                {
                    throw new InvalidOperationException(
                        $"PagedHashMap: initial bucket chunk {chunkId} is already allocated — a map is created over chunk 0 alone, every other chunk free.");
                }

                InitializeBucket(chunkId, ref accessor);
            }

            Volatile.Write(ref _packedMeta, initialBuckets);
            _entryCount = 0;
            _persistedEntryCount = 0;
            _persistedBucketCount = initialBuckets;
            _splitRetryAtEntries = 0;

            ref var meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);
            meta = default;
            meta.N0 = _n0;
            meta.Format = PagedHashMapMeta.FormatMagic;
            meta.BucketCount = initialBuckets;
            meta.EntryCount = 0;
            meta.Flags = (byte)(_allowMultiple ? 1 : 0);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Reconnect to an existing linear hash map by reading <see cref="_packedMeta"/> and <see cref="_entryCount"/> from chunk 0.
    /// </summary>
    /// <param name="tolerateDamage">
    /// The map is about to be rebuilt (a crash reopen, RB-01): a meta that cannot be used opens the map as <see cref="N0"/> empty buckets instead of throwing,
    /// so the rebuild has a map to clear. Its entries are then unreachable — what a torn meta means either way.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The meta is not this format's (<see cref="PagedHashMapMeta.FormatMagic"/>), its N0 is not the caller's, or its bucket count cannot be: a map written
    /// before #1205 — whose directory this code no longer reads — or a damaged meta. Refused here rather than resolved against: a wrong bucket count makes
    /// every lookup read the wrong chunk.
    /// </exception>
    protected void InitializeOpen(bool tolerateDamage = false)
    {
        var accessor = _segment.CreateChunkAccessor();
        try
        {
            ref readonly var meta = ref accessor.GetChunkReadOnly<PagedHashMapMeta>(0);
            if (!PagedHashMapMeta.IsUsable(meta, _n0, _segment.ChunkCapacity, _allowMultiple, out var reason))
            {
                if (tolerateDamage)
                {
                    Volatile.Write(ref _packedMeta, _n0);
                    _entryCount = 0;
                    _persistedEntryCount = -1;   // the meta chunk holds garbage: the first flush writes the count whatever it is
                    _persistedBucketCount = -1;
                    _segment.RaiseAllocationFloor(AllocationFloorFor(_n0));
                    return;
                }

                throw new InvalidOperationException(
                    $"PagedHashMap open refused: root={_segment.RootPageIndex} — {reason}. A map written before the directory was removed (#1205) is not "
                    + "readable by this engine; a damaged meta is rebuilt by the crash path, or by a repair that forces it.");
            }

            Volatile.Write(ref _packedMeta, meta.BucketCount);
            _entryCount = meta.EntryCount;
            _persistedEntryCount = meta.EntryCount;
            _persistedBucketCount = meta.BucketCount;
            _segment.RaiseAllocationFloor(AllocationFloorFor(meta.BucketCount));
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Reset an existing map back to empty for a crash-recovery rebuild: free every bucket / overflow chunk (everything but the reserved meta chunk 0) by the
    /// segment occupancy bitmap, then reserve <see cref="N0"/> empty buckets again and reset <see cref="_packedMeta"/> / <see cref="_entryCount"/>. The map
    /// is then repopulated via <c>InsertDuringRebuild</c> from the authoritative source (revision chains / cluster data).
    /// <para>
    /// Torn-safe by construction: freeing is a pure occupancy-bitmap operation — a CRC-torn page is reclaimed without ever being parsed. This is the
    /// EntityMap analogue of <c>BTreeBase.ClearSharedSegment</c> (Phase 2), making the EntityMap a derived-on-crash structure (03-recovery.md §7).
    /// </para>
    /// </summary>
    internal void ClearForRebuild(ChangeSet changeSet)
    {
        using var guard = EpochGuard.Enter(_segment.Store.EpochManager);

        // Free all bucket / overflow chunks by bitmap only (chunk 0 — the meta — is kept reserved): the occupancy metadata, never the page content, so a
        // torn page is reclaimed without being read — a page at a time, each released before the next, a bitmap word at a time.
        _segment.FreeAllChunksFrom(1);

        // Reserve N0 empty buckets over the preserved meta chunk and reset the in-memory + persisted meta.
        InitializeEmptyState(_n0, changeSet);
        OnClearedForRebuild();
    }

    /// <summary>
    /// Drops what a subclass caches about chunk positions. After a clear, a chunk that was a bucket's primary can come back as an overflow chunk, whose
    /// owner tag (<see cref="TagOverflowOwner"/>) a stale cache entry would read as a primary's unlocked OLC version.
    /// </summary>
    protected virtual void OnClearedForRebuild()
    {
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Diagnostics
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validate structural integrity of the hash map. Walks all bucket chains, checks header invariants, and verifies the total entry count
    /// matches <see cref="_entryCount"/>. Every overflow chunk must name its bucket as owner (<see cref="TagOverflowOwner"/>).
    /// </summary>
    public bool VerifyIntegrity(ref ChunkAccessor<TStore> accessor)
    {
        ref readonly var meta = ref accessor.GetChunkReadOnly<PagedHashMapMeta>(0);
        if (meta.N0 != _n0 || meta.N0 <= 0 || !BitOperations.IsPow2(meta.N0) || meta.Format != PagedHashMapMeta.FormatMagic)
        {
            return false;
        }

        var bucketCount = BucketCount;
        long totalEntries = 0;

        for (int b = 0; b < bucketCount; b++)
        {
            int chunkId = GetBucketChunkId(b, ref accessor);
            int visited = 0;

            while (chunkId != -1)
            {
                visited++;
                if (visited > 100)
                {
                    return false; // cycle detection
                }

                ref readonly var header = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(chunkId));
                if (header.EntryCount > BucketCapacity || (visited > 1 && header.OlcVersion != b + 1))
                {
                    return false;
                }

                totalEntries += header.EntryCount;
                chunkId = header.OverflowChunkId;
            }
        }

        return totalEntries == _entryCount;
    }

    /// <summary>
    /// Collect diagnostic statistics: bucket count, entry distribution, overflow chain depths, fill histogram.
    /// </summary>
    public PagedHashMapStats GetStats(ref ChunkAccessor<TStore> accessor)
    {
        var bucketCount = BucketCount;
        var stats = new PagedHashMapStats
        {
            BucketCount = bucketCount,
            EntryCount = _entryCount
        };

        for (int b = 0; b < bucketCount; b++)
        {
            int chunkId = GetBucketChunkId(b, ref accessor);
            ref readonly var primary = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(chunkId));
            int primaryEntryCount = primary.EntryCount;

            // Fill histogram (primary bucket only)
            if (primaryEntryCount == 0)
            {
                stats.FillEmpty++;
            }
            else
            {
                double fill = (double)primaryEntryCount / BucketCapacity;
                if (fill <= 0.25)
                {
                    stats.FillQuarter++;
                }
                else if (fill <= 0.50)
                {
                    stats.FillHalf++;
                }
                else if (fill <= 0.75)
                {
                    stats.FillThreeQuarter++;
                }
                else
                {
                    stats.FillFull++;
                }
            }

            // Chain walk (with cycle detection matching VerifyIntegrity)
            int chainLength = 1;
            if (primary.OverflowChunkId != -1)
            {
                stats.OverflowBucketCount++;
                int overflowId = primary.OverflowChunkId;
                while (overflowId != -1)
                {
                    chainLength++;
                    if (chainLength > 100)
                    {
                        break; // cycle detection
                    }
                    ref readonly var overflow = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(overflowId));
                    overflowId = overflow.OverflowChunkId;
                }
            }

            if (chainLength > stats.MaxChainLength)
            {
                stats.MaxChainLength = chainLength;
            }
        }

        stats.LoadFactor = bucketCount > 0
            ? (double)stats.EntryCount / ((long)bucketCount * BucketCapacity)
            : 0;

        return stats;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Abstract methods — implemented by concrete classes
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initialize a freshly allocated bucket chunk (set OlcVersion, EntryCount, OverflowChunkId sentinel).
    /// </summary>
    protected abstract void InitializeBucket(int chunkId, ref ChunkAccessor<TStore> accessor);

    /// <summary>
    /// Execute a split: redistribute entries from the current split-pointer bucket to old and new buckets. Called while holding the split lock. Returns false
    /// when the split was not made — the map at <see cref="MaxBucketCount"/>, or the new bucket's chunk not claimable yet (<see cref="ClaimBucketChunk"/>).
    /// </summary>
    protected abstract bool ExecuteSplit(ref ChunkAccessor<TStore> accessor, ChangeSet changeSet);
}
