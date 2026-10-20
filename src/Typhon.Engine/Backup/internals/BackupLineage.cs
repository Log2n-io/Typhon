using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// What this session knows about the database's last backup point, and whether its live change map covers every data-file write since that point's
/// freeze. Written by the backup thread (a publish), by the open and by a close that cannot join that thread (a revoke); read by admission and by the
/// close. Every member goes through one lock, so the close reads a consistent snapshot, and a revoke is permanent: a capture that publishes after it
/// (a store that came back after the close gave up on it) cannot make the lineage trusted again.
/// </summary>
internal sealed class BackupLineage
{
    private readonly Lock _lock = new();
    private bool _trusted;
    private bool _revoked;
    private Guid _storeId;
    private Guid _chainId;
    private uint _lastBackupId;
    private long _lastPointLsn;

    /// <summary>
    /// The live change map covers every data-file write since <see cref="LastBackupId"/>'s freeze. Never true again after <see cref="Revoke"/>.
    /// </summary>
    public bool Trusted
    {
        get
        {
            lock (_lock)
            {
                return _trusted;
            }
        }
        set
        {
            lock (_lock)
            {
                _trusted = value && !_revoked;
            }
        }
    }

    /// <summary>The store the last point went to.</summary>
    public Guid StoreId
    {
        get
        {
            lock (_lock)
            {
                return _storeId;
            }
        }
        set
        {
            lock (_lock)
            {
                _storeId = value;
            }
        }
    }

    /// <summary>The last point's chain.</summary>
    public Guid ChainId
    {
        get
        {
            lock (_lock)
            {
                return _chainId;
            }
        }
        set
        {
            lock (_lock)
            {
                _chainId = value;
            }
        }
    }

    /// <summary>The last point's id in its store; 0 for none.</summary>
    public uint LastBackupId
    {
        get
        {
            lock (_lock)
            {
                return _lastBackupId;
            }
        }
        set
        {
            lock (_lock)
            {
                _lastBackupId = value;
            }
        }
    }

    /// <summary>The last point's <c>P</c>.</summary>
    public long LastPointLsn
    {
        get
        {
            lock (_lock)
            {
                return _lastPointLsn;
            }
        }
        set
        {
            lock (_lock)
            {
                _lastPointLsn = value;
            }
        }
    }

    /// <summary>Records a published point: from its freeze on, the live map holds exactly the writes since it. Trusted only if not revoked.</summary>
    public void Published(Guid storeId, in BackupPointInfo point, bool mapEnabled)
    {
        lock (_lock)
        {
            _trusted = mapEnabled && !_revoked;
            _storeId = storeId;
            _chainId = point.ChainId;
            _lastBackupId = point.BackupId;
            _lastPointLsn = point.PointLsn;
        }
    }

    /// <summary>The live map can no longer vouch for anything, for the rest of the session: a capture's page set may be lost to it.</summary>
    public void Revoke()
    {
        lock (_lock)
        {
            _revoked = true;
            _trusted = false;
        }
    }

    /// <summary>Every member, read together.</summary>
    public LineageSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new LineageSnapshot(_trusted, _storeId, _chainId, _lastBackupId, _lastPointLsn);
        }
    }
}

/// <summary>A consistent read of a <see cref="BackupLineage"/>.</summary>
internal readonly record struct LineageSnapshot(bool Trusted, Guid StoreId, Guid ChainId, uint LastBackupId, long LastPointLsn);

/// <summary>Full or incremental (BKP-06).</summary>
internal static class BackupAdmission
{
    /// <summary>
    /// An incremental only when the caller allows one, the live map is trusted, and the store's head of the lineage's chain is the lineage's last point,
    /// on this database's timeline or on the timeline it forked from (a fork keeps the lineage: the map tracked every write, impl/1418 §3.6); a full point
    /// otherwise. Any doubt costs I/O, never correctness.
    /// </summary>
    /// <param name="requested"><see cref="BackupKind.Full"/> forces a full point; <see cref="BackupKind.Incremental"/> allows an incremental.</param>
    /// <param name="lineage">The session's lineage.</param>
    /// <param name="session">The open store session.</param>
    /// <param name="timelineId">The database's current timeline.</param>
    /// <param name="timelineParentId">The timeline it continues; <see cref="Guid.Empty"/> for none.</param>
    /// <param name="parent">Receives the head an incremental builds on.</param>
    public static BackupKind Decide(BackupKind requested, BackupLineage lineage, IBackupStoreSession session, Guid timelineId, Guid timelineParentId,
        out BackupChainHead parent)
    {
        parent = default;
        if (requested == BackupKind.Full || !lineage.Trusted || lineage.StoreId != session.StoreId || lineage.LastBackupId == 0)
        {
            return BackupKind.Full;
        }

        if (!session.TryGetChainHead(lineage.ChainId, out var head) || head.BackupId != lineage.LastBackupId
            || (head.TimelineId != timelineId && (timelineParentId == Guid.Empty || head.TimelineId != timelineParentId)))
        {
            return BackupKind.Full;
        }

        parent = head;
        return BackupKind.Incremental;
    }
}
