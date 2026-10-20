using Typhon.Workbench.Backups;
using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Tests.Backups;

/// <summary>
/// The real operations, with a gate in front of the long ones (#1417 R3): a test holds a job "still running" exactly as long as it needs, then releases
/// it — no timing. <see cref="Entered"/> is set when a gated call reached its gate.
/// </summary>
internal sealed class GatedBackupOperations : IBackupOperations
{
    private readonly BackupOperationsAdapter _real = new();

    public ManualResetEventSlim Gate { get; } = new(initialState: true);

    public ManualResetEventSlim Entered { get; } = new();

    /// <summary>Holds the next gated call until <see cref="Release"/>.</summary>
    public void Close()
    {
        Entered.Reset();
        Gate.Reset();
    }

    public void Release() => Gate.Set();

    /// <summary>Waits at the gate; a cancel that came meanwhile is then seen by the real call, as it would be mid-operation.</summary>
    private void Pass()
    {
        Entered.Set();
        Gate.Wait(TimeSpan.FromSeconds(30));
    }

    public BackupStoreStatus Status(string store) => _real.Status(store);

    public BackupStatusReport ReadStatus(string bundle) => _real.ReadStatus(bundle);

    public BackupStoreListing List(string store, bool computeRestorable) => _real.List(store, computeRestorable);

    public BackupArchiveListing ListArchive(string store) => _real.ListArchive(store);

    public BackupRestorePlan PlanRestore(string store, PointSelector point, string target, BackupRestoreOptions options) =>
        _real.PlanRestore(store, point, target, options);

    public BackupRetentionPlan PlanRetention(string store, BackupRetentionPolicy policy) => _real.PlanRetention(store, policy);

    public BackupTimeResolution ResolveTime(string store, DateTime utc, Guid? timeline) => _real.ResolveTime(store, utc, timeline);

    public BackupArchiveLog ReadLog(string store, Guid timeline, DateTime fromUtc, DateTime toUtc, int max) =>
        _real.ReadLog(store, timeline, fromUtc, toUtc, max);

    public BackupPointSummary CreateOffline(string bundle, string store, BackupCreateOptions options)
    {
        Pass();
        return _real.CreateOffline(bundle, store, options);
    }

    public Task<BackupPointSummary> CreateOnline(DatabaseEngine engine, string store, bool full, CancellationToken ct)
    {
        Pass();
        return _real.CreateOnline(engine, store, full, ct);
    }

    public Guid RequestBackup(string bundle, bool full) => _real.RequestBackup(bundle, full);

    public BackupVerifyReport Verify(string store, PointSelector? point, BackupVerifyLevel level, BackupVerifyOptions options)
    {
        Pass();
        return _real.Verify(store, point, level, options);
    }

    public BackupCompactionResult Compact(string store, PointSelector point, BackupLifecycleOptions options)
    {
        Pass();
        return _real.Compact(store, point, options);
    }

    public BackupRetentionResult ApplyRetention(string store, BackupRetentionPolicy policy, BackupRetentionPlan approved, BackupLifecycleOptions options)
    {
        Pass();
        return _real.ApplyRetention(store, policy, approved, options);
    }

    public BackupRestoreResult Restore(string store, PointSelector point, string target, BackupRestoreOptions options)
    {
        Pass();

        // A cancel that came while the restore was held is observed as one mid-restore is, before the publish. The stager itself observes its token only
        // after each full write batch, so an image smaller than one batch would publish anyway (reported, #1417 As built).
        options.Cancellation.ThrowIfCancellationRequested();
        return _real.Restore(store, point, target, options);
    }

    public BackupSettleResult Settle(string bundle, string[] schemaDllPaths) => _real.Settle(bundle, schemaDllPaths);

    public BackupDrillReportDto Drill(string store, string point, string scratch, string[] schemaDllPaths, Action<string, long, long> progress,
        CancellationToken ct) =>
        _real.Drill(store, point, scratch, schemaDllPaths, progress, ct);
}
