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
    /// Generations one archetype may take in one tick before a producer is refused. A refusal here is counted as an overflow, not thrown: the real bound
    /// on a tick's spawns is <c>EntityCommandsPerTick</c>, and this ceiling exists so a runaway cannot quietly consume key space instead of being seen.
    /// </summary>
    internal const int MaxGenerationsPerTick = 8;

    /// <summary>Floor on a block, for the same reason <c>EventQueue</c> floors a segment at 16: below it, a burst spends more on generations than on keys.</summary>
    internal const int MinBlockSize = 16;

    /// <summary>
    /// Ceiling on <c>Stride × BlockSize</c>. Solving <c>Stride × BlockSize × 1.5768e9 ticks/year ≤ 0.01 × 2^48</c> gives 1 785; 1 024 is the power of two
    /// under it, worth ~0.57 % of one archetype's key space per year and ~174 years to exhaustion. The cap, not the block size, is what makes the scheme
    /// safe: derived uncapped from <c>EntityCommandsPerTick</c> it burns 36.8 % per year at stride 8 and 73.5 % at stride 32.
    /// </summary>
    internal const int KeyBlockBudgetPerTick = 1024;

    private readonly DatabaseEngine _engine;

    /// <summary>Producers the stride covers — <c>ChunkIndex</c> is required to be below this, and refused rather than wrapped if it is not.</summary>
    private readonly int _stride;

    private readonly int _blockSize;

    // Per-archetype publication state, one line each. Indexed by INTERNAL archetype id.
    private ArchetypeKeyGenerations[] _archetypes;

    // Published bases: [archetype * MaxGenerationsPerTick + generation]. Written once per (archetype, generation) under the Claiming gate and read with
    // Volatile.Read by everyone else, which is the release/acquire pair the project's arm64 ordering rule asks for on cross-thread publication.
    private long[] _bases;

    // Per-producer bump cursors, CHUNK-MAJOR: [chunkIndex * _archetypeStride + archetype]. Chunk-major so one producer's cursors are contiguous and two
    // producers never share a cache line (each chunk's region is padded up to a multiple of 64 bytes). Reset per tick.
    private int[] _cursorGeneration;
    private int[] _cursorUsed;
    private int _archetypeStride;

    internal EntityKeyBlocks(DatabaseEngine engine, int stride, int blockSize)
    {
        _engine = engine;
        _stride = Math.Max(1, stride);
        _blockSize = blockSize;
        Resize(16);
    }

    /// <summary>Producers this allocator strides over. A <c>ChunkIndex</c> at or above it is refused.</summary>
    internal int Stride => _stride;

    /// <summary>Keys one producer gets per generation.</summary>
    internal int BlockSize => _blockSize;

    /// <summary>
    /// Derives the per-producer block from the declared per-tick command budget, then caps the product. The floor can win over the cap at a high chunk
    /// count (64 chunks caps at 16, which is also the floor) and that is correct: many producers each spawning a little pay one extra generation, not a
    /// weakened guarantee.
    /// </summary>
    internal static int DeriveBlockSize(int entityCommandsPerTick, int stride)
    {
        var s = Math.Max(1, stride);
        var cap = Math.Max(MinBlockSize, KeyBlockBudgetPerTick / s);
        return Math.Clamp(entityCommandsPerTick / s, MinBlockSize, cap);
    }

    private void Resize(int archetypeCount)
    {
        // 8 cursors per 64-byte line, so pad each chunk's region to a multiple of 8 entries and no two chunks share a line.
        _archetypeStride = (Math.Max(8, archetypeCount) + 7) & ~7;
        _archetypes = new ArchetypeKeyGenerations[Math.Max(8, archetypeCount)];
        _bases = new long[_archetypes.Length * MaxGenerationsPerTick];
        _cursorGeneration = new int[_stride * _archetypeStride];
        _cursorUsed = new int[_stride * _archetypeStride];
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

    /// <summary>Clears the per-tick state. Serial, at tick start, beside the command buffer's own reset.</summary>
    internal void Reset()
    {
        Array.Clear(_archetypes);
        Array.Clear(_bases);
        Array.Clear(_cursorGeneration);
        Array.Clear(_cursorUsed);
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

        var cursor = chunkIndex * _archetypeStride + internalArchetypeId;
        var generation = _cursorGeneration[cursor];
        var used = _cursorUsed[cursor];

        // Two independent questions, in this order. FIRST: does this cursor's generation have a published base at all? A cursor starts at (generation 0,
        // used 0) whether or not anyone has published generation 0 yet, so "used == 0" does not answer it — the archetype's published count does.
        if (Volatile.Read(ref _archetypes[internalArchetypeId].Published) <= generation && !TryEnsureGeneration(internalArchetypeId, generation))
        {
            return -1;
        }

        // SECOND: does the run fit in what is left of the block? A request is never split across generations, so a SpawnMany that would straddle the
        // boundary moves to a fresh block and leaves the tail unissued. That is what lets it fill a caller span with one contiguous run.
        if (used + count > _blockSize)
        {
            generation++;
            if (generation >= MaxGenerationsPerTick || !TryEnsureGeneration(internalArchetypeId, generation))
            {
                return -1;
            }

            _cursorGeneration[cursor] = generation;
            used = 0;
        }

        _cursorUsed[cursor] = used + count;
        return _bases[internalArchetypeId * MaxGenerationsPerTick + generation] + ((long)chunkIndex * _blockSize) + used;
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
                    _bases[internalArchetypeId * MaxGenerationsPerTick + generation] = first;

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
    internal long BaseOf(int internalArchetypeId, int generation) => _bases[internalArchetypeId * MaxGenerationsPerTick + generation];
}
