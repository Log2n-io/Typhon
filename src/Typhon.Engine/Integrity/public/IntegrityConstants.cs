using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// The handful of on-disk layout constants an integrity consumer needs to size buffers and interpret a
/// <see cref="Locus"/>. Mirrors the engine-internal storage constants so external callers (CLI, Workbench, tests) do not
/// have to hard-code them.
/// </summary>
[PublicAPI]
public static class IntegrityConstants
{
    /// <summary>Size in bytes of one storage page.</summary>
    public const int PageSize = 8192;

    /// <summary>Size in bytes of the page header zone (base header + metadata) that precedes the raw-data area.</summary>
    public const int PageHeaderSize = 192;

    /// <summary>Size in bytes of the raw-data area of a page — the part that holds chunks, directory entries or bitmap words.</summary>
    public const int PageRawDataSize = PageSize - PageHeaderSize;

    /// <summary>File name of the paged data file inside a <c>.typhon</c> bundle directory.</summary>
    public const string DataFileName = "data";

    /// <summary>File name of the single-writer lock inside a <c>.typhon</c> bundle directory.</summary>
    public const string LockFileName = "db.lock";

    /// <summary>Sub-directory holding the WAL segments inside a <c>.typhon</c> bundle directory.</summary>
    public const string WalDirectoryName = "wal";

    /// <summary>Canonical extension of a Typhon database bundle directory.</summary>
    public const string BundleExtension = ".typhon";
}
