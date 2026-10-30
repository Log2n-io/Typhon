using Typhon.Profiler;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// What a live attach session may retain before it stops (#822). Retention stops at a tick boundary; the session stays connected and can still be read and
/// saved. Bytes are the only bound: age protects no disk, and a long idle cherry-pick session is the intended use.
/// </summary>
/// <param name="MaxBytes">The temp cache's size limit. Past it no further tick is retained.</param>
/// <param name="MinFreeDiskBytes">The free space to leave on the temp volume: a size limit protects nothing on a volume with less than that left.</param>
public sealed record LiveCacheLimits(long MaxBytes, long MinFreeDiskBytes)
{
    /// <summary>The designed limit, 2 GiB, and 1 GiB left free.</summary>
    public static LiveCacheLimits Default { get; } = new(2L * 1024 * 1024 * 1024, 1024L * 1024 * 1024);

    /// <summary>Test seam: wraps the sink the session's builder writes to, so a test can fail a write without filling a disk.</summary>
    internal Func<ICacheChunkSink, ICacheChunkSink> SinkDecorator { get; init; }
}
