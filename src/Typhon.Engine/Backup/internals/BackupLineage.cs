using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// What this session knows about the database's last backup point, and whether its live change map covers every data-file write since that point's
/// freeze. Owned by the backup thread; read at open and close only around it.
/// </summary>
internal sealed class BackupLineage
{
    /// <summary>The live change map covers every data-file write since <see cref="LastBackupId"/>'s freeze.</summary>
    public bool Trusted { get; set; }

    /// <summary>The store the last point went to.</summary>
    public Guid StoreId { get; set; }

    /// <summary>The last point's chain.</summary>
    public Guid ChainId { get; set; }

    /// <summary>The last point's id in its store; 0 for none.</summary>
    public uint LastBackupId { get; set; }

    /// <summary>The last point's <c>P</c>.</summary>
    public long LastPointLsn { get; set; }

    /// <summary>Records a published point: from its freeze on, the live map holds exactly the writes since it.</summary>
    public void Published(Guid storeId, in BackupPointInfo point, bool mapEnabled)
    {
        Trusted = mapEnabled;
        StoreId = storeId;
        ChainId = point.ChainId;
        LastBackupId = point.BackupId;
        LastPointLsn = point.PointLsn;
    }
}

/// <summary>Full or incremental (BKP-06).</summary>
internal static class BackupAdmission
{
    /// <summary>
    /// An incremental only when the caller allows one, the live map is trusted, and the store's head of the lineage's chain is the lineage's last point,
    /// on this database's timeline; a full point otherwise. Any doubt costs I/O, never correctness.
    /// </summary>
    /// <param name="requested"><see cref="BackupKind.Full"/> forces a full point; <see cref="BackupKind.Incremental"/> allows an incremental.</param>
    /// <param name="lineage">The session's lineage.</param>
    /// <param name="session">The open store session.</param>
    /// <param name="timelineId">The database's current timeline.</param>
    /// <param name="parent">Receives the head an incremental builds on.</param>
    public static BackupKind Decide(BackupKind requested, BackupLineage lineage, IBackupStoreSession session, Guid timelineId, out BackupChainHead parent)
    {
        parent = default;
        if (requested == BackupKind.Full || !lineage.Trusted || lineage.StoreId != session.StoreId || lineage.LastBackupId == 0)
        {
            return BackupKind.Full;
        }

        if (!session.TryGetChainHead(lineage.ChainId, out var head) || head.BackupId != lineage.LastBackupId || head.TimelineId != timelineId)
        {
            return BackupKind.Full;
        }

        parent = head;
        return BackupKind.Incremental;
    }
}
