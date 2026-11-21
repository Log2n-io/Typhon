using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Pre-allocated pool of <see cref="TraceRecordRing"/> instances used as <b>spillover</b> buffers for
/// <see cref="ThreadSlot"/> chains. When a slot's primary ring overflows, the producer pops a spillover from this
/// pool, links it into its slot's chain, and continues writing. The consumer recycles spilled rings back to the
/// pool once they've drained.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> <see cref="Initialize"/> is called from <see cref="TyphonProfiler.Start"/> after option
/// validation; it eagerly allocates <c>count</c> rings of the configured size (or takes back the previous session's).
/// The module initialiser builds it lazily instead when no session is expected (<see cref="InitializeLazy"/>).
/// <see cref="Shutdown"/> is called from <see cref="TyphonProfiler.Stop"/> after every chain has been collapsed back
/// to its primary (see TyphonProfiler.Stop); it keeps the rings for the next session of the same size.
/// </para>
/// <para>
/// <b>Reuse across sessions.</b> <see cref="Shutdown"/> keeps the rings it held (and any released after it), and the next
/// <see cref="Initialize"/> with the same buffer size takes them back instead of allocating. Each ring is a managed array on the
/// large-object heap, freed only by a gen-2 collection: a process that starts and stops the profiler repeatedly used to allocate
/// <c>count × size</c> (128 MiB by default) per session and leave the GC to find it — a test cycling 100 sessions drove its host
/// to several GiB. The cost of reuse is that a process keeps its last pool after Stop.
/// </para>
/// <para>
/// <b>Concurrency.</b> Backed by a <see cref="ConcurrentStack{T}"/>. Producers (any worker thread) call
/// <see cref="TryAcquire"/>; the consumer thread calls <see cref="Release"/>. Both are lock-free.
/// </para>
/// <para>
/// <b>Counters.</b> <see cref="AcquiredCount"/> is the total number of successful acquires across the pool's
/// lifetime; <see cref="ExhaustedCount"/> counts failed acquires (pool was empty); <see cref="InUseCount"/> is the
/// instantaneous "outstanding" count (acquires - releases). All three use plain <see cref="Interlocked"/>
/// arithmetic so they stay accurate under contention. Snapshotted at <see cref="TyphonProfiler.Stop"/> for
/// post-mortem.
/// </para>
/// </remarks>
internal static class SpilloverRingPool
{
    private static ConcurrentStack<TraceRecordRing> s_pool;
    private static ConcurrentStack<TraceRecordRing> s_retained;
    private static int s_retainedSizeBytes;
    private static long s_ringsAllocated;
    private static int s_lazyRemaining;
    private static int s_initialCount;
    private static int s_bufferSizeBytes;
    private static long s_acquired;
    private static long s_exhausted;
    private static long s_inUse;

    /// <summary>True once <see cref="Initialize"/> has run and <see cref="Shutdown"/> has not yet been called.</summary>
    public static bool IsInitialized => s_pool != null;

    /// <summary>Eagerly allocate <paramref name="count"/> rings of <paramref name="bufferSizeBytes"/> each. Idempotent if the configuration matches; otherwise re-initialises the pool from scratch.</summary>
    public static void Initialize(int count, int bufferSizeBytes)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Spillover buffer count must be non-negative.");
        }
        if (count > 0)
        {
            if (bufferSizeBytes < 64 * 1024 || (bufferSizeBytes & (bufferSizeBytes - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferSizeBytes), "Spillover buffer size must be a power of two and at least 64 KiB.");
            }
        }

        // Re-initialisation drops the previous pool's references; any rings still linked into a producer chain stay
        // alive via that reference until the chain is collapsed. Counters are reset so a Start/Stop/Start cycle
        // reads cleanly.
        // The size is written before the pool is published, and the pool filled before it is: a Release that reads the new pool reads its size too.
        var pool = new ConcurrentStack<TraceRecordRing>();
        s_initialCount = count;
        Volatile.Write(ref s_bufferSizeBytes, bufferSizeBytes);
        Interlocked.Exchange(ref s_acquired, 0);
        Interlocked.Exchange(ref s_exhausted, 0);
        Interlocked.Exchange(ref s_inUse, 0);

        Volatile.Write(ref s_lazyRemaining, 0);
        // Take back the previous session's rings when they have this size; a size change lets them go to the GC.
        var retained = Interlocked.Exchange(ref s_retained, null);
        for (var i = 0; i < count; i++)
        {
            if (retained == null || s_retainedSizeBytes != bufferSizeBytes || !retained.TryPop(out var ring))
            {
                ring = new TraceRecordRing(bufferSizeBytes);
                Interlocked.Increment(ref s_ringsAllocated);
            }
            pool.Push(ring);
        }
        Volatile.Write(ref s_pool, pool);
    }

    /// <summary>
    /// A pool of <paramref name="count"/> rings that allocates each one the first time it is needed — the module-initialiser path, for events emitted
    /// before <see cref="TyphonProfiler.Start"/>. A process whose producer gate is open but that never starts a session (an enabled profiler with no
    /// output channel: the engine test suite's configuration) then holds no spillover memory unless something actually spills. <see cref="EnsureAllocated"/>
    /// makes it eager at <c>Start</c>, so a running session never allocates on a producer's overflow path.
    /// </summary>
    public static void InitializeLazy(int count, int bufferSizeBytes)
    {
        Initialize(0, bufferSizeBytes);
        Volatile.Write(ref s_lazyRemaining, count);
        s_initialCount = count;
    }

    /// <summary>Allocates the rings a lazy pool still owes, so the pool is fully pre-allocated. No-op on an eager pool.</summary>
    public static void EnsureAllocated()
    {
        var pool = s_pool;
        while (pool != null && Interlocked.Decrement(ref s_lazyRemaining) >= 0 && NewRing() is { } ring)
        {
            pool.Push(ring);
        }
        Volatile.Write(ref s_lazyRemaining, 0);
    }

    private static TraceRecordRing NewRing()
    {
        var size = Volatile.Read(ref s_bufferSizeBytes);
        if (size <= 0)
        {
            return null;   // shut down under us
        }
        Interlocked.Increment(ref s_ringsAllocated);
        return new TraceRecordRing(size);
    }

    /// <summary>
    /// Tear down the pool: no ring can be acquired until the next <see cref="Initialize"/>. The rings it held are kept for that next Initialize of the same
    /// size (see the remarks); counters remain readable for the AntHill _ExitTree printout.
    /// </summary>
    public static void Shutdown()
    {
        // The rings kept are clean (never acquired, or Reset by Release).
        s_retainedSizeBytes = s_bufferSizeBytes;
        s_retained = s_pool;
        Volatile.Write(ref s_pool, null);
        Volatile.Write(ref s_lazyRemaining, 0);
        s_initialCount = 0;
        s_bufferSizeBytes = 0;
        // Counters are deliberately NOT cleared on shutdown so post-mortem callers can still read AcquiredCount /
        // ExhaustedCount after Stop. Cleared on the next Initialize() instead.
    }

    /// <summary>
    /// Pop one ring from the pool. Returns <c>null</c> if the pool is empty (or not initialised — equivalent to
    /// "spillover disabled" in <see cref="ProfilerOptions"/>); the caller should fall back to its drop path.
    /// </summary>
    public static TraceRecordRing TryAcquire()
    {
        var pool = s_pool;
        if (pool == null)
        {
            Interlocked.Increment(ref s_exhausted);
            return null;
        }
        if (!pool.TryPop(out var ring))
        {
            // A lazy pool allocates the rings it still owes on demand (see InitializeLazy).
            if (Volatile.Read(ref s_lazyRemaining) <= 0 || Interlocked.Decrement(ref s_lazyRemaining) < 0)
            {
                Interlocked.Increment(ref s_exhausted);
                return null;
            }
            ring = NewRing();
            if (ring == null)
            {
                Interlocked.Increment(ref s_exhausted);
                return null;
            }
        }
        Interlocked.Increment(ref s_acquired);
        Interlocked.Increment(ref s_inUse);
        return ring;
    }

    /// <summary>
    /// Return a ring to the pool. <see cref="TraceRecordRing.Reset"/> is called here so the next acquirer always
    /// sees a clean buffer (no stale head/tail, no leftover drop counters, no forward link from the previous chain).
    /// </summary>
    public static void Release(TraceRecordRing ring)
    {
        if (ring == null)
        {
            return;
        }
        var pool = s_pool;
        if (pool == null || ring.Capacity != s_bufferSizeBytes)
        {
            // Pool already shut down, or re-initialised with another size: keep a ring of the retained size for the next session, else
            // let the GC have it.
            var retained = s_retained;
            if (retained != null && ring.Capacity == s_retainedSizeBytes)
            {
                ring.Reset();
                retained.Push(ring);
            }
            return;
        }
        ring.Reset();
        pool.Push(ring);
        Interlocked.Decrement(ref s_inUse);
    }

    /// <summary>Rings allocated over the process's lifetime: a re-<see cref="Initialize"/> of the same size adds none.</summary>
    internal static long RingsAllocated => Interlocked.Read(ref s_ringsAllocated);

    /// <summary>Total number of times <see cref="TryAcquire"/> succeeded since the last <see cref="Initialize"/>.</summary>
    public static long AcquiredCount => Interlocked.Read(ref s_acquired);

    /// <summary>Total number of times <see cref="TryAcquire"/> failed (pool empty or not initialised) since the last <see cref="Initialize"/>.</summary>
    public static long ExhaustedCount => Interlocked.Read(ref s_exhausted);

    /// <summary>Instantaneous count of rings outside the pool (linked into producer chains awaiting drain).</summary>
    public static long InUseCount => Interlocked.Read(ref s_inUse);

    /// <summary>The configured per-buffer size last passed to <see cref="Initialize"/>; 0 when the pool isn't initialised.</summary>
    public static int BufferSizeBytes => s_bufferSizeBytes;

    /// <summary>The number of buffers <see cref="Initialize"/> allocated; 0 when the pool isn't initialised.</summary>
    public static int InitialCount => s_initialCount;
}
