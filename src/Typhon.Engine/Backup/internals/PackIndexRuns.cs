using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
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
/// <b>Memory</b> is native, never the GC heap (a point used to leave ~15 MiB of large-object garbage: the heap's array grown to 8 MiB, a 6 MiB
/// copy of its rest, a 1 MiB output array, the merge's batch and read buffers): the heap (32 B per entry, grown as entries arrive), a 1 MiB
/// output buffer, and at the merge a 64 KiB read buffer per run and one batch. A pack of fewer entries than the heap holds never touches the
/// disk; its final sort runs in the heap's own memory. Runs are written to sidecars <c>{base}{n}</c>; the pack writer passes <c>{temp}.idx</c>,
/// so they are swept with abandoned temps. The merge reads at most <see cref="MaxMergeFanIn"/> runs at once: more are merged in passes, each
/// writing a longer run, so its handles and buffers stay bounded however the entries arrived.
/// </para>
/// </remarks>
internal sealed class PackIndexRuns : IDisposable
{
    public const int DefaultHeapEntries = 256 << 10;

    /// <summary>Runs read at once by a merge pass.</summary>
    internal const int MaxMergeFanIn = 64;

    private const int StreamEntries = (1 << 20) / PackFormat.PageEntrySize;
    private const int ReadBufferEntries = (64 << 10) / PackFormat.PageEntrySize;
    private const int BatchEntries = 4096;
    private const int InitialHeapEntries = 4096;

    private readonly IStoreFileSystem _fs;
    private readonly string _sidecarBase;
    private readonly int _heapEntries;
    private readonly BufferOwner _owner;
    private readonly string _bufferId;
    private readonly List<(string Path, long Count)> _runs = [];

    private NativeBuffer _heap;
    private int _heapCount;
    private int _bufferSerial;
    private int _sidecarSerial;
    private uint _currentRun;
    private long _lastOutput = -1;
    private IStoreFile _runFile;
    private long _runCount;
    private NativeBuffer _out;
    private int _outFill;

    /// <summary>A heap node: the entry and its key, <c>(run &lt;&lt; 32) | PageId</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Node
    {
        public ulong Key;
        public PackPageEntry Entry;
    }

    /// <param name="fs">Where sidecars go.</param>
    /// <param name="sidecarBase">Sidecar path prefix.</param>
    /// <param name="heapEntries">Entries sorted in memory before runs spill.</param>
    /// <param name="owner">The engine whose resource tree the buffers belong to; <c>null</c> for an engine-free tool.</param>
    /// <param name="bufferId">The prefix of the buffers' resource ids, unique under the owner's parent.</param>
    public PackIndexRuns(IStoreFileSystem fs, string sidecarBase, int heapEntries = DefaultHeapEntries, BufferOwner owner = null,
        string bufferId = "Backup.PackIndex")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heapEntries);
        _fs = fs;
        _sidecarBase = sidecarBase;
        _heapEntries = heapEntries;
        _owner = owner;
        _bufferId = bufferId;
    }

    /// <summary>Entries added.</summary>
    public long Count { get; private set; }

    /// <summary>Sorted runs the heap spilled to sidecars (a merge pass's output is not one).</summary>
    public int SpilledRuns { get; private set; }

    /// <summary>The most sidecars one merge pass read at once.</summary>
    public int WidestMerge { get; private set; }

    private Span<Node> Heap => MemoryMarshal.Cast<byte, Node>(_heap.Span);

    public void Add(in PackPageEntry entry)
    {
        if (_heapCount == _heapEntries)
        {
            EmitSmallest();
        }

        // An entry at or below the last one emitted cannot extend the current run: it belongs to the next. A duplicate lands there too and
        // is caught by the merge.
        var run = entry.PageId > _lastOutput ? _currentRun : _currentRun + 1;
        Push(new Node { Key = ((ulong)run << 32) | entry.PageId, Entry = entry });
        Count++;
    }

    /// <summary>
    /// Emits every entry in ascending <c>PageId</c>. Throws <see cref="InvalidOperationException"/> on a duplicate <c>PageId</c>, which only a
    /// caller bug can produce.
    /// </summary>
    public void Merge(PackEntryBatchSink sink)
    {
        CloseRun();

        // The heap holds the current run's tail and possibly the next run's head: sorted by page in its own memory, still one sorted source.
        if (_heapCount > 0)
        {
            Heap[.._heapCount].Sort(static (a, b) => a.Entry.PageId.CompareTo(b.Entry.PageId));
        }

        // Narrow the merge to a bounded fan-in: passes of MaxMergeFanIn runs into one longer run each, until the rest fits in one final pass.
        while (_runs.Count + (_heapCount > 0 ? 1 : 0) > MaxMergeFanIn)
        {
            var group = _runs.GetRange(0, MaxMergeFanIn);
            _runs.RemoveRange(0, MaxMergeFanIn);
            _runs.Add(MergeToSidecar(group));
        }

        var sources = new List<IRunReader>(_runs.Count + 1);
        try
        {
            foreach (var (path, count) in _runs)
            {
                sources.Add(new FileRun(_fs, path, count, AllocateBuffer(ReadBufferEntries * PackFormat.PageEntrySize, "Read")));
            }

            if (_heapCount > 0)
            {
                sources.Add(new HeapRun(this));
            }

            WidestMerge = Math.Max(WidestMerge, _runs.Count);
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

    private (string Path, long Count) MergeToSidecar(List<(string Path, long Count)> group)
    {
        var path = NextSidecar();
        long written = 0;
        var sources = new List<IRunReader>(group.Count);
        try
        {
            using (var file = _fs.OpenCreateNew(path))
            {
                foreach (var (p, c) in group)
                {
                    sources.Add(new FileRun(_fs, p, c, AllocateBuffer(ReadBufferEntries * PackFormat.PageEntrySize, "Read")));
                }

                WidestMerge = Math.Max(WidestMerge, sources.Count);
                MergeRuns(sources, batch =>
                {
                    file.Write(MemoryMarshal.AsBytes(batch), written * PackFormat.PageEntrySize);
                    written += batch.Length;
                });
            }
        }
        finally
        {
            foreach (var s in sources)
            {
                s.Dispose();
            }
        }

        foreach (var (p, _) in group)
        {
            DeleteSidecar(p);
        }

        return (path, written);
    }

    private void Push(in Node node)
    {
        if (_heap == null || _heapCount == _heap.Length / Unsafe.SizeOf<Node>())
        {
            GrowHeap();
        }

        var heap = Heap;
        var i = _heapCount++;
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            if (heap[parent].Key <= node.Key)
            {
                break;
            }

            heap[i] = heap[parent];
            i = parent;
        }

        heap[i] = node;
    }

    private Node PopSmallest()
    {
        var heap = Heap;
        var top = heap[0];
        var last = heap[--_heapCount];
        var i = 0;
        while (true)
        {
            var child = (2 * i) + 1;
            if (child >= _heapCount)
            {
                break;
            }

            if (child + 1 < _heapCount && heap[child + 1].Key < heap[child].Key)
            {
                child++;
            }

            if (last.Key <= heap[child].Key)
            {
                break;
            }

            heap[i] = heap[child];
            i = child;
        }

        if (_heapCount > 0)
        {
            heap[i] = last;
        }

        return top;
    }

    private void GrowHeap()
    {
        var nodeSize = Unsafe.SizeOf<Node>();
        var capacity = _heap == null ? Math.Min(_heapEntries, InitialHeapEntries) : Math.Min(_heapEntries, (_heap.Length / nodeSize) * 2);
        var grown = AllocateBuffer(capacity * nodeSize, "Heap");
        if (_heap != null)
        {
            _heap.Span[..(_heapCount * nodeSize)].CopyTo(grown.Span);
            ((IDisposable)_heap).Dispose();
        }

        _heap = grown;
    }

    private void EmitSmallest()
    {
        var node = PopSmallest();
        var run = (uint)(node.Key >> 32);
        if (run != _currentRun || _runFile == null)
        {
            CloseRun();
            _currentRun = run;
            var path = NextSidecar();
            _runFile = _fs.OpenCreateNew(path);
            _runs.Add((path, 0));
            SpilledRuns++;
            _out ??= AllocateBuffer(StreamEntries * PackFormat.PageEntrySize, "Out");
        }

        MemoryMarshal.Cast<byte, PackPageEntry>(_out.Span)[_outFill++] = node.Entry;
        _lastOutput = node.Entry.PageId;
        if (_outFill == StreamEntries)
        {
            FlushOut();
        }
    }

    private string NextSidecar()
    {
        var path = _sidecarBase + (++_sidecarSerial).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _fs.DeleteFile(path);
        return path;
    }

    /// <summary>A native buffer, the engine's under its owner when there is one; every id unique under the parent (a serial per buffer).</summary>
    private NativeBuffer AllocateBuffer(int bytes, string role) =>
        NativeBuffer.Allocate(bytes, _owner, $"{_bufferId}.{role}{++_bufferSerial}", alignment: 64);

    private void FlushOut()
    {
        if (_outFill == 0)
        {
            return;
        }

        _runFile.Write(_out.Span[..(_outFill * PackFormat.PageEntrySize)], _runCount * PackFormat.PageEntrySize);
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

    private void MergeRuns(List<IRunReader> sources, PackEntryBatchSink sink)
    {
        var heap = new PriorityQueue<int, uint>(sources.Count);
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i].MoveNext())
            {
                heap.Enqueue(i, sources[i].Current.PageId);
            }
        }

        var batchBuffer = AllocateBuffer(BatchEntries * PackFormat.PageEntrySize, "Batch");
        try
        {
            var batch = MemoryMarshal.Cast<byte, PackPageEntry>(batchBuffer.Span);
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
                    batch = MemoryMarshal.Cast<byte, PackPageEntry>(batchBuffer.Span);
                    n = 0;
                }

                if (sources[i].MoveNext())
                {
                    heap.Enqueue(i, sources[i].Current.PageId);
                }
            }

            if (n > 0)
            {
                sink(batch[..n]);
            }
        }
        finally
        {
            ((IDisposable)batchBuffer).Dispose();
        }
    }

    private void DeleteSidecar(string path)
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

    public void Dispose()
    {
        _runFile?.Dispose();
        _runFile = null;
        foreach (var (path, _) in _runs)
        {
            DeleteSidecar(path);
        }

        _runs.Clear();
        ((IDisposable)_heap)?.Dispose();
        _heap = null;
        _heapCount = 0;
        ((IDisposable)_out)?.Dispose();
        _out = null;
    }

    private interface IRunReader : IDisposable
    {
        PackPageEntry Current { get; }

        bool MoveNext();
    }

    /// <summary>The heap's sorted rest, read in place.</summary>
    private sealed class HeapRun(PackIndexRuns owner) : IRunReader
    {
        private int _next;

        public PackPageEntry Current { get; private set; }

        public bool MoveNext()
        {
            if (_next >= owner._heapCount)
            {
                return false;
            }

            Current = owner.Heap[_next++].Entry;
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
        private readonly NativeBuffer _buffer;
        private long _read;
        private int _pos;
        private int _filled;

        public FileRun(IStoreFileSystem fs, string path, long count, NativeBuffer buffer)
        {
            _buffer = buffer;
            try
            {
                _file = fs.OpenRead(path);
            }
            catch
            {
                ((IDisposable)buffer).Dispose();
                throw;
            }

            _count = count;
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

                var entries = MemoryMarshal.Cast<byte, PackPageEntry>(_buffer.Span);
                var n = (int)Math.Min(entries.Length, _count - _read);
                if (!_file.ReadExactly(_buffer.Span[..(n * PackFormat.PageEntrySize)], _read * PackFormat.PageEntrySize))
                {
                    throw new IOException($"Index sidecar '{_file.Path}' is shorter than the {_count} entries written to it.");
                }

                _read += n;
                _filled = n;
                _pos = 0;
            }

            Current = MemoryMarshal.Cast<byte, PackPageEntry>(_buffer.Span)[_pos++];
            return true;
        }

        public void Dispose()
        {
            _file.Dispose();
            ((IDisposable)_buffer).Dispose();
        }
    }
}
