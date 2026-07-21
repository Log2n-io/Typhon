using System;
using BenchmarkDotNet.Attributes;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// BTree: Enumeration Under Contention — Measures how EnumerateLeaves()
// interacts with concurrent writers.
//
// Current model (pre-OLC): AccessControl uses writer-preference — shared
// access blocks while ANY exclusive waiter is queued. This makes
// enumeration under continuous write pressure impossible. The benchmark
// uses a "stop-the-world" model reflecting the pre-OLC reality: writers
// must fully drain before enumeration can proceed. The measured time
// includes writer drain + enumeration.
//
// After OLC: each leaf is validated independently via version counter.
// Writers are NEVER blocked by enumerators. The coordination overhead
// disappears, and the benchmark can run with truly concurrent writers.
//
// Protocol per BDN iteration:
//   IterationSetup  → resume writers (gate opens), writers run for 10ms
//   Benchmark       → pause writers (gate closes, drain), enumerate
//   Writers remain paused until next IterationSetup
//
// Profile mapping:
//   Fast:   not included (enumeration is a specialized scenario)
//   Medium: WriterCount = [0, 8]
//   Full:   WriterCount = [0, 4, 16, 32]
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[BenchmarkCategory("BTree", "Concurrency", "BTreeFull")]
public class BTreeEnumerationBenchmarks
{
    private BTreeBenchmarkHelper _helper;
    private EpochManager _epochManager;
    private ChunkBasedSegment<PersistentStore> _segment;
    private LongSingleBTree<PersistentStore> _tree;

    // Writer coordination: writers loop continuously but check a pause flag.
    // When paused, they signal a CountdownEvent and block on a gate.
    // The gate is controlled by IterationSetup (open) and the benchmark (close).
    private volatile bool _stopWriters;
    private volatile bool _pauseRequested;
    private Task[] _writerTasks;
    private CountdownEvent _writersPaused;
    private ManualResetEventSlim _resumeGate;

    private const int PreFillCount = 10_000;

    [Params(0, 4, 16, 32)]
    public int WriterCount;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _helper = new BTreeBenchmarkHelper();
        _helper.Setup(1000);
        _epochManager = _helper.EpochManager;

        _segment = _helper.AllocateSegment<Index64Chunk>(1000);
        _tree = new LongSingleBTree<PersistentStore>(_segment);
        BTreeBenchmarkHelper.PreFillLong(_tree, _segment, PreFillCount);

        if (WriterCount > 0)
        {
            _writersPaused = new CountdownEvent(WriterCount);
            _resumeGate = new ManualResetEventSlim(false); // starts CLOSED — writers start paused
            _stopWriters = false;
            _pauseRequested = true; // writers start in paused state

            _writerTasks = new Task[WriterCount];
            for (int w = 0; w < WriterCount; w++)
            {
                var writerId = w;
                _writerTasks[w] = Task.Run(() => WriterLoop(writerId));
            }

            // Wait for all writers to reach the pause point
            _writersPaused.Wait();
        }
    }

    private void WriterLoop(int writerId)
    {
        var depth = _epochManager.EnterScope();
        try
        {
            var accessor = _segment.CreateChunkAccessor();
            var rangeStart = 1 + writerId * (PreFillCount / WriterCount);
            var rangeEnd = (writerId == WriterCount - 1) ? PreFillCount : rangeStart + (PreFillCount / WriterCount) - 1;
            var rangeSize = rangeEnd - rangeStart + 1;
            var rng = new Random(700 + writerId);

            while (!_stopWriters)
            {
                // Pause checkpoint: signal that we're paused, then wait for resume
                if (_pauseRequested)
                {
                    _writersPaused.Signal();
                    _resumeGate.Wait();
                    if (_stopWriters)
                    {
                        break;
                    }
                    continue;
                }

                var key = (long)(rangeStart + rng.Next(0, rangeSize));
                if (_tree.Remove(key, out var val, ref accessor))
                {
                    _tree.Add(key, val, ref accessor);
                }
            }
            accessor.Dispose();
        }
        finally
        {
            _epochManager.ExitScope(depth);
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        if (WriterCount == 0)
        {
            return;
        }

        // Resume writers: reset countdown, open gate, clear pause flag
        _writersPaused.Reset(WriterCount);
        _pauseRequested = false;
        _resumeGate.Set();

        // Let writers run for a bit to establish write pressure
        Thread.Sleep(10);

        // Close the gate for the next pause cycle (writers won't see it
        // until _pauseRequested is set in the benchmark method)
        _resumeGate.Reset();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_writerTasks != null)
        {
            _stopWriters = true;
            _pauseRequested = false;
            _resumeGate?.Set(); // unblock any paused writers so they see _stopWriters
            Task.WaitAll(_writerTasks);
        }
        _writersPaused?.Dispose();
        _resumeGate?.Dispose();
        _helper?.Dispose();
    }

    /// <summary>
    /// Full leaf enumeration while N writer threads are active.
    ///
    /// Pre-OLC: Writers are drained via a pause flag before enumeration.
    /// The measured time = drain latency + enumeration. This models the
    /// real pre-OLC cost: enumeration requires writer quiescence because
    /// the tree-wide shared lock cannot be acquired while any exclusive
    /// waiter is queued (writer-preference in AccessControl).
    ///
    /// WriterCount=0:  baseline enumeration time (no drain overhead)
    /// WriterCount=32: drain 32 writers + enumerate
    ///
    /// After OLC: the pause/drain will be removed. Writers proceed
    /// concurrently with the enumerator, which validates each leaf via
    /// version counters.
    /// </summary>
    [Benchmark]
    public void Enumerate_Full()
    {
        // Signal writers to pause and wait for all to drain
        if (WriterCount > 0)
        {
            _pauseRequested = true;
            _writersPaused.Wait();
        }

        // Enumerate with no contention (writers are paused)
        var accessor = _segment.CreateChunkAccessor();
        int count = 0;
        using (var enumerator = _tree.EnumerateLeaves())
        {
            while (enumerator.MoveNext())
            {
                count++;
            }
        }
        accessor.Dispose();

        // Writers remain paused — IterationSetup will resume them
        if (count == 0)
        {
            throw new InvalidOperationException("Enumeration returned no entries");
        }
    }
}
