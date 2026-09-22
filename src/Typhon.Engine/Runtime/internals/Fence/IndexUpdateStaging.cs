using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-archetype staging for the tick fence's IndexMassUpdate phase: where the Migrate phase writes the <c>(key, oldValue, newValue)</c> triples it used to
/// apply inline, and where the IndexMassUpdate phase reads them from (#872 step 6, §5.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape: one raw byte buffer per (Migrate chunk × indexed field).</b> A chunk owns its buffers exclusively for the life of the phase, so appending needs
/// no lock and no interlocked reservation — the same reason <c>FenceMigrateExecSystem</c>'s per-chunk <c>DirtyBitDelta</c> buffers exist, and it is sized in
/// <c>Prepare</c> for the same reason too: growing an array from concurrent workers is how that buffer lost a bucket before it was fixed.
/// </para>
/// <para>
/// <b>Raw bytes rather than a typed entry struct</b>, because the entry's layout belongs to the tree: a 4-byte key packs to 8 bytes with its value while an
/// 8-byte key pads to 16, and a <c>String64</c> key is 64 bytes on its own. The stride comes from <c>BTreeBase.BulkEntryStride</c> and nothing here interprets
/// what it stores.
/// </para>
/// </remarks>
internal sealed class IndexUpdateStaging
{
    private const int InitialBufferBytes = 4 * 1024;

    /// <summary>Flattened (slot, field) coordinates, one entry per indexed field of the archetype.</summary>
    internal readonly struct FieldRef
    {
        public readonly int SlotIndex;
        public readonly int FieldIndex;

        public FieldRef(int slotIndex, int fieldIndex)
        {
            SlotIndex = slotIndex;
            FieldIndex = fieldIndex;
        }
    }

    private readonly FieldRef[] _fields;

    // [chunkIndex * _slotStride + fieldId]. The stride is FieldCount rounded UP to 16 ints, so one chunk's counters own whole 64-byte cache lines and no
    // two chunks share one. Without the padding — and with FieldCount == 1, the common shape — slot == chunkIndex, putting sixteen concurrently-written
    // counters on a single line: `_byteCounts[slot] = count + stride` runs once per migrant per field from every Migrate worker at once, so that line would
    // ping-pong between cores for the whole phase. The waste is ~5 KB of int and ~6 KB of null array references at 45 chunks.
    private const int CountersPerLine = 64 / sizeof(int);

    private byte[][] _buffers = [];
    private int[] _byteCounts = [];
    private readonly int _slotStride;
    private int _chunkCapacity;

    // Per-chunk ping-pong partner for the radix sort, shared by the chunk's fields — the same worker sorts them one after another — and grown
    // geometrically to the largest run the chunk has seen.
    private byte[][] _sortScratch = [];

    // The merged, sorted buffer per field, reused tick over tick, plus the ping-pong partner the pairwise merge alternates with.
    private readonly byte[][] _merged;
    private readonly byte[][] _scratch;

    // Run boundaries within the ping-pong buffers, in BYTES. Reused across fields and ticks.
    private int[] _runOffsets = new int[16];
    private int[] _runLengths = new int[16];
    private int[] _nextOffsets = new int[16];
    private int[] _nextLengths = new int[16];

    // What the exec system's Prepare produced for this tick, per field: the sorted buffer's fill level, the leaf-snapped part boundaries in ENTRY units, and
    // the stride the boundaries are measured in. Read by the plan when it emits work items and by the workers when they apply them.
    private readonly int[] _preparedBytes;
    private readonly int[][] _boundaries;
    private readonly int[] _partCounts;
    private readonly int[] _strides;

    internal int FieldCount => _fields.Length;

    internal FieldRef Field(int fieldId) => _fields[fieldId];

    /// <summary>
    /// This tick's sorted, merged buffer for one field. Valid only between the phase Prepare that set it and the next <see cref="BeginTick"/>.
    /// </summary>
    internal byte[] Prepared(int fieldId) => _merged[fieldId];

    /// <summary>Parts this field's batch was split into, or zero when it staged nothing.</summary>
    internal int PartCount(int fieldId) => _partCounts[fieldId];

    /// <summary>Entry-index boundaries of the parts: part <c>p</c> is entries <c>[Boundaries(f)[p], Boundaries(f)[p + 1])</c>.</summary>
    internal int[] Boundaries(int fieldId) => _boundaries[fieldId];

    /// <summary>Bytes per staged entry for this field, as the tree reported it.</summary>
    internal int Stride(int fieldId) => _strides[fieldId];

    internal IndexUpdateStaging(FieldRef[] fields)
    {
        _fields = fields ?? [];
        _slotStride = ((_fields.Length + CountersPerLine - 1) / CountersPerLine) * CountersPerLine;
        _merged = new byte[_fields.Length][];
        _scratch = new byte[_fields.Length][];
        _preparedBytes = new int[_fields.Length];
        _boundaries = new int[_fields.Length][];
        _partCounts = new int[_fields.Length];
        _strides = new int[_fields.Length];
    }

    /// <summary>
    /// Records the outcome of this tick's merge, sort and leaf-snapped partition for one field.
    /// </summary>
    internal void SetPrepared(int fieldId, int byteCount, int stride, int[] boundaries, int partCount)
    {
        _preparedBytes[fieldId] = byteCount;
        _strides[fieldId] = stride;
        _boundaries[fieldId] = boundaries;
        _partCounts[fieldId] = partCount;
    }

    /// <summary>Forgets this tick's prepared state so an empty next tick cannot re-dispatch it.</summary>
    internal void ClearPrepared()
    {
        Array.Clear(_partCounts);
        Array.Clear(_preparedBytes);
    }

    /// <summary>Scratch boundary array for one field, grown to hold <paramref name="desiredParts"/> + 1 offsets and reused tick over tick.</summary>
    internal int[] RentBoundaries(int fieldId, int desiredParts)
    {
        var existing = _boundaries[fieldId];
        if (existing == null || existing.Length < desiredParts + 1)
        {
            existing = new int[desiredParts + 1];
            _boundaries[fieldId] = existing;
        }

        return existing;
    }

    /// <summary>
    /// Sizes the per-chunk buffers for this tick's chunk count and clears their fill levels. Called from the Migrate phase's <c>Prepare</c>, which the
    /// scheduler runs single-threaded.
    /// </summary>
    internal void BeginTick(int chunkCount)
    {
        if (_fields.Length == 0)
        {
            return;
        }

        var chunks = Math.Max(chunkCount, 1);
        if (_sortScratch.Length < chunks)
        {
            var grownScratch = new byte[Math.Max(chunks, _sortScratch.Length * 2)][];
            Array.Copy(_sortScratch, grownScratch, _sortScratch.Length);
            _sortScratch = grownScratch;
        }

        var needed = chunks * _slotStride;
        if (_buffers.Length < needed)
        {
            var grown = new byte[Math.Max(needed, _buffers.Length * 2)][];
            Array.Copy(_buffers, grown, _buffers.Length);
            _buffers = grown;

            var grownCounts = new int[grown.Length];
            Array.Copy(_byteCounts, grownCounts, _byteCounts.Length);
            _byteCounts = grownCounts;
        }

        // Only the FieldCount live counters of each chunk's padded run are touched; the padding slots stay null and zero forever.
        for (var c = 0; c < Math.Max(chunkCount, 1); c++)
        {
            for (var f = 0; f < _fields.Length; f++)
            {
                var slot = c * _slotStride + f;
                _buffers[slot] ??= new byte[InitialBufferBytes];
                _byteCounts[slot] = 0;
            }
        }

        _chunkCapacity = Math.Max(chunkCount, 1);
    }

    /// <summary>Reserves <paramref name="stride"/> bytes in one chunk's buffer for one field and returns where to write them.</summary>
    /// <remarks>
    /// Returns a span rather than a pointer: the buffer is a managed array that may be replaced by the growth below, and a pointer handed out before a grow
    /// would address the old one. The caller writes through the tree's own <c>WriteBulkEntry</c>, which is the only code that knows the layout.
    /// </remarks>
    internal Span<byte> Reserve(int chunkIndex, int fieldId, int stride)
    {
        var slot = chunkIndex * _slotStride + fieldId;
        var buffer = _buffers[slot];
        var count = _byteCounts[slot];

        if (count + stride > buffer.Length)
        {
            var grown = new byte[Math.Max(count + stride, buffer.Length * 2)];
            Array.Copy(buffer, grown, count);
            _buffers[slot] = grown;
            buffer = grown;
        }

        _byteCounts[slot] = count + stride;
        return buffer.AsSpan(count, stride);
    }

    /// <summary>
    /// One chunk's staged bytes for one field — what the worker that produced them sorts before it leaves the chunk.
    /// </summary>
    /// <remarks>
    /// A chunk's buffer is owned exclusively by the worker running that chunk, which is what makes sorting it there free of synchronisation and is the whole
    /// point: it moves the sort out of the serial <c>Prepare</c> and into the parallel region that was already running.
    /// </remarks>
    internal Span<byte> ChunkSpan(int chunkIndex, int fieldId)
    {
        var slot = chunkIndex * _slotStride + fieldId;
        return _buffers[slot].AsSpan(0, _byteCounts[slot]);
    }

    /// <summary>One chunk's sort scratch, at least <paramref name="byteCount"/> bytes: the radix sort's ping-pong partner for that chunk's runs.</summary>
    /// <remarks>Owned by the chunk like its buffers, so the worker sorting the chunk allocates nothing once the scratch has caught up with the run.</remarks>
    internal Span<byte> SortScratch(int chunkIndex, int byteCount)
    {
        var scratch = _sortScratch[chunkIndex];
        if (scratch == null || scratch.Length < byteCount)
        {
            scratch = new byte[Math.Max(Math.Max(byteCount, (scratch?.Length ?? 0) * 2), InitialBufferBytes)];
            _sortScratch[chunkIndex] = scratch;
        }

        return scratch.AsSpan(0, byteCount);
    }

    /// <summary>Total bytes staged for one field across every chunk this tick.</summary>
    internal int StagedBytes(int fieldId)
    {
        var total = 0;
        for (var c = 0; c < _chunkCapacity; c++)
        {
            total += _byteCounts[c * _slotStride + fieldId];
        }

        return total;
    }

    /// <summary>
    /// Merges one field's per-chunk runs — each already key-sorted by the worker that produced it — into a single sorted buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the half of the phase that was serial, and it was 88 % of it.</b> Concatenating the runs and sorting the result is <c>O(n log n)</c> on one
    /// thread; measured on the real phase at 100 000 migrants it was ~6.3 ms against ~0.5 ms of parallel apply, which capped the whole phase at ~1.15x no
    /// matter how many workers it was given. Sorting each run in the worker that filled it and merging the sorted runs here is <c>O(n log W)</c> serial, with
    /// the <c>O((n/W) log (n/W))</c> sorts absorbed by a parallel region that was already running.
    /// </para>
    /// <para>
    /// <b>Pairwise passes rather than a W-way heap.</b> A heap pays a sift per element with unpredictable branches; linear merge passes have the same
    /// asymptotic cost, a much smaller constant, and no per-element data structure at all. The run count is the number of Migrate CHUNKS that staged
    /// anything, NOT the worker count — the planner sizes chunks by cost (<c>ceil(totalCost / 200us)</c>), so a 100 000-migrant tick produces ~45 runs and
    /// therefore ~6 passes whatever W is. This term grows with the workload, so it is where the phase's remaining serial cost lives.
    /// </para>
    /// <para>
    /// The result is left in <see cref="Prepared"/>'s buffer whichever side of the ping-pong it lands on, by swapping the two array references rather than
    /// copying it back.
    /// </para>
    /// </remarks>
    internal byte[] MergeSortedRuns(int fieldId, int stride, BTreeBase<PersistentStore> tree, bool multi, out int byteCount)
    {
        byteCount = StagedBytes(fieldId);
        EnsureBuffer(ref _merged[fieldId], byteCount);
        if (byteCount == 0)
        {
            return _merged[fieldId];
        }

        EnsureRunArrays(_chunkCapacity);

        // Pass 0 is the gather: lay the non-empty runs out back to back in the destination, recording where each begins.
        var dst = _merged[fieldId];
        var runCount = 0;
        var offset = 0;
        for (var c = 0; c < _chunkCapacity; c++)
        {
            var slot = c * _slotStride + fieldId;
            var n = _byteCounts[slot];
            if (n == 0)
            {
                continue;
            }

            Array.Copy(_buffers[slot], 0, dst, offset, n);
            _runOffsets[runCount] = offset;
            _runLengths[runCount] = n;
            runCount++;
            offset += n;
        }

        if (runCount <= 1)
        {
            return dst;   // one worker produced everything, and it arrived sorted
        }

        EnsureBuffer(ref _scratch[fieldId], byteCount);
        var src = dst;
        var other = _scratch[fieldId];

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

                var aLen = _runLengths[r];
                var bLen = _runLengths[r + 1];
                tree.MergeBulkRuns(
                    new ReadOnlySpan<byte>(src, _runOffsets[r], aLen),
                    new ReadOnlySpan<byte>(src, _runOffsets[r + 1], bLen),
                    new Span<byte>(other, outOffset, aLen + bLen),
                    multi);

                _nextOffsets[outCount] = outOffset;
                _nextLengths[outCount] = aLen + bLen;
                outOffset += aLen + bLen;
                outCount++;
            }

            (src, other) = (other, src);
            (_runOffsets, _nextOffsets) = (_nextOffsets, _runOffsets);
            (_runLengths, _nextLengths) = (_nextLengths, _runLengths);
            runCount = outCount;
        }

        // `src` now holds the merged result. Publish it as the field's prepared buffer by swapping references, not by copying.
        _merged[fieldId] = src;
        _scratch[fieldId] = other;
        return src;
    }

    private static void EnsureBuffer(ref byte[] buffer, int needed)
    {
        if (buffer == null || buffer.Length < needed)
        {
            buffer = new byte[Math.Max(needed, InitialBufferBytes)];
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
