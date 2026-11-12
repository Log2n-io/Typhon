using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

// I8 of claude/research/Storage/huge-scan-assessment.md, rule PS-25: scan-resistant eviction. A page loaded for the first time waits on
// probation until a DIFFERENT operation — a transaction or a lease — asks for it again; only then does it join the clock. The probation area's size p adapts
// as ARC's does (Megiddo and Modha, FAST 2003), on the clock as CAR does (Bansal and Modha, FAST 2004): a miss on a page the area let go says the area is
// too small, a miss on a page the clock let go says the clock is. Nothing is reserved: with no walk running, p drifts to its floor and the clock has the cache.
public partial class PagedMMF
{
    /// <summary>
    /// The operation this thread's page requests belong to: the TSN of the transaction open on it, or of the point-in-time accessor whose lease it holds,
    /// 0 between them. A walk refreshes its pinned epoch every few rows, so the epoch cannot tell one operation from the next; a transaction or a lease spans
    /// the whole of one. Read by the eviction policy (<see cref="OperationTag"/>) and the page trace.
    /// </summary>
    [ThreadStatic]
    private static long t_operation;

    // The operations open on this thread, innermost last: ending one out of order — a transaction disposed before a later one — removes it wherever it
    // sits, so the thread never goes back to an operation that has ended. Deeper than its size, the oldest falls off.
    [ThreadStatic]
    private static long[] t_operations;

    [ThreadStatic]
    private static int t_operationDepth;

    private const int MaxOperationDepth = 16;

    /// <summary>Makes <paramref name="tsn"/> this thread's operation until <see cref="ExitOperation"/>.</summary>
    internal static void EnterOperation(long tsn)
    {
        var stack = t_operations ??= new long[MaxOperationDepth];
        if (t_operationDepth == MaxOperationDepth)
        {
            Array.Copy(stack, 1, stack, 0, MaxOperationDepth - 1);
            t_operationDepth--;
        }

        stack[t_operationDepth++] = tsn;
        t_operation = tsn;
    }

    /// <summary>Ends operation <paramref name="tsn"/> on this thread; the innermost one still open becomes the thread's operation.</summary>
    internal static void ExitOperation(long tsn)
    {
        var stack = t_operations;
        if (stack == null)
        {
            return;
        }

        for (var i = t_operationDepth - 1; i >= 0; i--)
        {
            if (stack[i] == tsn)
            {
                Array.Copy(stack, i + 1, stack, i, t_operationDepth - i - 1);
                t_operationDepth--;
                break;
            }
        }

        t_operation = t_operationDepth == 0 ? 0 : stack[t_operationDepth - 1];
    }

    /// <summary>
    /// This thread's operation as a probation tag: 1-255, or 0 outside any operation — a checkpoint, a fence, recovery, whose loads go straight to the clock.
    /// Two operations share a tag one time in 255; a request from one then leaves the other's page on probation, which the next request, or the ghost list
    /// past its eviction, corrects.
    /// </summary>
    internal static byte OperationTag
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var op = t_operation;
            return op == 0 ? (byte)0 : (byte)(1 + (ulong)op % 255);
        }
    }

    /// <summary>The policy, or null when <see cref="PagedMMFOptions.ScanResistantEviction"/> is off.</summary>
    private ProbationPolicy _probation;

    /// <summary>Pages on probation now. Raised by a placement, lowered by a promotion and by the reclaim of a probation page; read on the miss path.</summary>
    private CacheLinePaddedInt _probationCount;

    /// <summary>Pages promoted from probation to the clock. Diagnostic.</summary>
    internal long ProbationPromotions;

    /// <summary>The probation tag of slot <paramref name="memPageIndex"/> (tests).</summary>
    internal byte ProbationTagForTests(int memPageIndex) => Slot(memPageIndex).ProbationTag;

    /// <summary>The file page slot <paramref name="memPageIndex"/> holds, -1 for none (tests).</summary>
    internal int FilePageOfSlotForTests(int memPageIndex) => Slot(memPageIndex).FilePageIndex;

    /// <summary>Pages on probation now (tests, diagnostics).</summary>
    internal int ProbationCount => Volatile.Read(ref _probationCount.Value);

    /// <summary>The probation area's current target size p, in pages; 0 when the policy is off (tests, diagnostics).</summary>
    internal int ProbationTarget => _probation?.Target ?? 0;

    /// <summary>Misses on a page the probation area had let go, which grew its target (tests, diagnostics).</summary>
    internal long ProbationGhostHits => _probation?.AreaGhostHits ?? 0;

    /// <summary>Ghosts of probation and of the clock (tests, diagnostics).</summary>
    internal (int Area, int Clock) GhostCounts => _probation?.GhostCounts ?? (0, 0);

    /// <summary>Probation takes made (tests, diagnostics).</summary>
    internal long ProbationTakes => Interlocked.Read(ref _probationTakes);

    private long _probationTakes;

    /// <summary>Second chances given: probation pages their own operation asked for again, sent round the queue once more (tests, diagnostics).</summary>
    internal long ProbationSecondChances => _probation?.SecondChances ?? 0;

    /// <summary>Compactions of the probation queue run to their end (tests, diagnostics).</summary>
    internal long ProbationQueueCompactions => _probation?.Compactions ?? 0;

    /// <summary>The probation queue's entries, stale ones included (tests, diagnostics).</summary>
    internal long ProbationQueueLength => _probation?.QueueLength ?? 0;

    /// <summary>This thread's operation tag (tests).</summary>
    internal static long OperationForTests => t_operation;

    /// <summary>Misses on a page the clock had let go, which shrank the area's target (tests, diagnostics).</summary>
    internal long ClockGhostHits => _probation?.ClockGhostHits ?? 0;

    private void InitProbation(int pageCount)
    {
        if (Options.ScanResistantEviction)
        {
            _probation = new ProbationPolicy(this, pageCount);
        }
    }

    /// <summary>
    /// A request found the page resident — the hit path of every epoch-tagged request and of a warm read pin. A clock page gets its counter bumped, as
    /// before; a probation page asked for by a different operation joins the clock. One byte, on the line the counter is on.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteRequested(PageInfo pi)
    {
        var tag = pi.ProbationTag;
        if (tag == 0)
        {
            pi.IncrementClockSweepCounter();
            return;
        }

        NoteProbationRequested(pi, tag);
    }

    /// <summary>
    /// A request for a page on probation. From its own operation: counted in the page's clock counter, which the clock does not age on probation —
    /// <see cref="ProbationLoaded"/> for the request that loaded it, <see cref="ProbationReferenced"/>, CAR's T1 reference bit, for a request again, which
    /// buys it one more lap of the queue (<see cref="ProbationSecondLap"/>); asked for again in that lap (<see cref="ProbationReferencedTwice"/>), it is a
    /// page its operation keeps coming back to — an index's interior node under a walk — and joins the clock at its next take. From another operation, or
    /// from outside any: it joins the clock at once. A cold read pin (<see cref="AcquirePageForRead"/>) loads without a request, so its first one again
    /// counts as the load's: a re-read missed, never one invented.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void NoteProbationRequested(PageInfo pi, byte tag)
    {
        if (OperationTag == tag)
        {
            // One step from the value read, by CAS: two requests at once — two workers of one lease — take it once, not twice past its state.
            var c = pi.ClockSweepCounter;
            if (c < ProbationReferenced || c == ProbationSecondLap)
            {
                pi.TryAdvanceClockSweepCounter(c);
            }

            return;
        }

        Promote(pi, tag);
    }

    private void Promote(PageInfo pi, byte tag)
    {
        if (!pi.TryClearProbation(tag))
        {
            return;
        }

        Interlocked.Decrement(ref _probationCount.Value);
        Interlocked.Increment(ref ProbationPromotions);
        pi.IncrementClockSweepCounter();
    }

    /// <summary>Promotes slot <paramref name="memPageIndex"/>'s probation page as another operation's request would, its epoch untouched (tests).</summary>
    internal void PromoteForTests(int memPageIndex)
    {
        var pi = Slot(memPageIndex);
        Promote(pi, pi.ProbationTag);
    }

    /// <summary>
    /// The miss's placement, once its slot is claimed and keyed, before it is published: records the page the slot held in the ghost list of the set
    /// that let it go, adapts the area's target on a ghost hit for the page coming in, and sets the slot's tag — 0 for the clock, the operation's tag for
    /// probation, set under the policy's lock before the slot is queued, so a take never finds the entry untagged. Returns the tag.
    /// </summary>
    private byte PlaceLoad(int memPageIndex, int filePageIndex, int evictedFilePageIndex, byte evictedTag)
    {
        var policy = _probation;
        if (policy == null)
        {
            return 0;
        }

        var tag = policy.Place(filePageIndex, OperationTag, evictedFilePageIndex, evictedTag, memPageIndex, Slot(memPageIndex));
        if (tag != 0)
        {
            Interlocked.Increment(ref _probationCount.Value);
        }

        return tag;
    }

    /// <summary>
    /// A slot the reserve keeps (PS-24) gave up the page it held: the page goes to its set's ghost list, as a placement's displaced page does.
    /// </summary>
    private void RecordEvicted(int evictedFilePageIndex, byte evictedTag) => _probation?.RecordEvicted(evictedFilePageIndex, evictedTag);

    /// <summary>
    /// A slot that was placed on probation and given back before anyone used it — a lost publish race, a slot its owner failed to prepare —
    /// leaves probation with it.
    /// </summary>
    private void UnplaceLoad(PageInfo pi)
    {
        var tag = pi.ProbationTag;
        if (tag != 0 && pi.TryClearProbation(tag))
        {
            Interlocked.Decrement(ref _probationCount.Value);
        }
    }

    /// <summary>
    /// Takes the oldest probation page an eviction may take (<see cref="TryAcquire"/>'s rules) — when the area is over its target and the cache has no
    /// free slot, or, with <paramref name="regardless"/>, whatever its size, for a round the clock could serve nothing. A page the protections keep goes
    /// back to the tail. Until the cache has been filled once nothing is given up: probation grows into the free slots, as ARC's T1 grows until the cache
    /// is full. The first eviction says it was (<see cref="ProbationPolicy.CacheFilled"/>): the free-slot count includes the Idle slots.
    /// </summary>
    private bool TryTakeFromProbation(long minActiveEpoch, bool regardless, out int memPageIndex, out int evictedFilePageIndex, out byte evictedTag)
    {
        memPageIndex = -1;
        evictedFilePageIndex = -1;
        evictedTag = 0;
        var policy = _probation;
        if (policy == null || Volatile.Read(ref _probationCount.Value) == 0
            || (!regardless && (Volatile.Read(ref _probationCount.Value) <= policy.Target || !policy.CacheFilled)))
        {
            return false;
        }

        // One entry at a time, oldest first; the pop sends a page its operation asked for again round once more (bounded per take). A page the
        // protections keep goes to the back, and those tries are bounded, so a queue of held pages cannot keep an allocation here; the clock and pass 2
        // follow. TryAcquire checks under the slot's lock that the slot still holds the page on probation: popped, it may have been promoted or reloaded.
        var secondChances = Math.Min(Volatile.Read(ref _probationCount.Value), ProbationPolicy.SecondChancesPerTake);
        for (var tries = 0; tries < ProbationPolicy.TakeTries; tries++)
        {
            if (!policy.TryPopOldest(ref secondChances, out var entry))
            {
                return false;
            }

            var slot = (int)(entry >> 32);
            var filePage = (int)entry;
            var taken = false;
            try
            {
                taken = TryAcquire(slot, minActiveEpoch, filePage, out evictedFilePageIndex, out evictedTag);
            }
            finally
            {
                // Pinned, dirty, owed or epoch-held — or a lock timeout thrown: still on probation, at the back, never without its entry.
                if (!taken && IsQueuedProbationLive(slot, filePage))
                {
                    policy.Requeue(slot, filePage);
                }
            }

            if (taken)
            {
                memPageIndex = slot;
                Interlocked.Increment(ref _probationTakes);
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a queued (slot, file page) entry still names a page on probation. The policy's pop and compaction ask.</summary>
    internal bool IsQueuedProbationLive(int slot, int filePage)
    {
        var pi = Slot(slot);
        return pi.FilePageIndex == filePage && pi.ProbationTag != 0;
    }

    // A probation page's clock counter, which the clock does not age on probation: the requests of its own operation, lap by lap.
    private const int ProbationLoaded = 1;              // the request that loaded it
    private const int ProbationReferenced = 2;          // asked for again: CAR's T1 reference bit
    private const int ProbationSecondLap = 3;           // its bit spent on one more lap of the queue
    private const int ProbationReferencedTwice = 4;     // asked for again in that lap: a page its operation keeps coming back to

    /// <summary>What <see cref="ConsumeProbationReference"/> did with a live probation page at the queue's head.</summary>
    internal enum ProbationVerdict : byte
    {
        /// <summary>No reference since the last lap: the take may have it.</summary>
        None,

        /// <summary>Referenced once: one more lap of the queue.</summary>
        SecondLap,

        /// <summary>Referenced in two laps: joined the clock, its queue entry stale.</summary>
        Promoted,
    }

    /// <summary>
    /// The reference state of a live probation page at the queue's head: referenced once (<see cref="ProbationReferenced"/>), it earns one more lap;
    /// referenced again in that lap (<see cref="ProbationReferencedTwice"/>), it joins the clock. A walk's pages, read once — or many times in a row, which
    /// set the bit once — leave after one more lap at most; a page the walk keeps coming back to, an index's interior node, stays. Measured on the SWG
    /// trace with a 5M-row audit beside 500 bots: the walk's misses at 128 MiB 465k with one lap, 226k with this (the plain clock: 241k).
    /// </summary>
    internal ProbationVerdict ConsumeProbationReference(int slot)
    {
        var pi = Slot(slot);
        switch (pi.ClockSweepCounter)
        {
            case ProbationReferenced:
                return pi.TryAdvanceClockSweepCounter(ProbationReferenced) ? ProbationVerdict.SecondLap : ProbationVerdict.None;
            case ProbationReferencedTwice:
                // Into the clock as a fresh load, counter 1: its 4 says how its own operation used it, not how hot it is to the others — promoted at 5,
                // the hottest a counter goes, a walk's re-read pages outlasted the working set (64 MiB harness: 26 foreground loads beside a walk
                // became 1,264-1,652).
                if (pi.TryClearProbation(pi.ProbationTag))
                {
                    pi.ResetClockSweepCounter();
                    pi.IncrementClockSweepCounter();
                    Interlocked.Decrement(ref _probationCount.Value);
                    Interlocked.Increment(ref ProbationPromotions);
                }

                return ProbationVerdict.Promoted;
            default:
                return ProbationVerdict.None;
        }
    }

    private void DisposeProbation()
    {
        _probation?.Dispose();
        _probation = null;
    }
}

/// <summary>
/// The probation area's bookkeeping (PS-25): the FIFO of probation slots, the two ghost lists — B1, the pages probation let go, and B2, the pages the clock
/// let go, each up to one entry per slot — and the adaptive target p. One lock for all of it, taken on the miss path only: a miss already pays a disk read
/// or a page clear.
/// </summary>
[MustDispose(Reason = "owns native rings and tables sized to the cache")]
internal sealed unsafe class ProbationPolicy
{
    /// <summary>Probation entries a take tries before it leaves the allocation to the clock.</summary>
    internal const int TakeTries = 16;

    /// <summary>Referenced pages one take may send round again before it takes one regardless: the lock's hold stays bounded.</summary>
    internal const int SecondChancesPerTake = 64;

    /// <summary>Stale entries one pop drops before it gives up for this take: the lock's hold stays bounded, the next pops carry on.</summary>
    internal const int StaleSkipsPerPop = 256;

    /// <summary>Entries a placement's compaction step scans: the incremental compaction's pace (<see cref="Enqueue"/>).</summary>
    private const int CompactionStep = 8;

    private readonly PagedMMF _owner;
    private AccessControlSmall _lock;

    // The target and its rails: floor N/256, ceiling N/2, start N/16. Rails, not the operating point: the ghost hits move it between them.
    private double _target;
    private readonly double _floor;
    private readonly double _ceiling;

    // The FIFO of (slot << 32 | file page), a power-of-two ring. Entries go stale when their page is promoted or reclaimed; they are skipped at the head,
    // and compacted away incrementally once the ring is three quarters full: the live entries of [head, _compactWrite) are compacted, [_compactWrite,
    // _compactRead) is garbage, [_compactRead, tail) not yet scanned. A pop reaching the compacted part's end drops the compaction.
    private long* _queue;
    private readonly long _queueMask;
    private long _head;
    private long _tail;
    private bool _compacting;
    private long _compactWrite;
    private long _compactRead;

    private GhostList _area;    // B1
    private GhostList _clock;   // B2

    internal long AreaGhostHits;
    internal long ClockGhostHits;
    internal long SecondChances;
    internal long Compactions;

    /// <summary>Entries in the queue, stale ones included (tests, diagnostics).</summary>
    internal long QueueLength => _tail - _head;

    /// <summary>Ghosts in each list (tests, diagnostics).</summary>
    internal (int Area, int Clock) GhostCounts => (_area.Count, _clock.Count);

    internal ProbationPolicy(PagedMMF owner, int slots)
    {
        _owner = owner;
        _floor = Math.Max(1, slots / 256);
        _ceiling = Math.Max(_floor, slots / 2);
        _target = Math.Clamp(slots / 16, _floor, _ceiling);

        var queueCapacity = (long)BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, slots)) * 2;
        _queueMask = queueCapacity - 1;
        // native-alloc: the probation FIFO, two entries per slot, freed in Dispose with the cache
        _queue = (long*)NativeMemory.Alloc((nuint)(queueCapacity * sizeof(long)));
        _area = new GhostList(slots);
        _clock = new GhostList(slots);
    }

    /// <summary>The area's target size p, in pages.</summary>
    internal int Target => (int)Volatile.Read(ref _target);

    /// <summary>
    /// Set at the first eviction and never cleared: the cache has been full. Until then probation grows into the free slots. A flag, not a count of
    /// allocations, which an int would wrap after 2^31 misses.
    /// </summary>
    internal bool CacheFilled => Volatile.Read(ref _cacheFilled);

    private bool _cacheFilled;

    /// <summary>Records a page a slot gave up outside a placement (the reserve's stocking).</summary>
    internal void RecordEvicted(int evictedFilePage, byte evictedTag)
    {
        if (evictedFilePage < 0)
        {
            return;
        }

        _lock.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            AddGhost(evictedFilePage, evictedTag);
        }
        finally
        {
            _lock.ExitExclusiveAccess();
        }
    }

    /// <summary>The ghost of a page a slot gave up, in the list of the set that let it go; under the lock.</summary>
    private void AddGhost(int evictedFilePage, byte evictedTag)
    {
        Volatile.Write(ref _cacheFilled, true);
        if (evictedTag != 0)
        {
            _area.Add(evictedFilePage, evictedTag);
        }
        else
        {
            _clock.Add(evictedFilePage, 0);
        }
    }

    /// <summary>
    /// The placement of a miss on <paramref name="filePage"/> by the operation tagged <paramref name="opTag"/>, after its slot gave up
    /// <paramref name="evictedFilePage"/> (-1 for a free slot) tagged <paramref name="evictedTag"/>. Returns 0 for the clock, the operation's tag for
    /// probation; a probation placement is queued at <paramref name="memPageIndex"/>.
    /// </summary>
    internal byte Place(int filePage, byte opTag, int evictedFilePage, byte evictedTag, int memPageIndex, PagedMMF.PageInfo slot)
    {
        _lock.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            // ARC's order: the incoming page's ghost first, then the replacement's. Recorded first, the page the slot gave up could age out of a full
            // list the very ghost this miss is about to look for.
            var tag = Decide(filePage, opTag);
            if (evictedFilePage >= 0)
            {
                AddGhost(evictedFilePage, evictedTag);
            }

            if (tag != 0)
            {
                slot.SetProbationTag(tag);
                Enqueue(((long)memPageIndex << 32) | (uint)filePage);
            }

            return tag;
        }
        finally
        {
            _lock.ExitExclusiveAccess();
        }
    }

    /// <summary>The incoming page's placement, its ghost hit adapting the target; under the lock.</summary>
    private byte Decide(int filePage, byte opTag)
    {
        // Outside any operation — the checkpoint, a fence, recovery — a load goes to the clock as it always did.
        if (opTag == 0)
        {
            _area.TryRemove(filePage, out _);
            _clock.TryRemove(filePage, out _);
            return 0;
        }

        if (_area.TryRemove(filePage, out var loaderTag))
        {
            // B1: the area let it go too early. ARC's step: by the ratio of the clock's ghosts to the area's, at least 1.
            AreaGhostHits++;
            _target = Math.Min(_ceiling, _target + Math.Max(1.0, (double)_clock.Count / Math.Max(1, _area.Count)));
            return loaderTag != opTag ? (byte)0 : opTag;   // asked again by another operation: a real re-reference, straight to the clock
        }

        if (_clock.TryRemove(filePage, out _))
        {
            // B2: the clock let it go too early.
            ClockGhostHits++;
            _target = Math.Max(_floor, _target - Math.Max(1.0, (double)_area.Count / Math.Max(1, _clock.Count)));
            return 0;
        }

        return opTag;
    }

    /// <summary>
    /// Pops the oldest live entry whose page has no reference bit; false when none is left, or after <see cref="StaleSkipsPerPop"/> stale entries. A live
    /// page with its bit set goes back to the tail, its bit consumed, while <paramref name="secondChances"/> lasts: CAR's T1 clock.
    /// </summary>
    internal bool TryPopOldest(ref int secondChances, out long entry)
    {
        _lock.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            var stale = 0;
            while (true)
            {
                if (_compacting && _head == _compactWrite)
                {
                    _head = _compactRead;   // the compacted part is consumed: skip the garbage, drop the compaction
                    _compacting = false;
                }

                if (_head == _tail || stale == StaleSkipsPerPop)
                {
                    entry = 0;
                    return false;
                }

                entry = _queue[_head & _queueMask];
                _head++;
                var slot = (int)(entry >> 32);
                if (!_owner.IsQueuedProbationLive(slot, (int)entry))
                {
                    stale++;
                    continue;
                }

                var verdict = secondChances > 0 ? _owner.ConsumeProbationReference(slot) : PagedMMF.ProbationVerdict.None;
                if (verdict != PagedMMF.ProbationVerdict.None)
                {
                    secondChances--;
                    if (verdict == PagedMMF.ProbationVerdict.SecondLap)
                    {
                        SecondChances++;
                        Enqueue(entry);
                    }

                    continue;   // promoted: the clock's now, its entry stale
                }

                return true;
            }
        }
        finally
        {
            _lock.ExitExclusiveAccess();
        }
    }

    /// <summary>Puts a probation page the protections kept back at the tail.</summary>
    internal void Requeue(int slot, int filePage)
    {
        _lock.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            Enqueue(((long)slot << 32) | (uint)filePage);
        }
        finally
        {
            _lock.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Appends at the tail, under the lock. Past three quarters of the ring, each append also compacts a few entries — stale ones out, live ones in
    /// order — so the work is spread over the appends instead of one scan of the whole ring under the lock, which at 2^24 slots takes seconds.
    /// </summary>
    private void Enqueue(long entry)
    {
        var used = _tail - _head;
        if (!_compacting && used >= (_queueMask + 1) / 4 * 3)
        {
            _compacting = true;
            _compactWrite = _head;
            _compactRead = _head;
        }

        if (_compacting)
        {
            // Full: the compaction runs to its end now. Live entries number at most the slots, half the ring, so it frees a quarter at least.
            CompactStep(used > _queueMask ? long.MaxValue : CompactionStep);
        }

        // Still full only if live entries outnumber the slots: a slot reloaded on probation while its old entry is still queued leaves two live entries
        // for it. The oldest goes; its page is taken a little early, which costs a reload.
        if (_tail - _head > _queueMask)
        {
            _head++;
        }

        _queue[_tail++ & _queueMask] = entry;
    }

    private void CompactStep(long budget)
    {
        for (; budget > 0 && _compactRead != _tail; budget--)
        {
            var e = _queue[_compactRead & _queueMask];
            _compactRead++;
            if (_owner.IsQueuedProbationLive((int)(e >> 32), (int)e))
            {
                _queue[_compactWrite++ & _queueMask] = e;
            }
        }

        if (_compactRead == _tail)
        {
            _tail = _compactWrite;
            _compacting = false;
            Compactions++;
        }
    }

    internal void Dispose()
    {
        if (_queue != null)
        {
            NativeMemory.Free(_queue);
            _queue = null;
        }

        _area.Dispose();
        _clock.Dispose();
    }

    /// <summary>
    /// A ghost list: the file pages a set let go most recently, at most <c>capacity</c> of them, oldest out first, with the probation tag each had. An
    /// open-addressing table (linear probing, backward-shift delete) answers membership; a ring of file pages ages the oldest out. The table has a
    /// power of two of 8-byte entries, at least twice the capacity, and the ring 4 bytes an entry: 20 to 36 bytes per slot of the cache.
    /// </summary>
    internal struct GhostList
    {
        // Table entry: key = file page + 1 (0 = empty) in the high 32 bits, value = (ring position & 0xFFFFFF) | tag << 24 in the low 32.
        private long* _table;
        private int _tableMask;
        private int _shift;
        private int* _ring;
        private int _capacity;
        private int _next;     // the ring position the next add takes
        private int _count;

        internal GhostList(int slots)
        {
            _capacity = Math.Min(Math.Max(16, slots), 1 << 24);
            var tableSize = (int)BitOperations.RoundUpToPowerOf2((uint)_capacity * 2);
            _tableMask = tableSize - 1;
            _shift = 32 - BitOperations.Log2((uint)tableSize);
            // native-alloc: a ghost list's table and ring, sized to the cache, freed in Dispose with the cache
            _table = (long*)NativeMemory.AllocZeroed((nuint)tableSize * sizeof(long));
            _ring = (int*)NativeMemory.AllocZeroed((nuint)_capacity * sizeof(int));
            _next = 0;
            _count = 0;
        }

        internal readonly int Count => _count;

        internal void Add(int filePage, byte tag)
        {
            // The ring position this add takes held the oldest ghost: it ages out, unless removed or re-added since.
            var pos = _next;
            var old = _ring[pos] - 1;
            if (old >= 0)
            {
                var i = Find(old);
                if (i >= 0 && (int)(_table[i] & 0xFFFFFF) == pos)
                {
                    Delete(i);
                }
            }

            var existing = Find(filePage);
            if (existing >= 0)
            {
                Delete(existing);
            }

            _ring[pos] = filePage + 1;
            _next = pos + 1 == _capacity ? 0 : pos + 1;
            Insert(filePage, ((long)tag << 24) | (uint)pos);
        }

        internal bool TryRemove(int filePage, out byte tag)
        {
            var i = Find(filePage);
            if (i < 0)
            {
                tag = 0;
                return false;
            }

            tag = (byte)((_table[i] >> 24) & 0xFF);
            Delete(i);
            return true;
        }

        private readonly int Home(int filePage) => (int)(((uint)(filePage + 1) * 0x9E3779B1u) >> _shift);

        private readonly int Find(int filePage)
        {
            var key = (long)(filePage + 1) << 32;
            for (var i = Home(filePage); ; i = (i + 1) & _tableMask)
            {
                var e = _table[i];
                if (e == 0)
                {
                    return -1;
                }

                if ((e & unchecked((long)0xFFFFFFFF00000000)) == key)
                {
                    return i;
                }
            }
        }

        private void Insert(int filePage, long value)
        {
            var entry = ((long)(filePage + 1) << 32) | (value & 0xFFFFFFFF);
            var i = Home(filePage);
            while (_table[i] != 0)
            {
                i = (i + 1) & _tableMask;
            }

            _table[i] = entry;
            _count++;
        }

        /// <summary>Backward-shift deletion: no tombstones, so a miss stops at the first empty entry.</summary>
        private void Delete(int i)
        {
            _count--;
            var j = i;
            while (true)
            {
                j = (j + 1) & _tableMask;
                var e = _table[j];
                if (e == 0)
                {
                    break;
                }

                var home = Home((int)(e >> 32) - 1);
                // Move e back into the hole when its home is not between the hole (exclusive) and its slot (inclusive), cyclically.
                var between = i <= j ? home > i && home <= j : home > i || home <= j;
                if (!between)
                {
                    _table[i] = e;
                    i = j;
                }
            }

            _table[i] = 0;
        }

        internal void Dispose()
        {
            if (_table != null)
            {
                NativeMemory.Free(_table);
                _table = null;
            }

            if (_ring != null)
            {
                NativeMemory.Free(_ring);
                _ring = null;
            }
        }
    }
}
