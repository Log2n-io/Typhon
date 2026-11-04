using System;
using System.Collections.Generic;
using System.Numerics;

namespace Typhon.Engine.Internals;

/// <summary>
/// Puts each multi-value entry's element id back in its entity's cluster tail, in location order (#1588, design §D7). The builds emit
/// <c>(location, element id)</c> in key order; the clusters need them in location order. So the pairs are handled as the keys are: collected in one
/// fixed buffer, sorted by location and spilled as verified runs, then merged back per field during one ascending walk of the clusters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded memory (RB-09).</b> Nothing here grows with the archetype: a field's pairs take one buffer and its sort scratch while its tree is built
/// (<see cref="BeginColumn"/> .. <see cref="EndColumn"/>); a field whose pairs never spilled keeps them as an in-memory run only while the fields kept so
/// far fit their share; and the apply merges each field's runs through a share of the read buffer it is given. More data is more runs, never a larger
/// buffer.
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
internal sealed unsafe class ElementIdWriteBack : IElementIdSink, IDisposable
{
    private const int PairStride = 8;

    /// <summary>Test hook: when positive, the pair buffer holds this many pairs, so a small archetype spills pair runs.</summary>
    internal static int PairBufferEntriesForTest { get; set; }

    private readonly int[] _sections;
    private readonly RebuildMemory _memory;
    private readonly Func<RebuildTempFile> _file;
    private readonly Func<IntPtr> _staging;
    private readonly int _stagingBytes;
    private readonly long _keepBytes;
    private readonly int _capacity;
    private readonly List<RunDescriptor>[] _runs;
    private readonly NativeBuffer[] _kept;
    private readonly int[] _counts = new int[RadixSort.Buckets];

    private NativeBuffer _buffer;
    private NativeBuffer _scratch;
    private int _count;
    private int _column = -1;
    private long _keptBytes;
    private int _nextRunId;

    /// <param name="sections">The tail section of each persistent multi-value field, by column.</param>
    /// <param name="memory">The rebuild's memory.</param>
    /// <param name="pairBufferBytes">The bytes the pair buffer takes; its sort scratch takes as many again.</param>
    /// <param name="keepBytes">The bytes the fields' unspilled pairs may keep in memory until the apply.</param>
    /// <param name="file">The temp file, created on first use.</param>
    /// <param name="staging">The temp file's write staging buffer, created on first use.</param>
    /// <param name="stagingBytes">Its size.</param>
    /// <param name="firstRunId">The run id of the first pair run; ids stay unique within the temp file.</param>
    public ElementIdWriteBack(int[] sections, RebuildMemory memory, long pairBufferBytes, long keepBytes, Func<RebuildTempFile> file, Func<IntPtr> staging,
        int stagingBytes, int firstRunId)
    {
        _sections = sections;
        _memory = memory;
        _file = file;
        _staging = staging;
        _stagingBytes = stagingBytes;
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
    public int SpilledRuns { get; private set; }

    /// <summary>Tails written by <see cref="Apply"/>.</summary>
    public long TailsWritten { get; private set; }

    /// <summary>Starts collecting <paramref name="column"/>'s pairs: its tree's build is about to run.</summary>
    public void BeginColumn(int column)
    {
        _column = column;
        _count = 0;
        _buffer ??= _memory.Allocate((long)_capacity * PairStride, "IndexRebuild.ElementIdPairs");
        _scratch ??= _memory.Allocate((long)_capacity * PairStride, "IndexRebuild.ElementIdScratch");
    }

    public void Add(int column, int location, int elementId)
    {
        var pairs = (StagedEntry<int>*)_buffer.Pointer;
        pairs[_count] = new StagedEntry<int>(location, elementId);
        if (++_count == _capacity)
        {
            Spill();
        }
    }

    /// <summary>Ends <paramref name="column"/>'s pairs: kept in memory when nothing spilled and they fit the keep share, spilled otherwise.</summary>
    public void EndColumn(int column)
    {
        var bytes = (long)_count * PairStride;
        if (_runs[column].Count == 0 && _keptBytes + bytes <= _keepBytes)
        {
            if (_count > 0)
            {
                Sort();
                var kept = _kept[column] = _memory.Allocate(bytes, "IndexRebuild.ElementIdKept");
                new ReadOnlySpan<byte>(_buffer.Pointer, (int)bytes).CopyTo(kept.Span);
                _runs[column].Add(new RunDescriptor(kept.Pointer, _count, _nextRunId++));
                _keptBytes += kept.Length;
            }
        }
        else if (_count > 0)
        {
            Spill();
        }

        _count = 0;
        _column = -1;
    }

    /// <summary>Frees the build-time pair buffer and scratch; the apply needs neither.</summary>
    public void EndBuilds()
    {
        _memory.Free(ref _buffer);
        _memory.Free(ref _scratch);
    }

    /// <summary>The next run id after the pair runs.</summary>
    public int NextRunId => _nextRunId;

    /// <summary>
    /// Writes every occupied slot's tail in one ascending walk of the clusters, each field's pairs merged back from its runs through an equal share of
    /// <paramref name="readBuffer"/>; a field with more runs than its share holds blocks is first merged down into longer ones.
    /// </summary>
    public void Apply(ChunkBasedSegment<PersistentStore> segment, ArchetypeClusterInfo layout, ReadOnlySpan<int> sortedClusters, ChangeSet changeSet,
        Action batchPoint, byte* readBuffer, long readBufferBytes, int maxRunBufferBytes, byte* writeBuffer, int writeBufferBytes)
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
                    writeBuffer, writeBufferBytes, new Span<byte>((void*)_staging(), _stagingBytes), ref _nextRunId);
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
        EndBuilds();
        for (var c = 0; c < _kept.Length; c++)
        {
            _memory.Free(ref _kept[c]);
        }
    }

    private void Sort() => StagedOrder<int>.Sort(new Span<StagedEntry<int>>(_buffer.Pointer, _count), new Span<StagedEntry<int>>(_scratch.Pointer, _count),
        _counts);

    private void Spill()
    {
        Sort();
        _runs[_column].Add(_file().Write(new ReadOnlySpan<byte>(_buffer.Pointer, _count * PairStride), PairStride, _count, RebuildBlockKind.Pairs,
            _nextRunId++, new Span<byte>((void*)_staging(), _stagingBytes)));
        SpilledRuns++;
        _count = 0;
    }
}
