using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>Receives a pack's INDEX entries in ascending <c>PageId</c> order, a batch at a time.</summary>
internal delegate void PackEntryBatchSink(ReadOnlySpan<PackPageEntry> batch);

/// <summary>
/// Sorts a pack's index entries while they arrive, in bounded memory (plan 1413 §2.3, R5): replacement selection into sorted runs, then a k-way
/// merge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why replacement selection, not "greater than the last one ⇒ main run".</b> The scanner adds pages in ascending order; copy-before-write
/// pre-images arrive <i>ahead</i> of it, i.e. with HIGHER ids than the scanner's position. A "main run = anything above its last entry" rule takes
/// the first such stray into the main run and then routes every later scanner page to the side run: the opposite of the intent. Here every entry
/// waits in a heap of <c>heapEntries</c>; the smallest is emitted only when the heap is full. A stray therefore waits until the scanner passes
/// it and comes out in order: scanner order plus forward strays is ONE run, whatever the stray count, as long as fewer than
/// <c>heapEntries</c> strays are outstanding at once. Any other order is still sorted correctly, in more runs.
/// </para>
/// <para>
/// <b>Memory</b> is the heap (32 B per entry) plus a 1 MiB output buffer; a pack of fewer entries than the heap holds never touches the disk.
/// Runs are written to sidecars <c>{base}{n}</c>; the pack writer passes <c>{temp}.idx</c>, so they are swept with abandoned temps.
/// </para>
/// </remarks>
internal sealed class PackIndexRuns : IDisposable
{
    public const int DefaultHeapEntries = 256 << 10;
    private const int StreamEntries = (1 << 20) / PackFormat.PageEntrySize;
    private const int ReadBufferEntries = (256 << 10) / PackFormat.PageEntrySize;

    private readonly IStoreFileSystem _fs;
    private readonly string _sidecarBase;
    private readonly int _heapEntries;
    private readonly PriorityQueue<PackPageEntry, ulong> _heap;
    private readonly List<(string Path, long Count)> _runs = [];

    private uint _currentRun;
    private long _lastOutput = -1;
    private IStoreFile _runFile;
    private long _runCount;
    private PackPageEntry[] _out;
    private int _outFill;

    public PackIndexRuns(IStoreFileSystem fs, string sidecarBase, int heapEntries = DefaultHeapEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heapEntries);
        _fs = fs;
        _sidecarBase = sidecarBase;
        _heapEntries = heapEntries;
        _heap = new PriorityQueue<PackPageEntry, ulong>(Math.Min(heapEntries, 4096));
    }

    /// <summary>Entries added.</summary>
    public long Count { get; private set; }

    /// <summary>Sorted runs written to sidecars so far.</summary>
    public int SpilledRuns => _runs.Count;

    public void Add(in PackPageEntry entry)
    {
        if (_heap.Count == _heapEntries)
        {
            EmitSmallest();
        }

        // An entry at or below the last one emitted cannot extend the current run: it belongs to the next. A duplicate lands there too and
        // is caught by the merge.
        var run = entry.PageId > _lastOutput ? _currentRun : _currentRun + 1;
        _heap.Enqueue(entry, ((ulong)run << 32) | entry.PageId);
        Count++;
    }

    /// <summary>
    /// Emits every entry in ascending <c>PageId</c>. Throws <see cref="InvalidOperationException"/> on a duplicate <c>PageId</c>, which only a
    /// caller bug can produce.
    /// </summary>
    public void Merge(PackEntryBatchSink sink)
    {
        CloseRun();
        var rest = new PackPageEntry[_heap.Count];
        var n = 0;
        while (_heap.TryDequeue(out var e, out _))
        {
            rest[n++] = e;
        }

        // The heap holds the current run's tail and possibly the next run's head: one sorted array of both is still one sorted source.
        rest.AsSpan().Sort(static (a, b) => a.PageId.CompareTo(b.PageId));

        var sources = new List<IRunReader>(_runs.Count + 1);
        try
        {
            foreach (var (path, count) in _runs)
            {
                sources.Add(new FileRun(_fs, path, count));
            }

            if (rest.Length > 0)
            {
                sources.Add(new ArrayRun(rest));
            }

            MergeRuns(sources, sink);
        }
        finally
        {
            foreach (var s in sources)
            {
                s.Dispose();
            }
        }
    }

    private void EmitSmallest()
    {
        _heap.TryDequeue(out var e, out var key);
        var run = (uint)(key >> 32);
        if (run != _currentRun || _runFile == null)
        {
            CloseRun();
            _currentRun = run;
            var path = _sidecarBase + (_runs.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _fs.DeleteFile(path);
            _runFile = _fs.OpenCreateNew(path);
            _runs.Add((path, 0));
            _out ??= new PackPageEntry[StreamEntries];
        }

        _out[_outFill++] = e;
        _lastOutput = e.PageId;
        if (_outFill == _out.Length)
        {
            FlushOut();
        }
    }

    private void FlushOut()
    {
        if (_outFill == 0)
        {
            return;
        }

        _runFile.Write(MemoryMarshal.AsBytes(_out.AsSpan(0, _outFill)), _runCount * PackFormat.PageEntrySize);
        _runCount += _outFill;
        _outFill = 0;
    }

    private void CloseRun()
    {
        if (_runFile == null)
        {
            return;
        }

        FlushOut();
        _runs[^1] = (_runs[^1].Path, _runCount);
        _runFile.Dispose();
        _runFile = null;
        _runCount = 0;
    }

    private static void MergeRuns(List<IRunReader> sources, PackEntryBatchSink sink)
    {
        var heap = new PriorityQueue<int, uint>(sources.Count);
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i].MoveNext())
            {
                heap.Enqueue(i, sources[i].Current.PageId);
            }
        }

        var batch = new PackPageEntry[4096];
        var n = 0;
        long last = -1;
        while (heap.TryDequeue(out var i, out _))
        {
            var e = sources[i].Current;
            if (e.PageId <= last)
            {
                throw new InvalidOperationException($"Pack index: page {e.PageId} added twice.");
            }

            last = e.PageId;
            batch[n++] = e;
            if (n == batch.Length)
            {
                sink(batch);
                n = 0;
            }

            if (sources[i].MoveNext())
            {
                heap.Enqueue(i, sources[i].Current.PageId);
            }
        }

        if (n > 0)
        {
            sink(batch.AsSpan(0, n));
        }
    }

    public void Dispose()
    {
        _runFile?.Dispose();
        _runFile = null;
        foreach (var (path, _) in _runs)
        {
            try
            {
                _fs.DeleteFile(path);
            }
            catch (IOException)
            {
                // A temp: whoever next holds the store lock deletes it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _runs.Clear();
        _heap.Clear();
        _out = null;
    }

    private interface IRunReader : IDisposable
    {
        PackPageEntry Current { get; }

        bool MoveNext();
    }

    private sealed class ArrayRun(PackPageEntry[] entries) : IRunReader
    {
        private int _next;

        public PackPageEntry Current { get; private set; }

        public bool MoveNext()
        {
            if (_next >= entries.Length)
            {
                return false;
            }

            Current = entries[_next++];
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FileRun : IRunReader
    {
        private readonly IStoreFile _file;
        private readonly long _count;
        private readonly PackPageEntry[] _buffer;
        private long _read;
        private int _pos;
        private int _filled;

        public FileRun(IStoreFileSystem fs, string path, long count)
        {
            _file = fs.OpenRead(path);
            _count = count;
            _buffer = new PackPageEntry[(int)Math.Max(1, Math.Min(ReadBufferEntries, count))];
        }

        public PackPageEntry Current { get; private set; }

        public bool MoveNext()
        {
            if (_pos == _filled)
            {
                if (_read == _count)
                {
                    return false;
                }

                var n = (int)Math.Min(_buffer.Length, _count - _read);
                if (!_file.ReadExactly(MemoryMarshal.AsBytes(_buffer.AsSpan(0, n)), _read * PackFormat.PageEntrySize))
                {
                    throw new IOException($"Index sidecar '{_file.Path}' is shorter than the {_count} entries written to it.");
                }

                _read += n;
                _filled = n;
                _pos = 0;
            }

            Current = _buffer[_pos++];
            return true;
        }

        public void Dispose() => _file.Dispose();
    }
}
