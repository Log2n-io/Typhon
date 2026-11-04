using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

// The page trace (I7 of the huge-scan assessment, claude/scratch/huge-scan-assessment.md): every page request of a stretch of the engine's life, hit or
// miss, for scripts/page-trace-replay.py to replay against eviction policies. Recorded only while TelemetryConfig.ProfilerPageTraceEnabled; off, the
// request paths carry no trace code at all.
public partial class DatabaseEngine
{
    /// <summary>The trace file an environment-driven capture writes at dispose, or null.</summary>
    private string _pageTraceFile;

    /// <summary>Engines of this process that took the environment's capture: the first writes the file named, each later one its own, suffixed.</summary>
    private static int s_environmentPageTraces;

    /// <summary>
    /// Starts recording every page request, up to <paramref name="capacityRecords"/> records of 24 bytes: past it, requests are only counted. Records nothing
    /// unless <see cref="TelemetryConfig.ProfilerPageTraceEnabled"/> (<c>TYPHON__PROFILER__PAGETRACE__ENABLED=true</c>, read once when the engine assembly
    /// loads).
    /// </summary>
    internal void StartPageTrace(long capacityRecords) => MMF.StartPageTrace(capacityRecords);

    /// <summary>
    /// Stops the capture and writes it to <paramref name="path"/> with the storage type of every file page; returns the records written, -1 when no capture
    /// ran. The page types are classified now, so a page freed or reused during the capture carries its type at the end.
    /// </summary>
    internal long WritePageTrace(string path)
    {
        var session = MMF.StopPageTrace();
        if (session == null)
        {
            return -1;
        }

        try
        {
            // Past the file's length too: a page allocated and not yet written is requested like any other. The occupancy bitmap covers it.
            var pageCount = (int)Math.Min(Math.Max(MMF.StorageFilePageCount, session.MaxFilePage + 1), MMF.OccupancyCapacityPages);
            var types = new StoragePageType[pageCount];
            ClassifyPages(types, pageCount);
            session.WriteTo(path, MMF.PageTraceSlotCount, types);
            return session.Count;
        }
        finally
        {
            session.Free();
        }
    }

    /// <summary>
    /// A capture driven by the environment, for a host whose code cannot call <see cref="StartPageTrace"/>: <c>TYPHON_PAGE_TRACE_FILE</c> names the file,
    /// written at dispose, and <c>TYPHON_PAGE_TRACE_RECORDS</c> the capacity (default 16M records, 384 MiB). Started once the archetypes are initialized,
    /// so the open's own rebuild is not in it. The first engine of the process writes the file named; each later one writes its own, <c>-2</c>, <c>-3</c>…
    /// before the extension, so none overwrites another's.
    /// </summary>
    private void StartEnvironmentPageTrace()
    {
        if (!TelemetryConfig.ProfilerPageTraceEnabled || _pageTraceFile != null)
        {
            return;
        }

        var file = Environment.GetEnvironmentVariable("TYPHON_PAGE_TRACE_FILE");
        if (string.IsNullOrEmpty(file))
        {
            return;
        }

        var capacity = long.TryParse(Environment.GetEnvironmentVariable("TYPHON_PAGE_TRACE_RECORDS"), out var n) && n > 0 ? n : 16L << 20;
        var seq = Interlocked.Increment(ref s_environmentPageTraces);
        if (seq > 1)
        {
            file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file) ?? "",
                $"{System.IO.Path.GetFileNameWithoutExtension(file)}-{seq}{System.IO.Path.GetExtension(file)}");
        }

        StartPageTrace(capacity);
        _pageTraceFile = file;
    }

    /// <summary>Writes an environment-driven capture, first thing at dispose, while every segment is still registered.</summary>
    private void WriteEnvironmentPageTrace()
    {
        var file = _pageTraceFile;
        if (file == null)
        {
            return;
        }

        _pageTraceFile = null;
        try
        {
            WritePageTrace(file);
        }
        catch (Exception e)
        {
            // A diagnostic must not fail the close.
            LogPageTraceWriteFailed(file, e);
        }
    }

    [LoggerMessage(LogLevel.Warning, "The page trace from TYPHON_PAGE_TRACE_FILE could not start; the engine opens without it.")]
    private partial void LogPageTraceStartFailed(Exception exception);

    [LoggerMessage(LogLevel.Warning, "The page trace could not be written to '{file}'; the engine closes regardless.")]
    private partial void LogPageTraceWriteFailed(string file, Exception exception);
}
