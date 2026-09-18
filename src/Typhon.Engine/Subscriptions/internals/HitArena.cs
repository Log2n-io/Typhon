using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>Flags on one <see cref="InterestRun"/>.</summary>
internal static class InterestRunFlags
{
    /// <summary>Nothing to say about this run.</summary>
    internal const ushort None = 0;

    /// <summary>
    /// The cluster carried no replication block when the run was recorded, so the run's <see cref="InterestRun.Block"/> is null and the cluster is on the
    /// worker's new-block list. The blocks step creates it; until it has, nothing in this run is watched.
    /// </summary>
    internal const ushort NoBlock = 1 << 0;
}

/// <summary>
/// One run of hits: every entity of one cluster that one session's interest reached this tick, as a slot bitmask.
/// </summary>
/// <remarks>
/// <para>
/// <b>A run, not a hit, is the unit — and that is a deliberate deviation from the ≈ 8 B per hit of
/// <c>design/Subscriptions/09-phase1-build-plan.md § P1-12</c>.</b> The series already requires hits to arrive grouped by cluster so the directory is probed
/// once per run (<c>design/Subscriptions/foundation/02-replication-state-blocks.md § 4</c>); a cluster holds at most 64 entities and one session's interest
/// reaches a given entity at most once, so the grouping is exactly representable as a 64-bit mask. That makes the run self-describing at 24 B for 1-64 hits —
/// ≈ 1.1 B per hit at SWG's <c>N = 21</c>, against 8 B — and, more importantly, it makes the watched-bit update ONE atomic per cluster instead of one per
/// entity (see <see cref="InterestPass"/>).
/// </para>
/// <para>
/// What the mask cannot carry is per-hit data, which Phase 1 has none of: the enter budget's "nearest first" ordering
/// (<c>design/Subscriptions/01-model.md § 4</c>) is a Phase 2 sphere-observer concern and wants a distance per hit. When it arrives it is a side array
/// parallel to the mask's set bits, not a reason to widen every run of a <c>World</c> observer that will never have a distance.
/// </para>
/// <para>
/// <see cref="Block"/> is an <see cref="nint"/> rather than a <c>ReplicationBlockHeader*</c> so the run can live in an ordinary managed array. It addresses a
/// <see cref="ReplicationBlockPool"/> slab, which is native memory that outlives the tick; an <see cref="nint"/> held inside a managed array is not a pointer
/// into that array, which is the same reasoning <see cref="ReplicationDirectory"/> gives for its own storage.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 24)]
internal struct InterestRun
{
    /// <summary>The cluster's replication block, or zero when it has none yet (<see cref="InterestRunFlags.NoBlock"/>).</summary>
    public nint Block;

    /// <summary>Bit <c>i</c> is set when slot <c>i</c> of the cluster is a hit for the session this run belongs to.</summary>
    public ulong Slots;

    /// <summary>The cluster's chunk id.</summary>
    public int ChunkId;

    /// <summary>Index of the archetype's compiled plan, so the consumer reaches the layout without a lookup by name.</summary>
    public ushort ArchetypeIndex;

    /// <summary><see cref="InterestRunFlags"/>.</summary>
    public ushort Flags;

    /// <summary>How many entities this run carries.</summary>
    public readonly int HitCount => BitOperations.PopCount(Slots);
}

/// <summary>
/// One worker's scratch for a tick of S2a: the runs it produced, the clusters it found with no block, and the blocks it marked watched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per worker, so nothing here is shared and nothing here is synchronized.</b> A chunk owns its arena for the whole of its execution; the only memory two
/// chunks touch in common is the replication block header, whose watched mask is updated with one atomic per cluster.
/// </para>
/// <para>
/// <b>It grows once and is then reused for the life of the runtime</b> (SUB-07). Every buffer is cleared rather than reallocated at
/// <see cref="BeginTick"/>, so after the first few ticks a pass allocates nothing at all — which is what <c>InterestPassTests</c> asserts by measuring the
/// driving thread's allocations across two identical passes.
/// </para>
/// <para>
/// <b>The counters are written once per chunk, not once per run.</b> <see cref="InterestPass"/> accumulates them in locals and flushes through
/// <see cref="Note"/> at the end of its chunk. Arenas are separate small objects that the allocator may well place on one cache line, so a counter moved per
/// cluster would be a line ping-ponging between every worker for the whole stage.
/// </para>
/// </remarks>
internal sealed class HitArena
{
    private InterestRun[] _runs = new InterestRun[256];
    private int _runCount;

    /// <summary>Clusters this worker hit that carry no block yet, packed <c>(archetypeIndex &lt;&lt; 32) | chunkId</c>.</summary>
    private readonly List<long> _newBlocks = [];

    /// <summary>
    /// Membership test for <see cref="_newBlocks"/>, so one worker asks for a cluster's block once however many of its sessions hit it.
    /// </summary>
    /// <remarks>
    /// Touched only on a directory MISS, which is a genuinely new cluster and therefore rare after the first tick. Duplicates would not be a correctness
    /// problem — <see cref="ArchetypeReplicationState.TryAttachBlock"/> refuses a second block for a cluster that already has one — but at 110 sessions they
    /// would be a 110× list, and the blocks step is single-threaded.
    /// </remarks>
    private readonly HashSet<long> _newBlockSeen = [];

    /// <summary>Blocks this worker was the first to mark watched this tick; the next tick's prologue clears their masks.</summary>
    private readonly List<nint> _watchedBlocks = [];

    /// <summary>Runs recorded so far this tick.</summary>
    public int RunCount => _runCount;

    /// <summary>Directory probes this arena's worker made last tick — one per cluster run, never one per hit.</summary>
    public long DirectoryProbes { get; private set; }

    /// <summary>Hits this arena's worker recorded last tick.</summary>
    public long Hits { get; private set; }

    /// <summary>Chunks this worker executed with the engine's EW-01 window open — the observation <c>InterestEpochScopeTests</c> reads.</summary>
    public long ChunksInsideOpenWindow { get; private set; }

    /// <summary>
    /// The clusters this worker found with no block. The blocks step creates them; each entry is <c>(archetypeIndex &lt;&lt; 32) | chunkId</c>.
    /// </summary>
    public IReadOnlyList<long> NewBlocks => _newBlocks;

    private int[] _sphereChunks = new int[16];
    private ulong[] _sphereMasks = new ulong[16];
    private int _sphereCount;

    /// <summary>The blocks this worker marked watched this tick.</summary>
    public IReadOnlyList<nint> WatchedBlocks => _watchedBlocks;

    /// <summary>
    /// Resets the arena for a new tick. Called single-threaded, from the stage's <c>Prepare</c>, after the prologue has read the watched list.
    /// </summary>
    public void BeginTick()
    {
        _runCount = 0;
        _newBlocks.Clear();
        _newBlockSeen.Clear();
        _watchedBlocks.Clear();
        DirectoryProbes = 0;
        Hits = 0;
        ChunksInsideOpenWindow = 0;
    }

    /// <summary>
    /// Starts gathering one archetype's sphere hits for one session.
    /// </summary>
    /// <remarks>
    /// <b>The scratch lives here because an arena is per worker.</b> A sphere query reports hits grouped by cluster only if the spatial index happens to
    /// walk them that way, which is its property and not this pass's, so the hits are merged into one run per cluster before any of them is recorded. Holding
    /// that merge state on the pass itself would be a buffer shared by every worker resolving a session at the same time; holding it on the arena gives each
    /// worker its own, which is the same ownership every other buffer here has.
    /// </remarks>
    public void BeginSphere() => _sphereCount = 0;

    /// <summary>Merges one sphere hit into the run for its cluster.</summary>
    /// <param name="chunkId">The cluster the hit is in.</param>
    /// <param name="slot">Its slot within the cluster.</param>
    public void AddSphereHit(int chunkId, int slot)
    {
        if (chunkId < 0 || (uint)slot >= 64u)
        {
            return;
        }

        var bit = 1UL << slot;
        for (var i = 0; i < _sphereCount; i++)
        {
            if (_sphereChunks[i] == chunkId)
            {
                _sphereMasks[i] |= bit;
                return;
            }
        }

        if (_sphereCount == _sphereChunks.Length)
        {
            Array.Resize(ref _sphereChunks, Math.Max(16, _sphereChunks.Length * 2));
            Array.Resize(ref _sphereMasks, _sphereChunks.Length);
        }

        _sphereChunks[_sphereCount] = chunkId;
        _sphereMasks[_sphereCount] = bit;
        _sphereCount++;
    }

    /// <summary>How many distinct clusters the sphere reached.</summary>
    public int SphereCount => _sphereCount;

    /// <summary>The cluster of one gathered run.</summary>
    /// <param name="index">Its position.</param>
    /// <returns>The chunk id.</returns>
    public int SphereChunk(int index) => _sphereChunks[index];

    /// <summary>The slots of one gathered run.</summary>
    /// <param name="index">Its position.</param>
    /// <returns>The mask.</returns>
    public ulong SphereMask(int index) => _sphereMasks[index];

    /// <summary>Records one cluster run.</summary>
    /// <param name="archetypeIndex">Index of the archetype's compiled plan.</param>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="block">The cluster's replication block, or zero when it has none.</param>
    /// <param name="slots">The hit slots, which is never zero — an empty cluster is skipped before a run is opened.</param>
    /// <param name="flags"><see cref="InterestRunFlags"/>.</param>
    public void AddRun(int archetypeIndex, int chunkId, nint block, ulong slots, ushort flags)
    {
        if (_runCount == _runs.Length)
        {
            Array.Resize(ref _runs, _runs.Length * 2);
        }

        ref var run = ref _runs[_runCount++];
        run.Block = block;
        run.Slots = slots;
        run.ChunkId = chunkId;
        run.ArchetypeIndex = (ushort)archetypeIndex;
        run.Flags = flags;
    }

    /// <summary>Asks the blocks step for a block on <paramref name="chunkId"/>, once per cluster per worker per tick.</summary>
    /// <param name="archetypeIndex">Index of the archetype's compiled plan.</param>
    /// <param name="chunkId">The cluster.</param>
    public void AddNewBlock(int archetypeIndex, int chunkId)
    {
        var packed = ((long)archetypeIndex << 32) | (uint)chunkId;
        if (_newBlockSeen.Add(packed))
        {
            _newBlocks.Add(packed);
        }
    }

    /// <summary>Records that this worker was the one that took <paramref name="block"/> from unwatched to watched this tick.</summary>
    /// <param name="block">The block.</param>
    public void AddWatchedBlock(nint block) => _watchedBlocks.Add(block);

    /// <summary>Publishes a chunk's accumulated counters. Called once per chunk, never per run.</summary>
    /// <param name="probes">Directory probes the chunk made.</param>
    /// <param name="hits">Hits the chunk recorded.</param>
    /// <param name="insideOpenWindow"><see langword="true"/> when the chunk ran with the engine's EW-01 window open.</param>
    public void Note(long probes, long hits, bool insideOpenWindow)
    {
        DirectoryProbes += probes;
        Hits += hits;
        if (insideOpenWindow)
        {
            ChunksInsideOpenWindow++;
        }
    }

    /// <summary>A window onto the runs of one session, as the consumer reads them back.</summary>
    /// <param name="start">First run.</param>
    /// <param name="count">How many.</param>
    /// <returns>The runs.</returns>
    public ReadOnlySpan<InterestRun> Runs(int start, int count) => new(_runs, start, count);

    /// <summary>The archetype index a packed new-block entry names.</summary>
    /// <param name="packed">The entry.</param>
    /// <returns>The archetype's plan index.</returns>
    public static int NewBlockArchetype(long packed) => (int)(packed >> 32);

    /// <summary>The chunk id a packed new-block entry names.</summary>
    /// <param name="packed">The entry.</param>
    /// <returns>The cluster's chunk id.</returns>
    public static int NewBlockChunkId(long packed) => (int)packed;
}
