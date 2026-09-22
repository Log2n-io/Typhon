using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Allocates the network identities sessions use to name entities, and tracks the generation that makes a reused identity observable as a leave followed by
/// an enter. <b>One per database, not one per archetype.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the identity space is global.</b> The wire protocol states it outright — "entities are global dense u32 netIds"
/// (<c>design/Subscriptions/03-wire-protocol.md § 1</c>) — and two things depend on it. An <c>entityRef</c> is a bare netId that "may name an entity
/// outside the view", so it arrives with no archetype context to disambiguate it; and an event is encoded ONCE into a tick arena and memcpy'd into every
/// matching session's block precisely "because netIds are global, the bytes are identical for every receiver" (§ 7). A per-archetype space breaks the second
/// outright: the same number would mean different entities to different readers, so the shared bytes could not be shared. This was per-archetype until #955's
/// follow-up, which is the bug that reading the protocol against the code turned up.
/// </para>
/// <para>
/// <b>Why a released identity is quarantined for a tick.</b> The free list is LIFO, so without this an identity released during tick N is the FIRST one
/// handed out during tick N. A session that can see both entities then receives, in one frame, the new entity's enter and the old entity's leave — and the
/// frame application order applies leaves LAST (<c>03-wire-protocol.md § 5</c>), so the leave lands on the entity that just entered and an event between them
/// resolves to the wrong one. The generation does not save this: it makes reuse observable ACROSS ticks, which is SUB-06's job, but both records here carry
/// the same tick and the client has no ordering to recover. Releases therefore go to a quarantine list and <see cref="DrainQuarantine"/> splices it onto the
/// free list at the start of the next tick, so an identity is never reissued in the tick it was released.
/// </para>
/// <para>
/// <b>Why a live identity is marked, not merely absent.</b> <see cref="_nextFree"/> distinguishes live, free-with-a-successor, and end-of-list. Collapsing
/// "live" and "end of list" onto one value makes a double <see cref="Release"/> undetectable — the second release threads the identity to itself and every
/// later <see cref="Allocate"/> returns that same one, handing one identity to two live holders while <see cref="LiveCount"/> runs negative. A quarantined
/// identity is equally not-live, so the same check rejects a double release before the quarantine is ever drained.
/// </para>
/// <para>
/// <b>Why a flat array is right here, where it is wrong for the directory.</b> Identities are dense by construction: they come from a counter and are
/// recycled, so the high-water mark is the PEAK number of simultaneously watched entities, not a persisted address space. That keeps the side arrays bounded
/// by the watched set, which is what SUB-13 requires — unlike a cluster chunk id, which is a file address and would size an array by what the database holds.
/// </para>
/// <para>
/// <b>Thread safety: none, by contract.</b> Touched only at the replication track's single-threaded points. The design's per-worker identity blocks are an
/// optimisation for the projection pass and land with it; a per-worker lease is a slice off this allocator, not a different one — which is also why going
/// global costs no parallelism.
/// </para>
/// </remarks>
internal sealed class NetIdAllocator : ResourceNode, IMemoryResource
{
    /// <summary>Reserved: <c>0</c> means "no identity", so it is never handed out and never carries a generation.</summary>
    public const uint NoNetId = 0;

    /// <summary>End of a list. Distinct from <see cref="LiveMarker"/> so "free and last" is not confused with "not free at all".</summary>
    private const uint FreeListEnd = uint.MaxValue;

    /// <summary>
    /// Marks an identity as live, i.e. on neither the free list nor the quarantine. Safe as <c>0</c> because identity 0 is reserved and can therefore never
    /// be a successor.
    /// </summary>
    private const uint LiveMarker = 0;

    private ushort[] _generations;
    private uint[] _nextFree;

    private uint _freeHead = FreeListEnd;

    // Quarantine, as a ring of per-tick buckets (D1). A release joins the current bucket; DrainQuarantine rotates the ring and splices the bucket that is now
    // the oldest onto the free list, so an identity is held for exactly as many ticks as there are buckets. Each bucket is a head/tail pair threaded through
    // the same _nextFree links, which keeps both the append and the splice constant-time.
    private readonly uint[] _bucketHeads;
    private readonly uint[] _bucketTails;
    private int _currentBucket;
    private int _quarantineCount;

    private uint _highWaterMark;
    private int _liveCount;
    private bool _disposed;
    private ReplicationThreadAffinity _affinity;

    /// <summary>Creates the database's identity allocator.</summary>
    /// <param name="id">Stable resource id.</param>
    /// <param name="parent">Resource-graph parent, typically the runtime node.</param>
    /// <param name="initialCapacity">Identities addressable before the first growth.</param>
    /// <param name="quarantineTicks">
    /// How many ticks a released identity is held before it can be reissued. One is the minimum that keeps a leave and an enter out of the same frame;
    /// <c>SubscriptionsOptions.CloseAfterSkips + 1</c> is what the runtime passes, so no identity can be reused while a session that might still be holding
    /// it is alive (D1).
    /// </param>
    /// <remarks>
    /// <b>Why the window is the skip window and not one tick.</b> A session skipped for K ticks receives its next frame with everything that happened since
    /// its baseline, and a one-tick hold lets an identity be released on tick N and reissued on N+1 — so that one frame can carry the leave of the old holder
    /// and the enter of the new one for the same number, with no ordering the client can recover. Holding for the whole window a session may be skipped for
    /// makes that impossible by construction: an identity cannot be reissued while any session could still owe a frame naming its previous holder.
    /// </remarks>
    public NetIdAllocator(string id, IResource parent, int initialCapacity = 256, int quarantineTicks = 1)
        : base(Require(id, nameof(id)), ResourceType.Node, Require(parent, nameof(parent)))
    {
        if (initialCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity, "Initial capacity must be positive");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(quarantineTicks, 1);

        // +1 so index 0 exists and stays reserved, keeping netId usable directly as an array index.
        _generations = new ushort[initialCapacity + 1];
        _nextFree = new uint[initialCapacity + 1];
        _bucketHeads = new uint[quarantineTicks];
        _bucketTails = new uint[quarantineTicks];
        for (var i = 0; i < quarantineTicks; i++)
        {
            _bucketHeads[i] = FreeListEnd;
            _bucketTails[i] = FreeListEnd;
        }
    }

    /// <summary>Argument validation usable from a base-constructor argument, where a statement cannot run.</summary>
    private static T Require<T>(T value, string name) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return value;
    }

    /// <summary>Identities currently allocated.</summary>
    public int LiveCount => _liveCount;

    /// <summary>Identities handed out and given back since start, and how many hand-outs reused a quarantined id rather than minting a fresh one.</summary>
    /// <remarks>
    /// <b>This is the denominator for the frame stage's stale leaves.</b> A reissued netId is a leave and an enter for every session that held it
    /// (SUB-06), so a world recycling identities quickly pays wire traffic proportional to the recycle rate times the watching population, whatever its
    /// entities are doing. Written only under the allocator's thread affinity, so a read from elsewhere is a snapshot rather than a torn value.
    /// </remarks>
    public (long Minted, long Released, long Reused) IdentityFlow => (_minted, _released, _reused);

    private long _minted;
    private long _released;
    private long _reused;

    /// <summary>Identities released and not yet reissuable, across every bucket of the quarantine ring.</summary>
    public int QuarantinedCount => _quarantineCount;

    /// <summary>How many ticks a released identity is held before it can be reissued.</summary>
    public int QuarantineTicks => _bucketHeads.Length;

    /// <summary>Largest identity ever handed out. Tracks the PEAK watched count, and never falls.</summary>
    public uint HighWaterMark => _highWaterMark;

    /// <summary>Identities the side arrays can currently address without growing.</summary>
    public int Capacity => _generations.Length - 1;

    /// <summary>
    /// Approximate bytes held by the two side arrays: a 2-byte generation and a 4-byte link per addressable identity.
    /// </summary>
    /// <remarks>
    /// Reported by this node itself rather than by each archetype's replication state, which is what going global requires: the allocator is shared, so N
    /// archetypes each adding these bytes to their own total would report the same memory N times. <see cref="IMemoryResource"/> requires a node to exclude
    /// what it does not solely own, and after the move no archetype owns this.
    /// <para>
    /// Like the directory's, this memory follows the PEAK watched count and never shrinks — <see cref="HighWaterMark"/> does not fall and there is no
    /// compaction pass.
    /// </para>
    /// </remarks>
    public long EstimatedBytes => ((long)_generations.Length * 2L) + ((long)_nextFree.Length * 4L) + 64L;

    /// <inheritdoc />
    public int EstimatedMemorySize
    {
        get
        {
            var bytes = EstimatedBytes;
            return bytes > int.MaxValue ? int.MaxValue : (int)bytes;
        }
    }

    /// <summary>Takes an identity, preferring a recycled one so the space stays dense.</summary>
    /// <returns>An identity ≥ 1; never <see cref="NoNetId"/>.</returns>
    public uint Allocate()
    {
        _affinity.Enter(nameof(NetIdAllocator), nameof(Allocate));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            uint netId;

            // Deliberately does NOT fall back to the quarantine when the free list is empty. Minting a fresh identity is the correct answer there: reaching
            // into the quarantine is precisely the same-tick reuse this exists to prevent, and it would trade a bounded rise in the high-water mark for a
            // client that resolves a leave onto the wrong entity.
            if (_freeHead != FreeListEnd)
            {
                netId = _freeHead;
                _freeHead = _nextFree[netId];
                _reused++;
            }
            else
            {
                netId = _highWaterMark + 1;
                if (netId > (uint)Capacity)
                {
                    Grow();
                }
                _highWaterMark = netId;
            }

            _nextFree[netId] = LiveMarker;
            _liveCount++;
            _minted++;
            return netId;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>
    /// Bumps <paramref name="netId"/>'s generation and puts it in quarantine, from which the next <see cref="DrainQuarantine"/> makes it reissuable.
    /// </summary>
    /// <remarks>
    /// The bump happens on RELEASE rather than on the next allocate, so a session still holding the identity sees a changed pair immediately rather than only
    /// once the identity happens to be reissued — an entity that leaves and is never replaced must still read as gone.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The identity was never issued by this allocator.</exception>
    /// <exception cref="ArgumentException">The identity is already free or quarantined — releasing twice would hand it to two live holders.</exception>
    public void Release(uint netId)
    {
        _affinity.Enter(nameof(NetIdAllocator), nameof(Release));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (netId == NoNetId || netId > _highWaterMark)
            {
                throw new ArgumentOutOfRangeException(nameof(netId), netId, "Not an identity this allocator has issued");
            }

            if (_nextFree[netId] != LiveMarker)
            {
                throw new ArgumentException($"Identity {netId} is already free or quarantined; releasing it twice would issue it to two holders at once.",
                    nameof(netId));
            }

            unchecked
            {
                _generations[netId]++;
            }

            // Appended to the CURRENT bucket's tail, not pushed onto the free list. FIFO within a bucket is incidental — every entry in one becomes
            // reissuable at the same moment — but a tail pointer is what lets the drain splice in O(1).
            _nextFree[netId] = FreeListEnd;
            var bucket = _currentBucket;
            if (_bucketTails[bucket] == FreeListEnd)
            {
                _bucketHeads[bucket] = netId;
            }
            else
            {
                _nextFree[_bucketTails[bucket]] = netId;
            }

            _bucketTails[bucket] = netId;
            _quarantineCount++;
            _liveCount--;
            _released++;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>
    /// Makes every identity released before this call reissuable. Called once per tick, before the replication track dispatches.
    /// </summary>
    /// <remarks>
    /// The one-tick delay is the whole point: an identity released during tick N becomes reissuable at the start of tick N+1, so no frame can carry both the
    /// leave of its old holder and the enter of its new one. Calling this in the middle of a tick would reopen exactly the window it closes.
    /// </remarks>
    public void DrainQuarantine()
    {
        _affinity.Enter(nameof(NetIdAllocator), nameof(DrainQuarantine));
        try
        {
            // Returns silently on a disposed allocator rather than throwing, UNLIKE Allocate and Release above, and the asymmetry is the point: this is the
            // only member called unconditionally on the tick path, every tick, from OnTickEndInternal. Disposing a runtime mid-tick leaves a tick already past
            // the shutdown check still dispatching, so a throw here surfaces to the host as a fence failure on a tick that was merely being torn down — the
            // exact shape of the disposed-signal bug SchedulerDisposeRaceTests was written for. Allocate and Release keep their guards: they run only when the
            // track has work, so reaching them after disposal is genuine misuse worth hearing about.
            if (_disposed)
            {
                return;
            }

            // Rotate first: the bucket the cursor lands on is the one filled a full ring ago, and it becomes this tick's bucket once it has been emptied.
            _currentBucket = _currentBucket + 1 == _bucketHeads.Length ? 0 : _currentBucket + 1;

            var head = _bucketHeads[_currentBucket];
            if (head == FreeListEnd)
            {
                return;
            }

            _quarantineCount -= CountOf(head);
            _nextFree[_bucketTails[_currentBucket]] = _freeHead;
            _freeHead = head;
            _bucketHeads[_currentBucket] = FreeListEnd;
            _bucketTails[_currentBucket] = FreeListEnd;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>How many identities a bucket's chain holds, walked only when that bucket is being spliced out.</summary>
    /// <param name="head">The bucket's first identity.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    /// A walk rather than a per-bucket counter because the count exists only for telemetry and for the tests that assert nothing leaks, while a counter would
    /// have to be kept correct on every release. The chain being walked is the one about to be spliced onto the free list, so its cache lines are wanted
    /// anyway.
    /// </remarks>
    private int CountOf(uint head)
    {
        var count = 0;
        for (var at = head; at != FreeListEnd; at = _nextFree[at])
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The generation currently associated with <paramref name="netId"/>. An identity never issued reads <c>0</c>, which is also a fresh identity's
    /// generation — the value is only meaningful as one half of the <c>(netId, generation)</c> pair a session compares between ticks.
    /// </summary>
    public ushort GenerationOf(uint netId)
    {
        if (netId == NoNetId || netId >= (uint)_generations.Length)
        {
            return 0;
        }

        return _generations[netId];
    }

    /// <summary>
    /// Doubles the addressable capacity. Called only while the watched set is still growing.
    /// </summary>
    /// <remarks>
    /// The <see cref="_nextFree"/> copy is load-bearing, not defensive: free AND quarantined entries carry their successors through it, so dropping it would
    /// sever both lists at the growth boundary and strand every recycled identity.
    /// </remarks>
    private void Grow()
    {
        var newLength = (Capacity * 2) + 1;

        var generations = new ushort[newLength];
        Array.Copy(_generations, generations, _generations.Length);

        var nextFree = new uint[newLength];
        Array.Copy(_nextFree, nextFree, _nextFree.Length);

        _generations = generations;
        _nextFree = nextFree;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        base.Dispose(disposing);
        Parent?.RemoveChild(this);
    }
}
