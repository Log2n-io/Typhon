using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>The facade's WAL-archive side (#1418): plan a restore, list the archive, resolve a time, read the log.</summary>
public static partial class BackupOperations
{
    /// <summary>
    /// What <see cref="Restore"/> would do with these arguments, without doing any of it: read-only, no lock taken, nothing written. Every refusal the
    /// restore would raise is reported, never thrown: a target that exists, a live one, a misnamed one, an image of another format revision, a foreign
    /// staging, an LSN or a time that cannot be reached. Pass its <see cref="BackupRestorePlan.Fingerprint"/> as
    /// <see cref="BackupRestoreOptions.ApprovedPlan"/> to make the restore refuse if what it would restore has changed since.
    /// </summary>
    /// <param name="store">The store directory.</param>
    /// <param name="point">A point, or an LSN or a time of an archived timeline.</param>
    /// <param name="to">The bundle path or its parent directory; null to plan the source only.</param>
    /// <param name="options">Replace and format mismatch, as the restore would take them.</param>
    public static BackupRestorePlan PlanRestore(string store, PointSelector point, string to = null, BackupRestoreOptions options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        options ??= new BackupRestoreOptions();
        var preview = RestoreStager.Preview(store, point, to, new RestoreOptions
        {
            Replace = options.Replace,
            AllowFormatMismatch = options.AllowFormatMismatch,
            Logger = options.Logger,
        });
        if (preview.Unreachable is { } u)
        {
            return new BackupRestorePlan(null, null, null, [], 0, 0, 0, 0, point.Lsn, u.TimelineId, 0, 0, 0, null, null, u.Reason, u.Message,
                u.NearestRestorable);
        }

        var chain = new List<uint>(preview.Chain.Depth);
        foreach (var p in preview.Chain.Packs)
        {
            chain.Add(p.BackupId);
        }

        var plan = preview.ArchivePlan;
        var sample = plan?.SampleUsed;
        return new BackupRestorePlan(preview.Fingerprint, preview.ResolvedTarget, preview.DisplacedBundle, chain, preview.BytesToRead,
            preview.Chain.Point.BackupId, preview.Chain.Point.CheckpointLsn, plan?.EffectiveLsn ?? preview.Chain.Point.PointLsn, plan?.TargetLsn ?? 0,
            plan?.TimelineId ?? preview.Chain.Point.TimelineId, plan?.ArchivedSegments ?? 0, plan?.ArchivedBytes ?? 0, sample?.Lsn ?? 0,
            sample is { } s ? new DateTime(s.UtcTicks, DateTimeKind.Utc) : null, preview.Refusal?.Reason, null, preview.Refusal?.Message, []);
    }
}
