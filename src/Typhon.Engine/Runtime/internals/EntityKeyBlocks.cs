using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-archetype generation state for <see cref="EntityKeyBlocks"/> (#1099), one cache line each because the CAS that publishes a generation is the one
/// contended write in the whole key path.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct ArchetypeKeyGenerations
{
    /// <summary>CAS gate over publishing a generation for this archetype: 0 free, 1 held.</summary>
    [FieldOffset(0)]
    public int Claiming;

    /// <summary>Generations published for this archetype this tick. Read with <see cref="Volatile"/>; it is what a loser waits on.</summary>
    [FieldOffset(4)]
    public int Published;
}

/// <summary>
/// Hands a parallel producer a run of entity keys it can return as final <see cref="EntityId"/>s, without one atomic per spawn and without the key
/// depending on who got there first (#1099).
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape.</b> Per archetype per tick, one <see cref="Interlocked.Add(ref long, long)"/> of <c>Stride × BlockSize</c> on the archetype's own
/// <c>NextEntityKey</c> reserves a generation; the winner publishes its base and the losers acquire-read it. A producer identified by
/// <c>chunkIndex</c> then owns <c>[base + chunkIndex × BlockSize, base + (chunkIndex+1) × BlockSize)</c> and bump-allocates from it with no
/// synchronisation at all. Its n-th spawn of that archetype this tick is <c>base(g) + chunkIndex × BlockSize + n</c> — a pure function of
/// (archetype, tick, generation, chunkIndex, n).
/// </para>
/// <para>
/// <b>Strided by <c>ChunkIndex</c>, NOT by worker slot, and this is load-bearing.</b> Striding by slot couples the key to which worker happened to pick
/// up which chunk, which varies tick to tick and with the worker count — so the same seeded scenario would produce different ids on a different machine.
/// <c>ChunkIndex</c> is a property of the work, not of the thread that ran it.
/// </para>
/// <para>
/// <b>Why the cursor is per (archetype, chunk) and not per (archetype, chunk, system).</b> Two different chunked systems in one tick can both spawn into
/// one archetype from the same chunk index. Deriving the key from the chunk index alone would hand both the same key. The cursor therefore persists
/// across systems within the tick: the second system's spawns continue where the first's left off, and the sequence stays deterministic because the DAG's
/// order over systems is.
/// </para>
/// <para>
/// <b>No tail reclamation, deliberately.</b> Keys are monotonic per archetype and never recycled (<see cref="EntityId"/>), and the entity table is a hash
/// map keyed by the key rather than an array indexed by it, so an unissued tail costs no slots and no probing — only numbers out of 2^48 per archetype.
/// Bevy's "effectively a memory leak" objection to per-thread id blocks is about an index-array layout Typhon does not have. The burn is bounded by the
/// <c>Stride × BlockSize ≤ 1024</c> product cap, which is what keeps it under 0.6 % of an archetype's space per year at a continuous 50 Hz.
/// </para>
/// </remarks>
internal sealed class EntityKeyBlocks
{
    /// <summary>
    /// Hard ceiling on generations per archetype per tick, whatever the budget asks for — a backstop against a corrupt count spinning this loop, not the
    /// operating limit. The operating limit is <see cref="_maxGenerations"/>, derived from the declared budget.
    /// </summary>
    internal const int GenerationCeiling = 256;

    /// <summary>Floor on a block, for the same reason <c>EventQueue</c> floors a segment at 16: below it, a burst spends more on generations than on keys.</summary>
    internal const int MinBlockSize = 16;

    /// <summary>
    /// Keys one archetype's generation reserves: <c>Stride × BlockSize</c>. Kept as documentation of the figure the burn arithmetic below is stated in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This used to CAP <see cref="DeriveBlockSize"/>, and the cap was a silent functional limit rather than the safety measure it was described as.</b>
    /// With <c>Stride × BlockSize ≤ 1024</c> and a stride of 8, a block was 128 keys; against the old eight-generation ceiling that made 1 024 the most any
    /// one producer could ever queue in a tick, regardless of <c>EntityCommandsPerTick</c>. A <c>CallbackSystem</c> is ONE chunk, so one producer, so a
    /// 3 000-entity burst from one silently lost 1 976 of its entities. Measured: 102 400 entities created from 100 bursts of 3 000, which is exactly
    /// 100 × 1 024.
    /// </para>
    /// <para>
    /// <b>The burn the cap was protecting against is real but was priced against the wrong workload.</b> A generation reserves <c>Stride × BlockSize</c>
    /// keys and a single active producer uses one block of it, so striding wastes <c>Stride - 1</c> blocks per generation — and it wastes them exactly when
    /// only one chunk is spawning, which is the serial case. The old arithmetic then assumed that happening on EVERY tick forever. A burst workload is not
    /// that: 3 000 entities from one producer every 21 ticks at 50 Hz takes three generations of 8 × 1 024, so 24 576 keys about 2.4 times a second, which
    /// is 0.55 % of one archetype's 2^48 per year and roughly 180 years to exhaustion. Sustained flat out on every tick it would be far worse — which is
    /// what <c>EntityCommandsPerTick</c> is now honestly the knob for, since the burn scales with it.
    /// </para>
    /// <para>
    /// <b>And the waste disappears when the work is parallel</b>, which is the case the striding exists for: eight chunks each taking their own block out of
    /// one generation reserve nothing they do not issue.
    /// </para>
    /// </remarks>
    internal const int DocumentedBurnPerGeneration = 1024;

    private readonly DatabaseEngine _engine;

    /// <summary>Producers the stride covers — <c>ChunkIndex</c> is required to be below this, and refused rather than wrapped if it is not.</summary>
    private readonly int _stride;

    private readonly int _blockSize;

    /// <summary>Generations a producer may take this tick, derived so that <c>_maxGenerations × BlockSize</c> covers the declared per-tick budget.</summary>
    private readonly int _maxGenerations;

    // Per-archetype publication state, one line each. Indexed by INTERNAL archetype id.
    private ArchetypeKeyGenerations[] _archetypes;

    // Published bases: [archetype * _maxGenerations + generation]. Written once per (archetype, generation) under the Claiming gate and read with
    // Volatile.Read by everyone else, which is the release/acquire pair the project's arm64 ordering rule asks for on cross-thread publication.
    private long[] _bases;

    // Per-producer bump cursors, CHUNK-MAJOR: [chunkIndex * _archetypeStride + archetype]. One packed long per (chunk, archetype): generation in the high
    // 32 bits, keys used in the low 32.
    //
    // PACKED AND CAS'd, which the first version was not, and that was a duplicate-id defect rather than a missed optimisation. It read `used`, computed a
    // key and wrote `used + count` back with plain loads and stores, resting on chunk-index disjointness — but the scheduler guarantees disjoint worker
    // SLOTS, and says nothing about chunk indices ACROSS systems. Two systems with no DAG edge between them run concurrently, and a CallbackSystem is one
    // chunk, so two of them both see ChunkIndex 0, both read used == 0, and both return the same key. Two identical EntityIds, then two rows in the
    // EntityMap under one key. The pair has to move together, hence one long rather than two ints.
    //
    // Chunk-major and 8 longs to a region so one producer's cursors are contiguous and two producers never share a 64-byte line — the earlier `& ~7` on
    // INTS padded to 32 bytes and let adjacent chunk indices, which are exactly the concurrent producers, share one (rule MD-03).
    private long[] _cursors;
    private int _archetypeStride;

    internal EntityKeyBlocks(DatabaseEngine engine, int stride, int blockSize, int commandsPerTick)
    {
        _engine = engine;
        _stride = Math.Max(1, stride);
        _blockSize = blockSize;

        // One more than the budget needs, so a producer that reaches the budget exactly is not refused by a rounding boundary. Clamped to the backstop.
        _maxGenerations = Math.Clamp(((commandsPerTick + blockSize - 1) / blockSize) + 1, 1, GenerationCeiling);

        // After _maxGenerations: Resize strides _bases by it.
        Resize(16);
    }

    /// <summary>Producers this allocator strides over. A <c>ChunkIndex</c> at or above it is refused.</summary>
    internal int Stride => _stride;

    /// <summary>Keys one producer gets per generation.</summary>
    internal int BlockSize => _blockSize;

    /// <summary>Generations one producer may take this tick. <c>MaxGenerations × BlockSize</c> is what a single producer can queue.</summary>
    internal int MaxGenerations => _maxGenerations;

    /// <summary>The most one producer can queue into one archetype in one tick — what <c>EntityCommandsPerTick</c> now actually means for a single chunk.</summary>
    internal int MaxKeysPerProducerPerTick => _maxGenerations * _blockSize;

    /// <summary>
    /// Derives the per-producer block from the declared per-tick command budget: the budget divided by the stride, floored, and never above the budget
    /// itself.
    /// </summary>
    /// <remarks>
    /// The division by the stride is the useful part — it is the share a producer gets when every chunk is spawning, which is the case the striding is for.
    /// What is NOT here any more is the <c>Stride × BlockSize ≤ 1024</c> product cap; see <see cref="DocumentedBurnPerGeneration"/> for why that cap was a
    /// silent entity-losing limit rather than the protection it was documented as.
    /// </remarks>
    internal static int DeriveBlockSize(int entityCommandsPerTick, int stride)
    {
        var s = Math.Max(1, stride);
        return Math.Clamp(entityCommandsPerTick / s, MinBlockSize, Math.Max(MinBlockSize, entityCommandsPerTick));
    }

    private void Resize(int archetypeCount)
    {
        // 8 longs per 64-byte line, so a region padded to a multiple of 8 never shares a line with the next chunk's.
        _archetypeStride = (Math.Max(8, archetypeCount) + 7) & ~7;
        _archetypes = new ArchetypeKeyGenerations[Math.Max(8, archetypeCount)];

        // Strided by the OPERATING generation count, not the backstop. At the ceiling this array was 4 096 x 256 longs = 8 MB, because the engine's routing
        // table is a fixed 4 096 entries whatever the schema holds — and Reset cleared all of it every tick, which cost more than the work the feature does.
        _bases = new long[_archetypes.Length * _maxGenerations];
        _cursors = new long[_stride * _archetypeStride];
    }

    /// <summary>
    /// Grows the tables to cover <paramref name="archetypeCount"/> archetypes. Called from bind and from tick start, never from a producer — a producer
    /// that meets an id past the end is refused and counted, because growing a shared array from a parallel chunk is rule MD-02's own prohibition.
    /// </summary>
    internal void EnsureArchetypes(int archetypeCount)
    {
        if (archetypeCount > _archetypes.Length)
        {
            Resize(archetypeCount);
        }
    }

    /// <summary>
    /// Archetypes these tables need to cover: the registered high-water mark, not the routing table's fixed width.
    /// </summary>
    /// <remarks>
    /// <c>DatabaseEngine._archetypeStates</c> is always <c>RoutingTableSize</c> entries — 4 096 — regardless of how many archetypes a schema declares, so
    /// sizing from its <c>Length</c> made this feature's footprint a property of a constant rather than of the workload.
    /// </remarks>
    internal static int ArchetypesToCover(DatabaseEngine engine) => engine == null ? 16 : Math.Max(16, ArchetypeRegistry.MaxArchetypeId + 1);

    /// <summary>Clears the per-tick state. Serial, at tick start, beside the command buffer's own reset.</summary>
    internal void Reset()
    {
        // _bases is deliberately NOT cleared: a base is only ever read for a generation that Published says exists, and Published is cleared here, so a
        // stale base is unreachable. Clearing it was most of a megabytes-per-tick memset that bought nothing.
        Array.Clear(_archetypes);
        Array.Clear(_cursors);
    }

    /// <summary>
    /// Reserves <paramref name="count"/> consecutive keys for <paramref name="chunkIndex"/> in <paramref name="internalArchetypeId"/>, returning the
    /// first. Negative means refused — the producer is outside the stride, the archetype is past the table, the request is larger than a whole block, or
    /// the archetype has used its generation ceiling for this tick.
    /// </summary>
    /// <remarks>
    /// A request is never split across generations. A <c>SpawnMany</c> that would straddle a block boundary takes a fresh generation and leaves the tail
    /// of the old block unissued, which keeps the returned run contiguous — the property that lets <c>SpawnMany</c> fill a caller span with one run
    /// rather than two.
    /// </remarks>
    internal long Reserve(int internalArchetypeId, int chunkIndex, int count)
    {
        if ((uint)chunkIndex >= (uint)_stride || (uint)internalArchetypeId >= (uint)_archetypes.Length || count < 1 || count > _blockSize)
        {
            return -1;
        }

        // The archetype's own state has to exist before TryEnsureGeneration indexes it. Checked here rather than left to the caller: the bound above is on
        // THIS class's tables, which a Resize floor can make wider than the engine's, and the method's contract is its own.
        if (_engine?._archetypeStates == null || internalArchetypeId >= _engine._archetypeStates.Length)
        {
            return -1;
        }

        var cursor = chunkIndex * _archetypeStride + internalArchetypeId;

        while (true)
        {
            var packed = Volatile.Read(ref _cursors[cursor]);
            var generation = (int)(packed >> 32);
            var used = (int)packed;

            // Two independent questions, in this order. FIRST: does this cursor's generation have a published base at all? A cursor starts at (generation 0,
            // used 0) whether or not anyone has published generation 0 yet, so "used == 0" does not answer it — the archetype's published count does.
            if (Volatile.Read(ref _archetypes[internalArchetypeId].Published) <= generation && !TryEnsureGeneration(internalArchetypeId, generation))
            {
                return -1;
            }

            // SECOND: does the run fit in what is left of the block? A request is never split across generations, so a SpawnMany that would straddle the
            // boundary moves to a fresh block and leaves the tail unissued. That is what lets it fill a caller span with one contiguous run.
            var first = used;
            if (used + count > _blockSize)
            {
                generation++;
                if (generation >= _maxGenerations || !TryEnsureGeneration(internalArchetypeId, generation))
                {
                    return -1;
                }

                first = 0;
            }

            // One CAS publishes the new (generation, used) pair. A loser retries and re-reads, so two concurrent producers on one cursor hand out disjoint
            // runs rather than the same one.
            var next = ((long)generation << 32) | (uint)(first + count);
            if (Interlocked.CompareExchange(ref _cursors[cursor], next, packed) == packed)
            {
                return _bases[internalArchetypeId * _maxGenerations + generation] + ((long)chunkIndex * _blockSize) + first;
            }
        }
    }

    /// <summary>
    /// Makes sure generation <paramref name="generation"/> of <paramref name="internalArchetypeId"/> has a published base, taking it if nobody has.
    /// Exactly one producer performs the <see cref="Interlocked.Add(ref long, long)"/>; the others wait for the release store.
    /// </summary>
    private bool TryEnsureGeneration(int internalArchetypeId, int generation)
    {
        ref var arch = ref _archetypes[internalArchetypeId];
        if (Volatile.Read(ref arch.Published) > generation)
        {
            return true;
        }

        var state = _engine._archetypeStates[internalArchetypeId];
        if (state == null)
        {
            return false;
        }

        var waiter = new AdaptiveWaiter();
        while (true)
        {
            if (Volatile.Read(ref arch.Published) > generation)
            {
                return true;
            }

            if (Interlocked.CompareExchange(ref arch.Claiming, 1, 0) == 0)
            {
                try
                {
                    // Re-check under the gate: a racing producer may have published while we were acquiring it.
                    var published = arch.Published;
                    if (published > generation)
                    {
                        return true;
                    }

                    // Generations are taken in order, so a producer asking for g can only ever be asking for `published`. Anything else means a cursor
                    // skipped a generation, which would hand out keys nobody reserved.
                    if (published != generation)
                    {
                        return false;
                    }

                    var span = (long)_stride * _blockSize;
                    var first = Interlocked.Add(ref state.NextEntityKey, span) - span + 1;
                    _bases[internalArchetypeId * _maxGenerations + generation] = first;

                    // Release: the base must be visible before the count that advertises it.
                    Volatile.Write(ref arch.Published, generation + 1);
                    return true;
                }
                finally
                {
                    Volatile.Write(ref arch.Claiming, 0);
                }
            }

            waiter.Wait();
        }
    }

    /// <summary>Generations taken for one archetype this tick — telemetry. One is the healthy figure; more means the block is under-derived for the workload.</summary>
    internal int GenerationsTaken(int internalArchetypeId) =>
        (uint)internalArchetypeId < (uint)_archetypes.Length ? Volatile.Read(ref _archetypes[internalArchetypeId].Published) : 0;

    /// <summary>The base this archetype's generation <paramref name="generation"/> was published at. Tests only — it is what makes the id formula checkable.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long BaseOf(int internalArchetypeId, int generation) => _bases[internalArchetypeId * _maxGenerations + generation];
}
