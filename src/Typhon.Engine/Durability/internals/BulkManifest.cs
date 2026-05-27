using JetBrains.Annotations;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// 56-byte header for a BulkLoad manifest chunk (<see cref="WalChunkType.BulkBegin"/> or <see cref="WalChunkType.BulkEnd"/>).
/// Followed by <see cref="PageRangeCount"/> packed <see cref="BulkPageRange"/> entries describing the segments + pages allocated during the bulk session.
/// </summary>
/// <remarks>
/// <para>
/// <b>BulkBegin</b> is emitted (as a *placeholder* — empty page-range list, zero entity counts) when
/// <see cref="Typhon.Engine.Transactions.DatabaseEngine.BeginBulkLoad"/> opens a session. It anchors the bulk's
/// LSN in the WAL stream. <b>BulkEnd</b> is emitted (with the final manifest) by
/// <see cref="Typhon.Engine.Transactions.BulkLoadSession.CompleteBulkLoad"/> after the synchronous checkpoint completes.
/// </para>
/// <para>
/// <b>Recovery contract:</b> a bulk session is durable iff both <c>BulkBegin</c> and <c>BulkEnd</c> are on disk
/// AND <c>BulkEnd.LSN ≤ DurableLSN</c>. Otherwise <c>WalRecovery</c>'s Phase 3b frees every page in the manifest
/// via <c>BitmapL3.FreeRange</c> and removes the bulk segments from the registry (wholesale discard). See
/// <c>claude/design/Durability/BulkLoad/03-recovery.md</c> for the full state machine and
/// <c>claude/rules/durability.md</c> module <b>BulkLoad</b> for the invariants (BL-01..BL-04).
/// </para>
/// <para>
/// <b>WP-07 compliance:</b> <see cref="Lsn"/> sits at body offset 0 so <c>WalSegmentReader.LastValidLSN</c>
/// extracts it correctly via the generic <c>body[0..8]</c> convention.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
internal struct BulkManifestHeader
{
    /// <summary>WAL log sequence number assigned to this chunk. Placed at body offset 0 for WP-07.</summary>
    public long Lsn;

    /// <summary>Unique 64-bit ID identifying the bulk session. Stable across the chunk's <c>BulkBegin</c> / <c>BulkEnd</c> pair.</summary>
    public long BulkSessionId;

    /// <summary>
    /// LSN of the matching <see cref="WalChunkType.BulkBegin"/> chunk. In <c>BulkBegin</c> chunks this equals <see cref="Lsn"/> (self-reference); in
    /// <c>BulkEnd</c> chunks it cross-references the matching <c>BulkBegin</c>. The pair (<see cref="BulkSessionId"/>, <see cref="BulkBeginLsn"/>) is the
    /// bulk's identity at recovery time.
    /// </summary>
    public long BulkBeginLsn;

    /// <summary>
    /// Number of unique segments allocated during the bulk. Zero in <c>BulkBegin</c> placeholder; final in <c>BulkEnd</c>. Always ≤
    /// <see cref="PageRangeCount"/> (one segment may span multiple non-contiguous page ranges).
    /// </summary>
    public int SegmentCount;

    /// <summary>
    /// Number of <see cref="BulkPageRange"/> entries following this header inline. Zero in <c>BulkBegin</c>; final in <c>BulkEnd</c>. Total body size =
    /// <see cref="SizeInBytes"/> + 16 × <see cref="PageRangeCount"/>.
    /// </summary>
    public int PageRangeCount;

    /// <summary>
    /// Telemetry: entities spawned via <see cref="Typhon.Engine.BulkLoadSession.Spawn{TArch}"/>. Zero in <c>BulkBegin</c>; final in <c>BulkEnd</c>.
    /// </summary>
    public long EntitiesSpawned;

    /// <summary>
    /// Telemetry: entities updated via <see cref="Typhon.Engine.BulkLoadSession.Update{T}"/>. Zero in <c>BulkBegin</c>; final in <c>BulkEnd</c>.
    /// </summary>
    public long EntitiesUpdated;

    /// <summary>
    /// Telemetry: entities destroyed via <see cref="Typhon.Engine.BulkLoadSession.Destroy"/>. Zero in <c>BulkBegin</c>; final in <c>BulkEnd</c>.
    /// </summary>
    public long EntitiesDestroyed;

    /// <summary>Expected size of this struct in bytes. Body suffix (<see cref="BulkPageRange"/> array) is variable.</summary>
    public const int SizeInBytes = 56;
}

/// <summary>
/// 16-byte entry in the page-range list following a <see cref="BulkManifestHeader"/>. Describes one contiguous run of pages allocated to a segment during
/// the bulk.
/// </summary>
/// <remarks>
/// A segment may have multiple ranges (the segment grew non-contiguously). <c>WalRecovery</c>'s Phase 3b iterates these entries on a non-durable bulk and
/// calls <c>BitmapL3.FreeRange(<see cref="FirstPageId"/>, <see cref="PageCount"/>)</c> for each.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
internal struct BulkPageRange
{
    /// <summary>Root page index of the owning segment (the segment's identity in <c>ManagedPagedMMF._segments</c>).</summary>
    public int SegmentRootPageId;

    /// <summary>Lowest page id in this contiguous range.</summary>
    public int FirstPageId;

    /// <summary>Number of pages in this range. Total: pages <c>FirstPageId</c> .. <c>FirstPageId + PageCount - 1</c>.</summary>
    public int PageCount;

    /// <summary>Reserved for future use (per-range archetype id, compression marker, etc.). Producers write 0.</summary>
    public int Reserved;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 16;
}
