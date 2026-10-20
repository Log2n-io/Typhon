using System;
using System.Threading;

namespace Typhon.Engine.Internals;

// The seam between backup capture (#1412, the producer) and the backup store (#1413, the consumer). Both implementation plans
// (claude/design/Durability/PitBackup/impl/1412-capture-plan.md §5 and 1413-store-restore-plan.md §2.6) reconciled into one
// contract: change it here, in one commit, with both sides.
//
// Call order: IBackupPointStore.OpenSession → (capture runs admission: GetChainHead) → BeginPoint → SetImage →
// { AddPage | BeginTailSegment AppendTail* EndTailSegment }* → Complete | Abort.

/// <summary>The kind of a backup point. A compacted point is produced by chain compaction (#1415), never by capture.</summary>
internal enum BackupKind : byte
{
    /// <summary>Every page of the image; the base of a new chain.</summary>
    Full = 1,
    /// <summary>Only the pages written since the parent point's freeze, plus the meta pair.</summary>
    Incremental = 2,
    /// <summary>A base built from a chain inside the store (#1415).</summary>
    Compacted = 3,
}

/// <summary>A backup store as capture sees it: the configured destination of backup points.</summary>
internal interface IBackupPointStore
{
    /// <summary>
    /// Takes the store's exclusive lock and checks the store belongs to <paramref name="databaseId"/>. The session lives for the whole
    /// capture, so the chain head cannot move between admission and publish.
    /// </summary>
    /// <exception cref="BackupStoreMismatchException">The store belongs to another database.</exception>
    /// <exception cref="BackupStoreLockedException">Another writer holds the store's lock.</exception>
    IBackupStoreSession OpenSession(Guid databaseId, CancellationToken ct);
}

/// <summary>An open, locked store. Disposing it releases the lock.</summary>
internal interface IBackupStoreSession : IDisposable
{
    /// <summary>The store's identity, recorded in every pack and in the database's lineage.</summary>
    Guid StoreId { get; }

    /// <summary>The newest point of a chain, or <c>false</c> when the chain has no point in this store. Admission input (BKP-06).</summary>
    bool TryGetChainHead(Guid chainId, out BackupChainHead head);

    /// <summary>
    /// Allocates the point's <c>BackupId</c> (never reused), mints the <c>ChainId</c> for a full point, and opens the temporary pack.
    /// Called before the freeze; nothing is written to the pack until <see cref="IBackupPointSink.SetImage"/>.
    /// </summary>
    IBackupPointSink BeginPoint(in BackupPointPlan plan);
}

/// <summary>The newest point of a chain, as admission needs it.</summary>
internal readonly record struct BackupChainHead(uint BackupId, BackupKind Kind, uint BaseId, long PointLsn, Guid TimelineId);

/// <summary>What capture decided before the freeze.</summary>
/// <param name="DatabaseId">The database the point belongs to.</param>
/// <param name="TimelineId">The image's <c>BK_TimelineId</c>.</param>
/// <param name="Kind"><see cref="BackupKind.Full"/> or <see cref="BackupKind.Incremental"/>, decided by admission.</param>
/// <param name="ChainId">Incremental: the head's chain. Full: <see cref="Guid.Empty"/>, the store mints it.</param>
/// <param name="ParentId">Incremental: the head's <c>BackupId</c>, re-checked by the store under its lock. Full: 0.</param>
/// <param name="DatabaseFormatRevision">The image's on-disk format revision.</param>
/// <param name="EngineVersion">Informational.</param>
/// <param name="Offline">Captured with no engine (offline capture, 02 §8).</param>
internal readonly record struct BackupPointPlan(Guid DatabaseId, Guid TimelineId, BackupKind Kind, Guid ChainId, uint ParentId,
    int DatabaseFormatRevision, string EngineVersion, bool Offline);

/// <summary>
/// One backup point being written. Called by ONE thread, sequentially; the sink takes no engine lock and never calls back into the
/// engine. Every buffer argument is borrowed for the call only: the sink copies or consumes it before returning. Any exception
/// propagates to capture, which runs its failure path and then calls <see cref="Abort"/>.
/// </summary>
internal interface IBackupPointSink
{
    /// <summary>The point's id, allocated by <see cref="IBackupStoreSession.BeginPoint"/>.</summary>
    uint BackupId { get; }

    /// <summary>The point's chain: the plan's for an incremental, minted by the store for a full point.</summary>
    Guid ChainId { get; }

    /// <summary>
    /// The image's identity, known right after the freeze. Called exactly once, before any page or tail call; the pack header
    /// (freeze-time facts only) is written here.
    /// </summary>
    /// <param name="imagePages"><c>N</c>: the data file's physical length in pages at the freeze.</param>
    /// <param name="checkpointLsn"><c>C</c>: the CheckpointLSN read from the frozen meta pair.</param>
    /// <param name="freezeUtcTicks">The freeze instant, UTC ticks.</param>
    void SetImage(int imagePages, long checkpointLsn, long freezeUtcTicks);

    /// <summary>
    /// One 8 KiB image of file page <paramref name="pageId"/>. <c>0 ≤ pageId &lt; N</c>; each page at most once; any order
    /// (copy-before-write pre-images arrive out of order); pages 0 and 1 always come; a full point adds every page of <c>[0, N)</c>.
    /// </summary>
    /// <param name="pageId">The file page index.</param>
    /// <param name="image">Exactly 8 192 bytes, valid only during the call.</param>
    /// <param name="suspect">The page failed its checksum and is not all-zero.</param>
    void AddPage(int pageId, ReadOnlySpan<byte> image, bool suspect);

    /// <summary>
    /// Starts one WAL segment of the tail. Segments come in ascending id, may interleave with <see cref="AddPage"/> between segment
    /// calls, and never nest. RESTORE CONTRACT: the segment is recreated as <c>{segmentId:D16}.wal</c> at
    /// <paramref name="declaredSize"/>, its copied bytes then zeros (C3).
    /// </summary>
    /// <param name="segmentId">The segment's id.</param>
    /// <param name="firstLsn">The segment header's first LSN.</param>
    /// <param name="declaredSize">The segment header's size (a 4 096 multiple, ≥ the copied prefix).</param>
    void BeginTailSegment(long segmentId, long firstLsn, uint declaredSize);

    /// <summary>The next bytes of the current segment, in file order from offset 0. Valid only during the call.</summary>
    void AppendTail(ReadOnlySpan<byte> bytes);

    /// <summary>Ends the current segment.</summary>
    void EndTailSegment();

    /// <summary>
    /// Seals and durably publishes the point (BKP-08): either throws with no point visible, or returns with it durable. A catalog
    /// update failure after the publish does not fail it. Never retried by capture.
    /// </summary>
    /// <param name="pointLsn"><c>P</c>: the highest LSN in <c>(C, end of the tail's valid prefix]</c> recovery applies as a unit (a
    /// commit marker or a fence record); <c>C</c> when there is none.</param>
    /// <param name="tailEndUtcTicks">When the tail copy ended, UTC ticks.</param>
    BackupPointInfo Complete(long pointLsn, long tailEndUtcTicks);

    /// <summary>Abandons the point. Idempotent, never throws, safe at any time before <see cref="Complete"/> returns.</summary>
    void Abort();
}

/// <summary>A published point, as capture records it in the database's lineage.</summary>
internal readonly record struct BackupPointInfo(uint BackupId, Guid ChainId, BackupKind Kind, long PointLsn, long CheckpointLsn,
    int ImagePages, int PageCount, int SuspectCount, long PackBytes, string PackPath);
