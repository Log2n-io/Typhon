using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Backups;

/// <summary>
/// Every engine call the Backups view makes (#1417 §3.1, R3), behind one seam: a signature that moves changes this adapter only, and a test substitutes
/// a gated fake to stage "still running" or "cancelled before publish" without timing.
/// </summary>
public interface IBackupOperations
{
    BackupStoreStatus Status(string store);

    BackupStatusReport ReadStatus(string bundle);

    BackupStoreListing List(string store, bool computeRestorable);

    BackupArchiveListing ListArchive(string store);

    BackupRestorePlan PlanRestore(string store, PointSelector point, string target, BackupRestoreOptions options);

    BackupRetentionPlan PlanRetention(string store, BackupRetentionPolicy policy);

    BackupTimeResolution ResolveTime(string store, DateTime utc, Guid? timeline);

    BackupArchiveLog ReadLog(string store, Guid timeline, DateTime fromUtc, DateTime toUtc, int max);

    BackupPointSummary CreateOffline(string bundle, string store, BackupCreateOptions options);

    Task<BackupPointSummary> CreateOnline(DatabaseEngine engine, string store, bool full, CancellationToken ct);

    Guid RequestBackup(string bundle, bool full);

    BackupVerifyReport Verify(string store, PointSelector? point, BackupVerifyLevel level, BackupVerifyOptions options);

    BackupCompactionResult Compact(string store, PointSelector point, BackupLifecycleOptions options);

    BackupRetentionResult ApplyRetention(string store, BackupRetentionPolicy policy, BackupRetentionPlan approved, BackupLifecycleOptions options);

    BackupRestoreResult Restore(string store, PointSelector point, string target, BackupRestoreOptions options);

    /// <summary>Opens and closes a restored bundle in a child process (<see cref="BackupChildProcess.Settle"/>).</summary>
    BackupSettleReport Settle(string bundle, string[] schemaDllPaths);

    /// <summary>A drill in a child process (<see cref="BackupChildProcess.Drill"/>).</summary>
    BackupDrillReportDto Drill(string store, string point, string scratch, string[] schemaDllPaths, Action<string, long, long> progress,
        CancellationToken ct);
}

/// <summary>The production <see cref="IBackupOperations"/>: the engine's facade, the engine's online service, the child process (drill, settle).</summary>
public sealed class BackupOperationsAdapter : IBackupOperations
{
    public BackupStoreStatus Status(string store) => BackupOperations.Status(store);

    public BackupStatusReport ReadStatus(string bundle) => BackupOperations.ReadStatus(bundle);

    public BackupStoreListing List(string store, bool computeRestorable) => BackupOperations.List(store, computeRestorable);

    public BackupArchiveListing ListArchive(string store) => BackupOperations.ListArchive(store);

    public BackupRestorePlan PlanRestore(string store, PointSelector point, string target, BackupRestoreOptions options) =>
        BackupOperations.PlanRestore(store, point, target, options);

    public BackupRetentionPlan PlanRetention(string store, BackupRetentionPolicy policy) => BackupOperations.PlanRetention(store, policy);

    public BackupTimeResolution ResolveTime(string store, DateTime utc, Guid? timeline) => BackupOperations.ResolveTime(store, utc, timeline);

    public BackupArchiveLog ReadLog(string store, Guid timeline, DateTime fromUtc, DateTime toUtc, int max) =>
        BackupOperations.ReadLog(store, timeline, fromUtc, toUtc, commits: true, max);

    public BackupPointSummary CreateOffline(string bundle, string store, BackupCreateOptions options) => BackupOperations.Create(bundle, store, options);

    public Task<BackupPointSummary> CreateOnline(DatabaseEngine engine, string store, bool full, CancellationToken ct) =>
        engine.Backup.CreateAsync(store, full ? BackupRequestKind.Full : BackupRequestKind.Auto, ct);

    public Guid RequestBackup(string bundle, bool full) =>
        BackupOperations.RequestBackup(bundle, full ? BackupRequestKind.Full : BackupRequestKind.Auto, $"Typhon Workbench (PID {Environment.ProcessId})");

    public BackupVerifyReport Verify(string store, PointSelector? point, BackupVerifyLevel level, BackupVerifyOptions options) =>
        point is { } p ? BackupOperations.Verify(store, p, level, options) : BackupOperations.Verify(store, level, options);

    public BackupCompactionResult Compact(string store, PointSelector point, BackupLifecycleOptions options) =>
        BackupOperations.Compact(store, point, options);

    public BackupRetentionResult ApplyRetention(string store, BackupRetentionPolicy policy, BackupRetentionPlan approved,
        BackupLifecycleOptions options) =>
        BackupOperations.ApplyRetention(store, policy, approved, options);

    public BackupRestoreResult Restore(string store, PointSelector point, string target, BackupRestoreOptions options) =>
        BackupOperations.Restore(store, point, target, options);

    public BackupSettleReport Settle(string bundle, string[] schemaDllPaths) => BackupChildProcess.Settle(bundle, schemaDllPaths);

    public BackupDrillReportDto Drill(string store, string point, string scratch, string[] schemaDllPaths, Action<string, long, long> progress,
        CancellationToken ct) =>
        BackupChildProcess.Drill(store, point, scratch, schemaDllPaths, progress, ct);
}
