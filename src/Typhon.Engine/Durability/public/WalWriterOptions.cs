using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Configuration options for the WAL Writer thread.
/// </summary>
[PublicAPI]
public sealed class WalWriterOptions
{
    /// <summary>
    /// Directory where WAL segment files are stored. Leave <see langword="null"/> (the default) to let the engine derive <c>{bundle}/wal</c> inside the
    /// database's <c>.typhon</c> bundle — this keeps each database's WAL private and drops the old cwd-relative shared <c>wal/</c>. Set explicitly only to
    /// place the WAL elsewhere.
    /// </summary>
    public string WalDirectory { get; set; }

    /// <summary>
    /// GroupCommit flush interval in milliseconds. The WAL writer auto-flushes at this interval when using <see cref="DurabilityMode.GroupCommit"/>. Default: 5ms.
    /// </summary>
    public int GroupCommitIntervalMs { get; set; } = 5;

    /// <summary>Size of each WAL segment file in bytes. Default: 64 MB.</summary>
    public uint SegmentSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Size of the FIRST segment created when the WAL opens, in bytes. Default: 16 MB. Clamped to <see cref="SegmentSize"/>
    /// when larger; 0 means "use <see cref="SegmentSize"/>", restoring the pre-#784 behaviour.
    /// </summary>
    /// <remarks>
    /// A database that never fills a segment never rotates, so this one file IS its entire WAL for the lifetime of the
    /// bundle. Sizing it for steady-state throughput therefore charged every small database the full
    /// <see cref="SegmentSize"/> on disk forever — a 1.8 MB database carried a 320 MiB <c>wal/</c> directory. Segments
    /// created by rotation are unaffected: from the first rotation on, everything is <see cref="SegmentSize"/> as before,
    /// so a busy database's steady state is unchanged.
    /// </remarks>
    public uint InitialSegmentSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Number of segments to pre-allocate ahead of the write position. Default: 4.
    /// </summary>
    /// <remarks>
    /// The pool is built on the FIRST ROTATION, not at open — until a database has actually filled a segment there is no
    /// evidence it ever will, and pre-allocating eagerly left <c>PreAllocateSegments * SegmentSize</c> of empty, headerless
    /// files that WAL reclamation cannot see (they are never sealed, so they never enter the reclaim list). See #784.
    /// </remarks>
    public int PreAllocateSegments { get; set; } = 4;

    /// <summary>
    /// Size of the 4096-byte-aligned staging buffer used for O_DIRECT writes. Default: 256 KB. Must be a multiple of 4096.
    /// </summary>
    public int StagingBufferSize { get; set; } = 256 * 1024;

    /// <summary>
    /// Core affinity for the WAL writer thread. -1 for no affinity (default). When set, pins the writer thread to the specified logical core.
    /// </summary>
    public int WriterThreadCoreAffinity { get; set; } = -1;

    /// <summary>
    /// Whether to open segment files with FUA (Force Unit Access) for per-write durability. When true, each write is durable on return. When false,
    /// explicit flush calls are needed.
    /// Default: true (safe default for Immediate mode support).
    /// </summary>
    public bool UseFUA { get; set; } = true;
}
