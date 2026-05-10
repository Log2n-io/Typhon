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
/// validation; it eagerly allocates <c>count</c> rings of the configured size. <see cref="Shutdown"/> is called
/// from <see cref="TyphonProfiler.Stop"/>; it drops every ring reference, including any that may still be linked
/// into producer chains — the caller is responsible for collapsing chains first (see TyphonProfiler.Stop).
/// Re-initialising after shutdown is supported and yields a fresh pool.
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
        s_pool = new ConcurrentStack<TraceRecordRing>();
        s_initialCount = count;
        s_bufferSizeBytes = bufferSizeBytes;
        Interlocked.Exchange(ref s_acquired, 0);
        Interlocked.Exchange(ref s_exhausted, 0);
        Interlocked.Exchange(ref s_inUse, 0);

        for (var i = 0; i < count; i++)
        {
            s_pool.Push(new TraceRecordRing(bufferSizeBytes));
        }
    }

    /// <summary>Tear down the pool. Drops every ring reference; counters remain readable for the AntHill _ExitTree printout.</summary>
    public static void Shutdown()
    {
        s_pool = null;
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
        if (pool == null || !pool.TryPop(out var ring))
        {
            Interlocked.Increment(ref s_exhausted);
            return null;
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
        if (pool == null)
        {
            // Pool already shut down — let the ring be GC'd.
            return;
        }
        ring.Reset();
        pool.Push(ring);
        Interlocked.Decrement(ref s_inUse);
    }

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
