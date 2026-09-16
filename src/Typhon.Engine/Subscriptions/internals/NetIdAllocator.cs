using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Allocates the network identities sessions use to name entities, and tracks the generation that makes a reused identity observable as a leave followed by
/// an enter. One per replicated archetype.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a generation exists at all.</b> Identities are recycled — a watched entity stops being watched, its id returns to the free list, and the next
/// entity to be watched gets it back. A session that missed the release would otherwise see the same id carry different data and conclude the entity moved
/// rather than that it was replaced. Bumping the generation on reuse makes the pair <c>(netId, generation)</c> change, so every session resolves the reuse as
/// a leave and a fresh enter (SUB-06). Sessions compare the pair between consecutive ticks; they do not require it to be unique for the process lifetime,
/// which is why a 16-bit generation is enough — a wrap needs 65 536 reuses of the same id, and a session that has been absent that long has been resynced.
/// </para>
/// <para>
/// <b>Why a live identity is marked, not merely absent.</b> <see cref="_nextFree"/> distinguishes three states, and it has to: an id that is live, an id that
/// is free with a successor, and the free list's end. Collapsing "live" and "end of list" onto one value makes a double <see cref="Release"/> undetectable —
/// the second release threads the id to itself and every later <see cref="Allocate"/> returns that same identity forever, handing one id to two live holders
/// while <see cref="LiveCount"/> runs negative. That is the precise failure SUB-06 exists to prevent, and it is silent, so the three states are kept
/// distinct and a double release is rejected in O(1).
/// </para>
/// <para>
/// <b>Why a flat array is right here, where it is wrong for the directory.</b> Identities are dense by construction: they come from a counter and are
/// recycled through a free list, so the high-water mark is the PEAK number of simultaneously watched entities, not a persisted address space. That keeps the
/// side arrays bounded by the watched set, which is what SUB-13 requires — unlike a cluster chunk id, which is a file address and would size an array by what
/// the database holds. The distinction is the reason the directory is a map and this is not.
/// </para>
/// <para>
/// <b>Thread safety: none, by contract.</b> Like the pool and the directory, this is touched only at the replication track's single-threaded points. The
/// design's per-worker identity blocks are an optimisation for the projection pass and land with it; a per-worker lease is a slice off this allocator, not a
/// different one.
/// </para>
/// <para>
/// <b>Allocation.</b> The two side arrays double on growth, which happens only while the watched set is still growing. In the steady state — and across any
/// amount of allocate/release churn at a stable watched count — this allocates nothing, per SUB-07.
/// </para>
/// </remarks>
internal sealed class NetIdAllocator
{
    /// <summary>Reserved: <c>0</c> means "no identity", so it is never handed out and never carries a generation.</summary>
    public const uint NoNetId = 0;

    /// <summary>End of the free list. Distinct from <see cref="LiveMarker"/> so "free and last" is not confused with "not free at all".</summary>
    private const uint FreeListEnd = uint.MaxValue;

    /// <summary>
    /// Marks an identity as live, i.e. NOT on the free list. Safe as <c>0</c> because identity 0 is reserved and can therefore never be a successor.
    /// </summary>
    private const uint LiveMarker = 0;

    private ushort[] _generations;
    private uint[] _nextFree;

    private uint _freeHead = FreeListEnd;
    private uint _highWaterMark;
    private int _liveCount;
    private ReplicationThreadAffinity _affinity;

    /// <summary>Creates an allocator with room for <paramref name="initialCapacity"/> identities before its first growth.</summary>
    public NetIdAllocator(int initialCapacity = 256)
    {
        if (initialCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity, "Initial capacity must be positive");
        }

        // +1 so index 0 exists and stays reserved, keeping netId usable directly as an array index.
        _generations = new ushort[initialCapacity + 1];
        _nextFree = new uint[initialCapacity + 1];
    }

    /// <summary>Identities currently allocated.</summary>
    public int LiveCount => _liveCount;

    /// <summary>Largest identity ever handed out. Tracks the PEAK watched count, and never falls.</summary>
    public uint HighWaterMark => _highWaterMark;

    /// <summary>Identities the side arrays can currently address without growing.</summary>
    public int Capacity => _generations.Length - 1;

    /// <summary>
    /// Approximate bytes held by the two side arrays, so the owner can report them to the resource graph.
    /// </summary>
    /// <remarks>
    /// Six bytes per addressable identity — a 2-byte generation and a 4-byte free-list link. Like the directory's, this memory follows the PEAK watched
    /// count and never shrinks: <see cref="HighWaterMark"/> does not fall and there is no compaction pass.
    /// </remarks>
    public long EstimatedBytes => ((long)_generations.Length * 2L) + ((long)_nextFree.Length * 4L) + 64L;

    /// <summary>Takes an identity, preferring a recycled one so the space stays dense.</summary>
    /// <returns>An identity ≥ 1; never <see cref="NoNetId"/>.</returns>
    public uint Allocate()
    {
        _affinity.Enter(nameof(NetIdAllocator), nameof(Allocate));
        try
        {
            uint netId;

            if (_freeHead != FreeListEnd)
            {
                netId = _freeHead;
                _freeHead = _nextFree[netId];
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
            return netId;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>
    /// Returns an identity to the free list and bumps its generation, so the next holder is observed as a different entity.
    /// </summary>
    /// <remarks>
    /// The bump happens on RELEASE rather than on the next allocate, so that a session still holding the released identity sees a changed pair immediately
    /// rather than only once the identity happens to be reissued — an entity that leaves and is never replaced must still read as gone.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The identity was never issued by this allocator.</exception>
    /// <exception cref="ArgumentException">The identity is already free — releasing twice would hand it to two live holders.</exception>
    public void Release(uint netId)
    {
        _affinity.Enter(nameof(NetIdAllocator), nameof(Release));
        try
        {
            if (netId == NoNetId || netId > _highWaterMark)
            {
                throw new ArgumentOutOfRangeException(nameof(netId), netId, "Not an identity this allocator has issued");
            }

            if (_nextFree[netId] != LiveMarker)
            {
                throw new ArgumentException($"Identity {netId} is already free; releasing it twice would issue it to two holders at once.", nameof(netId));
            }

            unchecked
            {
                _generations[netId]++;
            }

            _nextFree[netId] = _freeHead;
            _freeHead = netId;
            _liveCount--;
        }
        finally
        {
            _affinity.Exit();
        }
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
    /// The <see cref="_nextFree"/> copy is load-bearing, not defensive: free entries carry their successors, so dropping it would sever the free list at the
    /// growth boundary and strand every recycled identity.
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
}
