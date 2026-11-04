using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Reads one sorted run back, in order (#1588, design §D3): from a native buffer that never spilled, or block by block from the temp file through a
/// read buffer, each block verified before an entry of it is used (RB-10).
/// </summary>
/// <remarks>
/// The entries are read in place: <see cref="Current"/> points into the read buffer or the in-memory run, both native memory the rebuild owns. A run's
/// entry count is checked at its end, so a block lost from the tail of a run cannot pass for a shorter run.
/// </remarks>
internal unsafe struct RunCursor<TKey> where TKey : unmanaged
{
    private RebuildTempFile _file;
    private RunDescriptor _run;
    private byte* _buffer;
    private int _bufferBlocks;
    private int _nextBlockToRead;
    private int _blockInBuffer;
    private int _blocksInBuffer;
    private long _consumed;
    private StagedEntry<TKey>* _current;
    private StagedEntry<TKey>* _end;

    /// <summary>A cursor over <paramref name="run"/>; <paramref name="buffer"/> holds <paramref name="bufferBytes"/> bytes, a multiple of the block size,
    /// and is unused for an in-memory run.</summary>
    public RunCursor(RebuildTempFile file, in RunDescriptor run, byte* buffer, int bufferBytes)
    {
        _file = file;
        _run = run;
        _buffer = buffer;
        _bufferBlocks = bufferBytes / RebuildTempFile.BlockSize;
        _nextBlockToRead = 0;
        _blockInBuffer = 0;
        _blocksInBuffer = 0;
        _consumed = 0;
        if (run.InMemory)
        {
            _current = (StagedEntry<TKey>*)run.Memory;
            _end = _current + run.Entries;
            return;
        }

        if (run.Blocks > 0 && _bufferBlocks == 0)
        {
            ThrowHelper.ThrowArgument("RunCursor: a file run needs a read buffer of at least one block.");
        }

        _current = null;
        _end = null;
        NextBlock();
    }

    /// <summary>True while an entry is available.</summary>
    public readonly bool HasCurrent
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current < _end;
    }

    /// <summary>The entry the cursor is on; valid while <see cref="HasCurrent"/>.</summary>
    public readonly ref StagedEntry<TKey> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref *_current;
    }

    /// <summary>Moves to the next entry, reading and verifying the next block when this one is done.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance()
    {
        _current++;
        if (_current == _end && !_run.InMemory)
        {
            NextBlock();
        }
    }

    private void NextBlock()
    {
        if (_blockInBuffer == _blocksInBuffer)
        {
            var left = _run.Blocks - _nextBlockToRead;
            if (left == 0)
            {
                if (_consumed != _run.Entries)
                {
                    throw new IndexRebuildTempCorruptException(_file.Path, _run.Offset, $"run {_run.Owner} ends after {_consumed} of {_run.Entries} entries");
                }

                _current = _end = null;
                return;
            }

            _blocksInBuffer = Math.Min(left, _bufferBlocks);
            _file.Read(_run.Offset + (long)_nextBlockToRead * RebuildTempFile.BlockSize,
                new Span<byte>(_buffer, _blocksInBuffer * RebuildTempFile.BlockSize));
            _blockInBuffer = 0;
        }

        // _nextBlockToRead moves past the buffer only once all of it is consumed, so it is still the sequence of the buffer's first block.
        var sequence = _nextBlockToRead + _blockInBuffer;
        var block = _buffer + (long)_blockInBuffer * RebuildTempFile.BlockSize;
        var payload = _file.Verify(new ReadOnlySpan<byte>(block, RebuildTempFile.BlockSize), _run.Offset + (long)sequence * RebuildTempFile.BlockSize,
            _run.Kind, _run.Owner, sequence, StagedOrder<TKey>.Stride);
        var count = payload.Length / StagedOrder<TKey>.Stride;
        _current = (StagedEntry<TKey>*)(block + RebuildTempFile.HeaderSize);
        _end = _current + count;
        _consumed += count;
        _blockInBuffer++;
        if (_blockInBuffer == _blocksInBuffer)
        {
            _nextBlockToRead += _blocksInBuffer;
        }
    }
}

/// <summary>
/// The k-way merge of sorted runs into one (key, location)-ordered stream (#1588, design §D3): a loser tree over <see cref="RunCursor{TKey}"/>s,
/// <c>log₂ k</c> comparisons per entry. Equal keys from different runs come out by location, the order's own tie-break.
/// </summary>
/// <remarks>
/// <para>
/// Heap layout: positions <c>1 .. k-1</c> hold the loser of each internal match, positions <c>k .. 2k-1</c> are the leaves (cursor <c>p - k</c>), and
/// <c>_tree[0]</c> the overall winner. Valid for any <c>k</c>, not only powers of two. An exhausted cursor loses every match.
/// </para>
/// <para>
/// <b>Fan-in is bounded by memory, not by the merge:</b> each file run needs one read buffer of at least a block. <see cref="MergeDown"/> merges groups
/// of runs into longer ones until the rest fit, so a tree of any size is merged in a bounded buffer, at one extra read and write of its runs per pass.
/// </para>
/// </remarks>
internal sealed unsafe class RunMerger<TKey> where TKey : unmanaged
{
    private readonly RunCursor<TKey>[] _cursors;
    private readonly int[] _tree;
    private readonly int _k;

    /// <summary>A merge of <paramref name="runs"/>, the file runs sharing <paramref name="readBuffer"/> equally.</summary>
    /// <param name="file">The temp file the file runs are in; may be null when every run is in memory.</param>
    /// <param name="runs">The runs, at least one.</param>
    /// <param name="readBuffer">The read buffer the file runs share.</param>
    /// <param name="readBufferBytes">Its size: at least one block per file run.</param>
    /// <param name="maxRunBufferBytes">The most one run's buffer takes (<c>MergeReadBufferBytes</c>).</param>
    public RunMerger(RebuildTempFile file, ReadOnlySpan<RunDescriptor> runs, byte* readBuffer, long readBufferBytes, int maxRunBufferBytes)
    {
        if (runs.Length == 0)
        {
            ThrowHelper.ThrowArgument("RunMerger: no run to merge.");
        }

        var fileRuns = 0;
        foreach (var run in runs)
        {
            fileRuns += run.InMemory ? 0 : 1;
        }

        var perRun = fileRuns == 0 ? 0 : RunBufferBytes(readBufferBytes, fileRuns, maxRunBufferBytes);
        if (fileRuns > 0 && perRun < RebuildTempFile.BlockSize)
        {
            ThrowHelper.ThrowArgument($"RunMerger: {readBufferBytes} read bytes cannot give {fileRuns} file runs a block each; merge them down first.");
        }

        _k = runs.Length;
        _cursors = new RunCursor<TKey>[_k];
        var next = readBuffer;
        for (var i = 0; i < _k; i++)
        {
            if (runs[i].InMemory)
            {
                _cursors[i] = new RunCursor<TKey>(file, in runs[i], null, 0);
            }
            else
            {
                _cursors[i] = new RunCursor<TKey>(file, in runs[i], next, perRun);
                next += perRun;
            }
        }

        _tree = new int[_k];
        Build();
    }

    /// <summary>The bytes each of <paramref name="fileRuns"/> runs reads per refill out of <paramref name="readBufferBytes"/>: whole blocks, capped.</summary>
    public static int RunBufferBytes(long readBufferBytes, int fileRuns, int maxRunBufferBytes)
    {
        var perRun = Math.Min(readBufferBytes / Math.Max(1, fileRuns), maxRunBufferBytes);
        return (int)(perRun / RebuildTempFile.BlockSize * RebuildTempFile.BlockSize);
    }

    /// <summary>Takes the next entry of the merged stream; false once every run is done.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Next(out StagedEntry<TKey> entry)
    {
        var w = _tree[0];
        ref var cursor = ref _cursors[w];
        if (!cursor.HasCurrent)
        {
            entry = default;
            return false;
        }

        entry = cursor.Current;
        cursor.Advance();
        Replay(w);
        return true;
    }

    /// <summary>
    /// Merges groups of <paramref name="runs"/> into longer runs, in place, until at most <paramref name="maxFanIn"/> are left, each pass reading and
    /// writing the runs once.
    /// </summary>
    /// <param name="file">The temp file; the new runs are appended to it.</param>
    /// <param name="runs">The runs, replaced by the merged ones.</param>
    /// <param name="maxFanIn">The most runs one merge may read at once: <c>read buffer / block size</c>.</param>
    /// <param name="readBuffer">The read buffer.</param>
    /// <param name="readBufferBytes">Its size.</param>
    /// <param name="writeBuffer">The output buffer: whole blocks of entries are gathered here before each write.</param>
    /// <param name="writeBufferBytes">Its size, at least one block.</param>
    /// <param name="staging">The temp file's write staging buffer.</param>
    /// <param name="nextRunId">The id the next merged run takes; advanced.</param>
    /// <returns>The extra passes made.</returns>
    public static int MergeDown(RebuildTempFile file, List<RunDescriptor> runs, int maxFanIn, byte* readBuffer, long readBufferBytes, byte* writeBuffer,
        int writeBufferBytes, Span<byte> staging, ref int nextRunId)
    {
        if (maxFanIn < 2)
        {
            ThrowHelper.ThrowArgument($"RunMerger: a fan-in of {maxFanIn} cannot merge.");
        }

        var stride = StagedOrder<TKey>.Stride;
        var capacity = RebuildTempFile.Capacity(stride);
        var outBlocks = writeBufferBytes / RebuildTempFile.BlockSize;
        var outCapacity = outBlocks * capacity;
        if (outBlocks == 0)
        {
            ThrowHelper.ThrowArgument("RunMerger: the write buffer needs at least one block.");
        }

        var passes = 0;
        while (runs.Count > maxFanIn)
        {
            passes++;
            var merged = new List<RunDescriptor>((runs.Count + maxFanIn - 1) / maxFanIn);
            for (var g = 0; g < runs.Count; g += maxFanIn)
            {
                var group = runs.GetRange(g, Math.Min(maxFanIn, runs.Count - g));
                if (group.Count == 1)
                {
                    merged.Add(group[0]);
                    continue;
                }

                var total = 0L;
                foreach (var r in group)
                {
                    total += r.Entries;
                }

                var owner = nextRunId++;
                var kind = group[0].Kind;
                var blocks = RebuildTempFile.BlocksFor(total, stride);
                var offset = file.Reserve(blocks);
                var merger = new RunMerger<TKey>(file, group.ToArray(), readBuffer, readBufferBytes, int.MaxValue);
                var output = (StagedEntry<TKey>*)writeBuffer;
                var pending = 0;
                var sequence = 0;
                while (merger.Next(out var e))
                {
                    output[pending++] = e;
                    if (pending == outCapacity)
                    {
                        file.WriteBlocks(offset, sequence, new ReadOnlySpan<byte>(writeBuffer, pending * stride), stride, pending, kind, owner, staging);
                        sequence += outBlocks;
                        pending = 0;
                    }
                }

                if (pending > 0)
                {
                    file.WriteBlocks(offset, sequence, new ReadOnlySpan<byte>(writeBuffer, pending * stride), stride, pending, kind, owner, staging);
                }

                merged.Add(new RunDescriptor(offset, blocks, owner, total, kind));
            }

            runs.Clear();
            runs.AddRange(merged);
        }

        return passes;
    }

    private void Build()
    {
        if (_k == 1)
        {
            _tree[0] = 0;
            return;
        }

        // Winners of each internal position, bottom-up; positions ≥ k are leaves.
        Span<int> winners = _k <= 256 ? stackalloc int[_k] : new int[_k];
        for (var t = _k - 1; t >= 1; t--)
        {
            var a = WinnerAt(2 * t, winners);
            var b = WinnerAt(2 * t + 1, winners);
            if (Less(a, b))
            {
                winners[t] = a;
                _tree[t] = b;
            }
            else
            {
                winners[t] = b;
                _tree[t] = a;
            }
        }

        _tree[0] = winners[1];
    }

    private int WinnerAt(int position, Span<int> winners) => position >= _k ? position - _k : winners[position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Replay(int leaf)
    {
        var w = leaf;
        for (var t = (leaf + _k) >> 1; t > 0; t >>= 1)
        {
            var other = _tree[t];
            if (Less(other, w))
            {
                _tree[t] = w;
                w = other;
            }
        }

        _tree[0] = w;
    }

    /// <summary>Whether cursor <paramref name="a"/>'s entry precedes <paramref name="b"/>'s; an exhausted cursor precedes nothing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Less(int a, int b)
    {
        ref var ca = ref _cursors[a];
        ref var cb = ref _cursors[b];
        if (!ca.HasCurrent)
        {
            return false;
        }

        if (!cb.HasCurrent)
        {
            return true;
        }

        var c = StagedOrder<TKey>.Compare(in ca.Current, in cb.Current);
        return c < 0 || (c == 0 && a < b);
    }
}
