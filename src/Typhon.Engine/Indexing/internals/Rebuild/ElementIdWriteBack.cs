using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Puts each multi-value entry's element id back in its entity's cluster tail, in location order (#1588, design §D7). The builds emit
/// <c>(location, element id)</c> in key order; the clusters need them in location order. So the pairs are handled as the keys are: collected in one
/// fixed buffer, sorted by location and spilled as verified runs, then merged back per field during one ascending walk of the clusters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded memory (RB-09).</b> Nothing here grows with the archetype: each build worker's pairs take one buffer and its sort scratch
/// (<see cref="PairCollector"/>); a field whose pairs never spilled keeps them as an in-memory run only while the fields kept so far fit their share; and
/// the apply merges each field's runs through a share of the read buffer it is given. More data is more runs, never a larger buffer.
/// </para>
/// <para>
/// Pair runs are <see cref="RebuildBlockKind.Pairs"/> blocks with their own run ids, read back through <see cref="RunCursor{TKey}"/>: every block is
/// verified and every run's count checked before a pair is used (RB-10). A pair is a <see cref="StagedEntry{TKey}"/> of <c>int</c> whose key is the
/// location and whose second field the element id, so the location sort is the keys' radix sort.
/// </para>
/// <para>
/// Every occupied slot's tail is written, for every persistent multi-value field: a slot with no pair gets 0, which is what an entry the sparse rule
/// skipped carries (IX-09). A pair naming a slot the walk does not find occupied is a broken invariant and throws. Each cluster is written through a
/// dirty ChangeSet accessor, once (PS-10).
/// </para>
/// </remarks>
internal sealed unsafe class ElementIdWriteBack : IDisposable
{
    internal const int PairStride = 8;

    /// <summary>Test hook: when positive, the pair buffer holds this many pairs, so a small archetype spills pair runs.</summary>
    internal static int PairBufferEntriesForTest { get; set; }

    private readonly int[] _sections;
    private readonly RebuildMemory _memory;
    private readonly Func<RebuildTempFile> _file;
    private readonly long _keepBytes;
    private readonly int _capacity;
    private readonly List<RunDescriptor>[] _runs;
    private readonly NativeBuffer[] _kept;
    private readonly Lock _keepLock = new();
    private long _keptBytes;
    private int _nextRunId;
    private int _spilledRuns;

    /// <param name="sections">The tail section of each persistent multi-value field, by column.</param>
    /// <param name="memory">The rebuild's memory.</param>
    /// <param name="pairBufferBytes">The bytes each collector's pair buffer takes; its sort scratch takes as many again.</param>
    /// <param name="keepBytes">The bytes the fields' unspilled pairs may keep in memory until the apply, all collectors together.</param>
    /// <param name="file">The temp file, created on first use; safe to call from several threads.</param>
    /// <param name="firstRunId">The run id of the first pair run; ids stay unique within the temp file.</param>
    public ElementIdWriteBack(int[] sections, RebuildMemory memory, long pairBufferBytes, long keepBytes, Func<RebuildTempFile> file, int firstRunId)
    {
        _sections = sections;
        _memory = memory;
        _file = file;
        _keepBytes = keepBytes;
        _capacity = PairBufferEntriesForTest > 0 ? PairBufferEntriesForTest : (int)Math.Max(1024, Math.Min(pairBufferBytes, int.MaxValue) / PairStride);
        _runs = new List<RunDescriptor>[sections.Length];
        _kept = new NativeBuffer[sections.Length];
        _nextRunId = firstRunId;
        for (var c = 0; c < sections.Length; c++)
        {
            _runs[c] = [];
        }
    }

    /// <summary>Pair runs spilled to the temp file.</summary>
    public int SpilledRuns => Volatile.Read(ref _spilledRuns);

    /// <summary>Tails written by <see cref="Apply"/>.</summary>
    public long TailsWritten { get; private set; }

    /// <summary>The bytes one collector takes: its pair buffer and its sort scratch.</summary>
    public long CollectorBytes => 2L * _capacity * PairStride;

    /// <summary>
    /// A pair collector for one build worker, with its own pair buffer and sort scratch, spilling through <paramref name="staging"/>. A column's pairs all
    /// go through the collector of the worker building its tree, so its runs are that worker's alone until the apply.
    /// </summary>
    public PairCollector CreateCollector(byte* staging, int stagingBytes) => new(this, staging, stagingBytes);

    /// <summary>
    /// Ends <paramref name="column"/>'s last <paramref name="count"/> pairs, sorted: kept in memory when the column never spilled and they fit what is left
    /// of the keep share, spilled otherwise.
    /// </summary>
    internal void End(int column, byte* pairs, int count, byte* staging, int stagingBytes)
    {
        if (count == 0)
        {
            return;
        }

        var bytes = (long)count * PairStride;
        if (_runs[column].Count == 0)
        {
            var keep = false;
            lock (_keepLock)
            {
                if (_keptBytes + bytes <= _keepBytes)
                {
                    _keptBytes += bytes;
                    keep = true;
                }
            }

            if (keep)
            {
                var kept = _kept[column] = _memory.Allocate(bytes, "IndexRebuild.ElementIdKept");
                new ReadOnlySpan<byte>(pairs, (int)bytes).CopyTo(kept.Span);
                _runs[column].Add(new RunDescriptor(kept.Pointer, count, Interlocked.Increment(ref _nextRunId) - 1));
                return;
            }
        }

        Spill(column, pairs, count, staging, stagingBytes);
    }

    /// <summary>Spills <paramref name="count"/> sorted pairs of <paramref name="column"/> as one run.</summary>
    internal void Spill(int column, byte* pairs, int count, byte* staging, int stagingBytes)
    {
        _runs[column].Add(_file().Write(new ReadOnlySpan<byte>(pairs, count * PairStride), PairStride, count, RebuildBlockKind.Pairs,
            Interlocked.Increment(ref _nextRunId) - 1, new Span<byte>(staging, stagingBytes)));
        Interlocked.Increment(ref _spilledRuns);
    }

    internal int Capacity => _capacity;

    internal RebuildMemory Memory => _memory;

    /// <summary>The next run id after the pair runs.</summary>
    public int NextRunId => Volatile.Read(ref _nextRunId);

    /// <summary>
    /// Writes every occupied slot's tail in one ascending walk of the clusters, each field's pairs merged back from its runs through an equal share of
    /// <paramref name="readBuffer"/>; a field with more runs than its share holds blocks is first merged down into longer ones.
    /// </summary>
    public void Apply(ChunkBasedSegment<PersistentStore> segment, ArchetypeClusterInfo layout, ReadOnlySpan<int> sortedClusters, ChangeSet changeSet,
        Action batchPoint, byte* readBuffer, long readBufferBytes, int maxRunBufferBytes, byte* writeBuffer, int writeBufferBytes, byte* staging,
        int stagingBytes, RebuildProgress progress = null)
    {
        var columns = _sections.Length;
        var share = readBufferBytes / columns / RebuildTempFile.BlockSize * RebuildTempFile.BlockSize;
        var mergers = new RunMerger<int>[columns];
        var heads = new StagedEntry<int>[columns];
        var hasHead = new bool[columns];
        var file = SpilledRuns > 0 ? _file() : null;
        for (var c = 0; c < columns; c++)
        {
            var runs = _runs[c];
            if (runs.Count == 0)
            {
                continue;
            }

            var fileRuns = 0;
            foreach (var r in runs)
            {
                fileRuns += r.InMemory ? 0 : 1;
            }

            if (fileRuns > share / RebuildTempFile.BlockSize)
            {
                // The whole read buffer is free while one field merges down: no final merge has started reading yet.
                RunMerger<int>.MergeDown(file, runs, (int)Math.Max(2, readBufferBytes / RebuildTempFile.BlockSize), readBuffer, readBufferBytes,
                    writeBuffer, writeBufferBytes, new Span<byte>(staging, stagingBytes), ref _nextRunId);
            }
        }

        for (var c = 0; c < columns; c++)
        {
            if (_runs[c].Count > 0)
            {
                mergers[c] = new RunMerger<int>(file, _runs[c].ToArray(), readBuffer + c * share, share, maxRunBufferBytes);
                hasHead[c] = mergers[c].Next(out heads[c]);
            }
        }

        var accessor = segment.CreateChunkAccessor(changeSet);
        var since = 0;
        try
        {
            foreach (var chunkId in sortedClusters)
            {
                var clusterBase = accessor.GetChunkAddress(chunkId, dirty: true);
                var occupancy = *(ulong*)clusterBase;
                since += BitOperations.PopCount(occupancy);
                if (progress != null)
                {
                    Interlocked.Increment(ref progress.Done);
                }
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    var location = chunkId * 64 + slot;
                    for (var c = 0; c < columns; c++)
                    {
                        var elementId = 0;
                        if (hasHead[c] && heads[c].Key <= location)
                        {
                            if (heads[c].Key < location)
                            {
                                ThrowHelper.ThrowInvalidOp($"Index rebuild: an element id for location {heads[c].Key}, which holds no entity.");
                            }

                            elementId = heads[c].Location;
                            hasHead[c] = mergers[c].Next(out heads[c]);
                        }

                        *(int*)(clusterBase + layout.IndexElementIdOffset(_sections[c], slot)) = elementId;
                        TailsWritten++;
                    }
                }

                if (since >= DatabaseEngine.RecoveryBatchSize && batchPoint != null)
                {
                    accessor.CommitChanges();
                    batchPoint();
                    since = 0;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        for (var c = 0; c < columns; c++)
        {
            if (hasHead[c])
            {
                ThrowHelper.ThrowInvalidOp($"Index rebuild: element ids left for locations past the last cluster (from {heads[c].Key}).");
            }
        }
    }

    public void Dispose()
    {
        for (var c = 0; c < _kept.Length; c++)
        {
            _memory.Free(ref _kept[c]);
        }
    }
}

/// <summary>
/// One build worker's element-id pairs (#1588, design §D7): a fixed buffer its multi-value builds fill in key order, sorted by location and spilled as a
/// run whenever it fills, and kept or spilled at the end of each tree (<see cref="ElementIdWriteBack.End"/>).
/// </summary>
internal sealed unsafe class PairCollector : IElementIdSink, IDisposable
{
    private readonly ElementIdWriteBack _owner;
    private readonly byte* _staging;
    private readonly int _stagingBytes;
    private readonly int[] _counts = new int[RadixSort.Buckets];
    private NativeBuffer _buffer;
    private NativeBuffer _scratch;
    private int _count;
    private int _column = -1;

    internal PairCollector(ElementIdWriteBack owner, byte* staging, int stagingBytes)
    {
        _owner = owner;
        _staging = staging;
        _stagingBytes = stagingBytes;
    }

    /// <summary>Starts collecting <paramref name="column"/>'s pairs: its tree's build is about to run on this worker.</summary>
    public void Begin(int column)
    {
        _column = column;
        _count = 0;
        var bytes = (long)_owner.Capacity * ElementIdWriteBack.PairStride;
        _buffer ??= _owner.Memory.Allocate(bytes, "IndexRebuild.ElementIdPairs");
        _scratch ??= _owner.Memory.Allocate(bytes, "IndexRebuild.ElementIdScratch");
    }

    public void Add(int column, int location, int elementId)
    {
        var pairs = (StagedEntry<int>*)_buffer.Pointer;
        pairs[_count] = new StagedEntry<int>(location, elementId);
        if (++_count == _owner.Capacity)
        {
            Sort();
            _owner.Spill(_column, _buffer.Pointer, _count, _staging, _stagingBytes);
            _count = 0;
        }
    }

    /// <summary>Ends the column's pairs: kept or spilled by <see cref="ElementIdWriteBack.End"/>.</summary>
    public void End()
    {
        Sort();
        _owner.End(_column, _buffer.Pointer, _count, _staging, _stagingBytes);
        _count = 0;
        _column = -1;
    }

    public void Dispose()
    {
        _owner.Memory.Free(ref _buffer);
        _owner.Memory.Free(ref _scratch);
    }

    private void Sort() => StagedOrder<int>.Sort(new Span<StagedEntry<int>>(_buffer.Pointer, _count), new Span<StagedEntry<int>>(_scratch.Pointer, _count),
        _counts);
}
