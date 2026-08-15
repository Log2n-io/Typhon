using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Carries the two capture-window numbers that are only knowable at the <i>end</i> of a profiling session — the final TSN and the final runtime tick — from
/// the engine and runtime that own them to the exporter that writes them into the trace header (#614, D-5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a holder instead of reading the engine at close.</b> The trace is finalized from the engine storage's <c>DisposingEvent</c>, which by design fires
/// <i>after</i> <c>DatabaseEngine.Dispose</c> — so at the moment the header is patched there is no live engine left to ask. Both producers therefore publish
/// while they are still alive: the engine at <c>PersistEngineState</c> (its own "this is the final TSN" moment) and the runtime at shutdown. Last write wins,
/// which is exactly right for a monotonic counter.
/// </para>
/// <para>
/// <b>Absent values stay absent.</b> A host profiling without a runtime never publishes a tick, and one without an engine never publishes a TSN; those fields
/// simply read back as their start values and the header records a zero-width window. That is honest — a capture with no engine genuinely has no transaction
/// window — and is preferable to inventing a plausible number.
/// </para>
/// <para><b>Threading:</b> plain interlocked/volatile scalars. Written from teardown paths, read once at close; no ordering relationship to protect.</para>
/// </remarks>
internal static class ProfilerCaptureCounters
{
    private static long TSNAtStart;
    private static long TSNLatest;
    private static long TickAtStart;
    private static long TickLatest;

    /// <summary>
    /// Seeds both windows at capture start. Called by <see cref="ProfilerSessionMetadataBuilder"/> with the engine's next-free TSN and the runtime's current
    /// tick, so a second capture in the same process never inherits the first one's numbers.
    /// </summary>
    internal static void BeginCapture(long tsn, long tick)
    {
        Volatile.Write(ref TSNAtStart, tsn);
        Volatile.Write(ref TSNLatest, tsn);
        Volatile.Write(ref TickAtStart, tick);
        Volatile.Write(ref TickLatest, tick);
    }

    /// <summary>Publishes the engine's current next-free TSN. Called from <c>DatabaseEngine.PersistEngineState</c>.</summary>
    internal static void RecordEngineTsn(long tsn) => Volatile.Write(ref TSNLatest, tsn);

    /// <summary>Publishes the runtime's current tick number. Called from <c>TyphonRuntime.StopInternal</c>.</summary>
    internal static void RecordRuntimeTick(long tick) => Volatile.Write(ref TickLatest, tick);

    /// <summary>
    /// The close-time header values: the highest TSN observed, and how many runtime ticks the capture spans. Tick count saturates at
    /// <see cref="uint.MaxValue"/> — at 60 Hz that is over two years of continuous capture, so clamping is a formality rather than a real truncation.
    /// </summary>
    internal static (long TsnMax, uint TickCount) SnapshotAtClose()
    {
        var tsn = Volatile.Read(ref TSNLatest);
        var elapsedTicks = Volatile.Read(ref TickLatest) - Volatile.Read(ref TickAtStart);
        if (elapsedTicks < 0)
        {
            elapsedTicks = 0;
        }
        return (tsn, elapsedTicks > uint.MaxValue ? uint.MaxValue : (uint)elapsedTicks);
    }

    /// <summary>The TSN the capture started at, for callers that need the window's left edge without going back to the session metadata.</summary>
    internal static long TsnAtStart => Volatile.Read(ref TSNAtStart);
}
