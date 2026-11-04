using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>How a page was asked for, as a page trace records it.</summary>
internal enum PageTraceKind : byte
{
    /// <summary>An epoch-tagged request (<see cref="PagedMMF.RequestPageEpoch"/>): a point read, a write, a regular accessor.</summary>
    Epoch = 0,

    /// <summary>An epoch-tagged request that skips the CRC check: a segment growing.</summary>
    EpochUnchecked = 1,

    /// <summary>A scan accessor's read pin that marks the page recently used: a query scan.</summary>
    ReadPinWarm = 2,

    /// <summary>A scan accessor's read pin that leaves the page cold: a structural scan, a walk.</summary>
    ReadPinCold = 3,

    /// <summary>Introspection: neither tags the clock nor verifies.</summary>
    NoSweep = 4,
}

/// <summary>
/// A page trace being captured (I7 of the huge-scan assessment): one fixed-size record per page request, hit or miss, appended to a native buffer of a
/// fixed capacity. Past it, requests are counted as dropped. Written as a file by <see cref="WriteTo"/> for <c>scripts/page-trace-replay.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Record</b>, 24 bytes, little-endian: the Stopwatch timestamp (8), the file page (4), the operation (4) — the low 32 bits of the TSN of the
/// transaction open on the thread, or of the point-in-time accessor whose lease it holds, else of the thread's pinned epoch — the managed thread id (2), the
/// <see cref="PageTraceKind"/> (1) and a flag byte whose bit 0 is a miss (1). Order is the order slots were claimed in the buffer, which is the order the
/// requests reached the cache within a few instructions. A request that a page-state race sends round its retry loop (PS-15) is recorded once per round,
/// and a miss a racing loader resolves is still recorded as a miss.
/// </para>
/// <para>
/// <b>File</b>: magic <c>TPTR</c>, version, the cache's slot count, the file's page count, the record count, the dropped count, the Stopwatch frequency,
/// then one <see cref="StoragePageType"/> byte per file page, then the records.
/// </para>
/// </remarks>
internal sealed unsafe class PageTraceSession
{
    internal const int RecordSize = 24;
    internal const uint Magic = 0x52545054;   // "TPTR"
    internal const int Version = 1;

    private readonly byte* _records;
    private readonly long _capacity;
    private long _next;
    private long _dropped;

    internal PageTraceSession(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        // native-alloc: a capture's buffer, sized by the caller and freed by Free: a diagnostic, not a structure the resource tree accounts
        _records = (byte*)NativeMemory.Alloc((nuint)(capacity * RecordSize));
    }

    /// <summary>Records written, at most the capacity.</summary>
    internal long Count => Math.Min(Volatile.Read(ref _next), _capacity);

    /// <summary>Requests past the capacity, not recorded.</summary>
    internal long Dropped => Volatile.Read(ref _dropped);

    /// <summary>The highest file page recorded, -1 for none.</summary>
    internal int MaxFilePage
    {
        get
        {
            var max = -1;
            for (var i = 0L; i < Count; i++)
            {
                max = Math.Max(max, *(int*)(_records + i * RecordSize + 8));
            }

            return max;
        }
    }

    /// <summary>One record; the caller holds the cache's in-flight count, so the session is not freed under it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(int filePageIndex, long operation, PageTraceKind kind, bool miss)
    {
        var i = Interlocked.Increment(ref _next) - 1;
        if (i < _capacity)
        {
            var r = _records + i * RecordSize;
            *(long*)r = System.Diagnostics.Stopwatch.GetTimestamp();
            *(int*)(r + 8) = filePageIndex;
            *(int*)(r + 12) = (int)operation;
            *(ushort*)(r + 16) = (ushort)Environment.CurrentManagedThreadId;
            r[18] = (byte)kind;
            r[19] = miss ? (byte)1 : (byte)0;
            *(int*)(r + 20) = 0;
        }
        else
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Writes the trace file: the header, one storage type per file page, then the records.</summary>
    internal void WriteTo(string path, int slotCount, ReadOnlySpan<StoragePageType> pageTypes)
    {
        var count = Count;   // once: the header and the records must agree
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new BinaryWriter(file);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(slotCount);
        writer.Write(pageTypes.Length);
        writer.Write(count);
        writer.Write(Dropped);
        writer.Write(System.Diagnostics.Stopwatch.Frequency);
        writer.Write(MemoryMarshal.AsBytes(pageTypes));
        writer.Flush();

        var remaining = count * RecordSize;
        var at = _records;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, 1 << 20);
            file.Write(new ReadOnlySpan<byte>(at, chunk));
            at += chunk;
            remaining -= chunk;
        }
    }

    internal void Free() => NativeMemory.Free(_records);
}

public partial class PagedMMF
{
    /// <summary>The capture running on this cache, or null: what the request paths record into under the trace flag.</summary>
    private PageTraceSession _pageTrace;

    /// <summary>
    /// Requests between reading <see cref="_pageTrace"/> and finishing their record. Raised BEFORE the read, and the stop exchanges the session out BEFORE it
    /// reads this count — both full fences — so a request either sees no session or is counted, and a stopped session is never freed under a write.
    /// </summary>
    private int _pageTraceInFlight;

    /// <summary>
    /// Starts recording every page request into a buffer of <paramref name="capacity"/> records (24 bytes each). Nothing is recorded unless
    /// <see cref="TelemetryConfig.ProfilerPageTraceEnabled"/>: the request paths read the session only under that JIT-folded flag.
    /// </summary>
    internal void StartPageTrace(long capacity)
    {
        var session = new PageTraceSession(capacity);
        if (Interlocked.CompareExchange(ref _pageTrace, session, null) != null)
        {
            session.Free();
            ThrowHelper.ThrowInvalidOp("A page trace is already running on this cache.");
        }
    }

    /// <summary>Stops the capture and hands its session to the caller, who writes and frees it; null when none runs.</summary>
    internal PageTraceSession StopPageTrace()
    {
        var session = Interlocked.Exchange(ref _pageTrace, null);
        if (session != null)
        {
            var spinner = new SpinWait();
            while (Volatile.Read(ref _pageTraceInFlight) != 0)
            {
                spinner.SpinOnce();
            }
        }

        return session;
    }

    /// <summary>The cache's slot count, for a trace's header.</summary>
    internal int PageTraceSlotCount => MemPagesCount;

    /// <summary>One request into the running capture, if any. Called only under <see cref="TelemetryConfig.ProfilerPageTraceEnabled"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void NotePageTrace(int filePageIndex, PageTraceKind kind, bool miss)
    {
        Interlocked.Increment(ref _pageTraceInFlight);
        try
        {
            var session = Volatile.Read(ref _pageTrace);
            if (session != null)
            {
                // The eviction policy's operation (PagedMMF.Probation.cs), 0 outside any, as the policy sees it: one id space, so a replay sees what
                // the engine decided on.
                session.Add(filePageIndex, t_operation, kind, miss);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pageTraceInFlight);
        }
    }
}
