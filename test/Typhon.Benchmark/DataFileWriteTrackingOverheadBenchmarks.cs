using BenchmarkDotNet.Attributes;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Benchmark;

/// <summary>
/// What online backup's tracking adds to one data-file page write, with no I/O (#1412, plan §9). <see cref="DataFileWritePathBenchmarks"/> cannot
/// resolve 1 %: the real <c>pwrite</c> swings far more than that between runs. This isolates the added work: the striped write gate's shared enter and
/// exit, the change map's test-and-set, and the copy-before-write hook's <c>Volatile.Read</c> and null check, around an empty, non-inlined stand-in
/// for the write.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Tracked"/> mirrors <c>PagedMMF.IssueDataFileWrite</c> + <c>BeforeDataFileWrite</c> statement for statement (with the test probe's null
/// check); <see cref="Untracked"/> is the stand-in alone. Their difference is the added cost. A change to either production method must be mirrored here.
/// </para>
/// <para>
/// Times are per page per writer: each of <see cref="Writers"/> threads writes <see cref="PagesPerWriter"/> pages per invocation, and the wall time is
/// divided by that count, so it is what one checkpoint wave writer pays per page. <see cref="PageLayout.Distinct"/> gives each writer its own words;
/// <see cref="PageLayout.SameWord"/> interleaves the writers' pages so all of them set bits in the same words (the contended case).
/// <see cref="MapState.FirstWrite"/> unmarks the pages before every pass (the <c>Interlocked.Or</c> path, a page's first write since the freeze);
/// <see cref="MapState.AlreadySet"/> leaves them marked (the steady state: a load and a branch).
/// </para>
/// <para>
/// Same binary, A/B by environment: with <c>TYPHON_DATAFILE_TRACKING=0</c> and <c>TYPHON_DATAFILE_GATE=0</c> set, <see cref="Tracked"/> runs the
/// switched-off body (two folded branches). Gate: <c>Tracked − Untracked ≤ 37 ns</c> (1 % of the 3.7 µs measured per checkpoint page).
/// </para>
/// </remarks>
[BenchmarkCategory("Storage", "Backup")]
public class DataFileWriteTrackingOverheadBenchmarks
{
    /// <summary>Pages each writer writes per invocation.</summary>
    public const int PagesPerWriter = 4096;

    public enum PageLayout
    {
        Distinct,
        SameWord,
    }

    public enum MapState
    {
        FirstWrite,
        AlreadySet,
    }

    private DataFileWriteGate _gate;
    private PageChangeMap _changeMap;
    private IDataFileWriteHook _writeHook;
    private Action<int, int> _probe;
    private int[][] _pages;
    private int _firstPage;
    private int _pageSpan;

    [Params(1, 4)]
    public int Writers { get; set; }

    [Params(PageLayout.Distinct, PageLayout.SameWord)]
    public PageLayout Layout { get; set; }

    [Params(MapState.FirstWrite, MapState.AlreadySet)]
    public MapState State { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _gate = new DataFileWriteGate();
        _changeMap = new PageChangeMap();
        _writeHook = null;
        _probe = null;
        _firstPage = 1 << 20;
        _pageSpan = Writers * PagesPerWriter;
        _pages = new int[Writers][];
        for (var w = 0; w < Writers; w++)
        {
            _pages[w] = new int[PagesPerWriter];
            for (var i = 0; i < PagesPerWriter; i++)
            {
                _pages[w][i] = Layout == PageLayout.Distinct
                    ? _firstPage + w * PagesPerWriter + i      // each writer owns whole words
                    : _firstPage + i * Writers + w;             // the writers' pages interleave inside every word
            }
        }

        _changeMap.Set(_firstPage, _pageSpan);   // allocates the chunk; AlreadySet keeps the marks
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = PagesPerWriter)]
    public void Untracked() => Run(tracked: false);

    [Benchmark(OperationsPerInvoke = PagesPerWriter)]
    public void Tracked() => Run(tracked: true);

    private void Run(bool tracked)
    {
        // FirstWrite: unmark before every pass, inside the measured call (an IterationSetup would force one invocation per iteration). It costs a
        // store per 64 pages, charged to Tracked: a slight overestimate of the added cost.
        if (State == MapState.FirstWrite && tracked)
        {
            _changeMap.ClearWordsForTests(_firstPage, _pageSpan);
        }

        if (Writers == 1)
        {
            Pass(_pages[0], tracked);
            return;
        }

        Parallel.For(0, Writers, new ParallelOptions { MaxDegreeOfParallelism = Writers }, w => Pass(_pages[w], tracked));
    }

    private void Pass(int[] pages, bool tracked)
    {
        if (tracked)
        {
            for (var i = 0; i < pages.Length; i++)
            {
                TrackedWrite(pages[i]);
            }
        }
        else
        {
            for (var i = 0; i < pages.Length; i++)
            {
                Write(pages[i]);
            }
        }
    }

    // Mirrors PagedMMF.IssueDataFileWrite (around the write) and BeforeDataFileWrite.
    private void TrackedWrite(int filePageIndex)
    {
        var gated = DataFileWriteTracking.GateEnabled;
        var token = gated ? _gate.EnterShared() : default;
        try
        {
            if (DataFileWriteTracking.MapEnabled)
            {
                Volatile.Read(ref _changeMap).Set(filePageIndex, 1);
            }

            Volatile.Read(ref _writeHook)?.BeforeWrite(filePageIndex, 1);
            _probe?.Invoke(filePageIndex, 1);
            Write(filePageIndex);
        }
        finally
        {
            if (gated)
            {
                _gate.ExitShared(token);
            }
        }
    }

    /// <summary>Stands in for <c>RandomAccess.Write</c>: a call the JIT cannot remove.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write(int filePageIndex)
    {
    }
}
