using BenchmarkDotNet.Attributes;
using System.Threading;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Runtime: EventQueue<T> multi-producer push path (#861)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Guards the push path of the per-worker-segmented <see cref="EventQueue{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The correctness argument for #861 is "no atomics, because worker slots are disjoint", and its whole justification is throughput — so a regression
/// here is a regression in the reason the design exists. Nothing in CI covered it before this fixture: the numbers quoted in the design doc came from
/// a scratch file.
/// </para>
/// <para>
/// <b>What to watch.</b> <see cref="Push_ViaWriter"/> is the sanctioned path and should stay within a few tenths of a nanosecond of
/// <see cref="Push_RawArrayBaseline"/>, which is the plain <c>_buffer[_count++]</c> the single-producer implementation used. If the gap widens, the
/// writer has stopped inlining or something has crept onto the fast path. <see cref="Push_ViaSharedAtomicTail"/> is not a candidate implementation —
/// it is the design the doc originally specified, kept as a live measurement of what segmentation buys, because a shared tail looks cheap until it is
/// measured under contention.
/// </para>
/// </remarks>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Runtime", "Regression")]
public class EventQueueBenchmarks
{
    /// <summary>Pushes per invocation — large enough that per-invocation overhead does not dominate a ~1 ns operation.</summary>
    private const int PushesPerOp = 4096;

    private EventQueue<DamageEvent> _queue;
    private TickContext _ctx;

    private DamageEvent[] _rawBuffer;
    private int _rawCount;

    private DamageEvent[] _sharedBuffer;
    private PaddedTail _sharedTail;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Sized so no invocation reaches the growth ceiling: this measures the steady-state fast path, not the allocator.
        _queue = new EventQueue<DamageEvent>("bench", capacity: 1 << 20);
        _ctx = new TickContext { WorkerId = 0 };

        _rawBuffer = new DamageEvent[1 << 20];
        _sharedBuffer = new DamageEvent[1 << 20];
    }

    /// <summary>The pre-#861 inner loop: a plain array store and a non-atomic increment. The floor this design is measured against.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = PushesPerOp)]
    public void Push_RawArrayBaseline()
    {
        var buffer = _rawBuffer;
        var mask = buffer.Length - 1;
        var n = _rawCount;
        for (var i = 0; i < PushesPerOp; i++)
        {
            buffer[n++ & mask] = new DamageEvent(i, i);
        }

        _rawCount = 0;
    }

    /// <summary>The sanctioned path: resolve the worker's segment once, then push. Should track the baseline closely.</summary>
    [Benchmark(OperationsPerInvoke = PushesPerOp)]
    public void Push_ViaWriter()
    {
        var w = _ctx.Writer(_queue);
        for (var i = 0; i < PushesPerOp; i++)
        {
            w.Push(new DamageEvent(i, i));
        }

        _queue.Reset();
    }

    /// <summary>
    /// The rejected alternative — one shared tail advanced by <c>Interlocked.Increment</c>. Single-threaded here, so this is its BEST case; it degrades
    /// sharply once several workers contend for the line.
    /// </summary>
    [Benchmark(OperationsPerInvoke = PushesPerOp)]
    public void Push_ViaSharedAtomicTail()
    {
        var buffer = _sharedBuffer;
        var mask = buffer.Length - 1;
        for (var i = 0; i < PushesPerOp; i++)
        {
            var slot = Interlocked.Increment(ref _sharedTail.Value) - 1;
            buffer[slot & mask] = new DamageEvent(i, i);
        }

        _sharedTail.Value = 0;
    }

    /// <summary>Reset runs per queue per tick on the timer thread; it early-outs on a queue nobody pushed into.</summary>
    [Benchmark]
    public void Reset_UntouchedQueue() => _queue.Reset();

    /// <summary>
    /// The reactive-skip poll: <c>IsEmpty</c> is read once per consumed queue per system per tick, so it must not fold every worker's cache line.
    /// </summary>
    [Benchmark]
    public bool IsEmpty_UntouchedQueue() => _queue.IsEmpty;

    private readonly record struct DamageEvent(int Target, int Amount);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 128)]
    private struct PaddedTail
    {
        [System.Runtime.InteropServices.FieldOffset(64)]
        public int Value;
    }
}
