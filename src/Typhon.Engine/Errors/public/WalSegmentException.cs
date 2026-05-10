using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// A WAL segment file operation failed (creation, rotation, or header validation).
/// </summary>
[PublicAPI]
public class WalSegmentException : DurabilityException
{
    /// <summary>
    /// Creates a new <see cref="WalSegmentException"/>.
    /// </summary>
    /// <param name="segmentPath">Path to the segment file that caused the error.</param>
    /// <param name="detail">Description of the failure.</param>
    public WalSegmentException(string segmentPath, string detail) : base(TyphonErrorCode.WalSegmentError, $"WAL segment error [{segmentPath}]: {detail}")
    {
        SegmentPath = segmentPath;
    }

    /// <summary>Path to the segment file that caused the error.</summary>
    public string SegmentPath { get; }
}
