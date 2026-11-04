using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// An open's index rebuild read back a block of its temp file that failed a check (RB-10): its magic, kind, owner, sequence, stride, count, or one of its
/// two CRCs. The rebuild never uses an unchecked record and never repairs one, so the open fails; the indexes are derived data, and the next open rebuilds
/// them from the clusters with a fresh temp file.
/// </summary>
[PublicAPI]
public sealed class IndexRebuildTempCorruptException : DurabilityException
{
    /// <summary>Creates the exception for the block at <paramref name="offset"/> of <paramref name="path"/>.</summary>
    /// <param name="path">The temp file.</param>
    /// <param name="offset">The block's byte offset in it.</param>
    /// <param name="check">The check that failed.</param>
    public IndexRebuildTempCorruptException(string path, long offset, string check)
        : base(TyphonErrorCode.IndexRebuildTempCorrupt, $"Index rebuild temp block at offset {offset} of '{path}' failed its check: {check}")
    {
        Path = path;
        Offset = offset;
        Check = check;
    }

    /// <summary>The temp file.</summary>
    public string Path { get; }

    /// <summary>The failing block's byte offset in the temp file.</summary>
    public long Offset { get; }

    /// <summary>The check that failed.</summary>
    public string Check { get; }
}
