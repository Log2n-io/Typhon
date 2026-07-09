using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Data integrity violation — checksum mismatch, structural corruption, invalid page state.
/// Never transient (inherits default false). Requires human intervention or restore from backup.
/// </summary>
[PublicAPI]
public class CorruptionException : StorageException
{
    /// <summary>
    /// Creates a new <see cref="CorruptionException"/> for the specified component and page.
    /// </summary>
    /// <param name="componentName">Name of the component where corruption was detected.</param>
    /// <param name="pageIndex">Page index where corruption was detected, or -1 if not page-specific.</param>
    /// <param name="detail">Human-readable description of the corruption.</param>
    public CorruptionException(string componentName, int pageIndex, string detail)
        : base(TyphonErrorCode.DataCorruption, $"Corruption in '{componentName}' at page {pageIndex}: {detail}")
    {
        ComponentName = componentName;
        PageIndex = pageIndex;
    }

    /// <summary>
    /// Creates a new <see cref="CorruptionException"/> with a specific error code. Used by subclasses.
    /// </summary>
    protected CorruptionException(TyphonErrorCode errorCode, string componentName, int pageIndex, string detail)
        : base(errorCode, $"Corruption in '{componentName}' at page {pageIndex}: {detail}")
    {
        ComponentName = componentName;
        PageIndex = pageIndex;
    }

    /// <summary>Name of the component where corruption was detected.</summary>
    public string ComponentName { get; }

    /// <summary>Page index where corruption was detected, or -1 if not page-specific.</summary>
    public int PageIndex { get; }
}

/// <summary>
/// CRC32C checksum mismatch on a data page — the page is torn or corrupted.
/// Thrown when on-load (OnLoad) verification detects a mismatch outside recovery — there is no repair (FPI was retired; recovery heals torn pages via the
/// rebuild net).
/// </summary>
[PublicAPI]
public class PageCorruptionException : CorruptionException
{
    /// <summary>
    /// Creates a new <see cref="PageCorruptionException"/> for a page whose stored CRC does not match its computed CRC.
    /// </summary>
    /// <param name="pageIndex">Index of the page that failed verification.</param>
    /// <param name="expectedCrc">CRC32C stored in the page header.</param>
    /// <param name="computedCrc">CRC32C recomputed from the page contents.</param>
    public PageCorruptionException(int pageIndex, uint expectedCrc, uint computedCrc) : base(TyphonErrorCode.PageChecksumMismatch, "PageCache", pageIndex,
        $"CRC mismatch: stored=0x{expectedCrc:X8}, computed=0x{computedCrc:X8}. Page is torn/corrupted (no on-load repair).")
    {
        ExpectedCrc = expectedCrc;
        ComputedCrc = computedCrc;
    }

    /// <summary>CRC stored in the page header.</summary>
    public uint ExpectedCrc { get; }

    /// <summary>CRC computed from the page contents.</summary>
    public uint ComputedCrc { get; }
}
