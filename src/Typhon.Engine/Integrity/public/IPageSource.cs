using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// A read-only, random-access source of 8 KiB storage pages. The seam every integrity check reads through, so the same
/// catalogue can run against an offline bundle, a live engine, a backup file, or a synthetic in-memory image.
/// </summary>
/// <remarks>
/// <para>
/// Implementations <b>must be side-effect free</b>: no lock acquisition, no WAL replay, no clean-shutdown flag mutation, no
/// page-cache residency change. This is principle <c>PR-2</c> (<i>scan never mutates</i>) expressed as a type constraint —
/// it is what makes a scan always safe to run, including on a production database and on one that will not open.
/// </para>
/// <para>
/// Deliberately <i>not</i> a page cache: no pinning, no eviction, no epochs, no latches. The scanner streams and does its
/// own bounded buffering so it can walk a very large database in a process with a small heap without competing with a live
/// engine's cache budget.
/// </para>
/// </remarks>
[PublicAPI]
public interface IPageSource : IDisposable
{
    /// <summary>Number of pages addressable in this source. Indices <c>[0, PageCount)</c> are in range.</summary>
    int PageCount { get; }

    /// <summary>
    /// Reads page <paramref name="index"/> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="index">Zero-based file-page index.</param>
    /// <param name="destination">Buffer of at least <see cref="IntegrityConstants.PageSize"/> bytes.</param>
    /// <returns>
    /// <c>true</c> when the page was read in full; <c>false</c> when it does not exist in this source (out of range, or a
    /// hole in a sparse source such as an incremental backup). A <c>false</c> return is not an error — the caller decides
    /// whether an absent page is a finding.
    /// </returns>
    bool TryReadPage(int index, Span<byte> destination);

    /// <summary>
    /// Human-readable identity of the source, used to anchor findings: a bundle path, a backup id, or <c>"live"</c>.
    /// </summary>
    string Describe();
}
