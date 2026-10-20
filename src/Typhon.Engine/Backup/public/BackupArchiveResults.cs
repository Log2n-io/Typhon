using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// What a restore would do, worked out without doing any of it (<see cref="BackupOperations.PlanRestore"/>): the point (or, for an LSN or a time, the
/// base and the archived WAL), the target as the restore resolves it, what would stop it, and the fingerprint
/// <see cref="BackupRestoreOptions.ApprovedPlan"/> binds a restore to.
/// </summary>
/// <param name="Fingerprint">Identifies what would be restored and where; it changes when the chain, the archived WAL used, the cut or the target change.</param>
/// <param name="ResolvedTarget">The bundle the restore writes; null when no target was given or it is refused by name.</param>
/// <param name="DisplacedBundle">Where Replace moves the existing target; null when there is none.</param>
/// <param name="Chain">The points whose packs are read, base first.</param>
/// <param name="BytesToRead">The packs' and archived segments' bytes.</param>
/// <param name="BaseBackupId">The point restored (the base, for an LSN or a time); 0 when unreachable.</param>
/// <param name="CheckpointLsn">The base's <c>C</c>.</param>
/// <param name="PointLsn">The restored database's last LSN: the point's <c>P</c>, or <c>L'</c> for an LSN or a time.</param>
/// <param name="TargetLsn">The LSN asked for, or the one an instant resolved to; 0 for a point.</param>
/// <param name="TimelineId">The timeline restored from.</param>
/// <param name="ArchivedSegments">Archived WAL segments the restore reads.</param>
/// <param name="ArchivedBytes">Their file bytes.</param>
/// <param name="SampleLsn">For an instant, the LSN of the time sample used; 0 otherwise.</param>
/// <param name="SampleUtc">For an instant, the sample's time; null otherwise.</param>
/// <param name="Refusal">Why the restore would be refused; null when it would not.</param>
/// <param name="Unreachable">Why an LSN or a time cannot be reached; null when it can.</param>
/// <param name="RefusalMessage">The refusal's message; null when none.</param>
/// <param name="NearestRestorable">For an unreachable target, the restorable LSNs nearest it.</param>
[PublicAPI]
public sealed record BackupRestorePlan(string Fingerprint, string ResolvedTarget, string DisplacedBundle, IReadOnlyList<uint> Chain, long BytesToRead,
    uint BaseBackupId, long CheckpointLsn, long PointLsn, long TargetLsn, Guid TimelineId, int ArchivedSegments, long ArchivedBytes, long SampleLsn,
    DateTime? SampleUtc, RestoreRefusalReason? Refusal, RestoreUnreachableReason? Unreachable, string RefusalMessage, IReadOnlyList<long> NearestRestorable)
{
    /// <summary>Whether the restore would proceed as planned.</summary>
    public bool CanRestore => Refusal == null && Unreachable == null;
}
