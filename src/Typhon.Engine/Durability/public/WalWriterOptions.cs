using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Configuration options for the WAL Writer thread.
/// </summary>
[PublicAPI]
public sealed class WalWriterOptions
{
    /// <summary>Directory where WAL segment files are stored.</summary>
    public string WalDirectory { get; set; } = "wal";

    /// <summary>
    /// GroupCommit flush interval in milliseconds. The WAL writer auto-flushes at this interval when using <see cref="DurabilityMode.GroupCommit"/>. Default: 5ms.
    /// </summary>
    public int GroupCommitIntervalMs { get; set; } = 5;

    /// <summary>Size of each WAL segment file in bytes. Default: 64 MB.</summary>
    public uint SegmentSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>Number of segments to pre-allocate ahead of the write position. Default: 4.</summary>
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
