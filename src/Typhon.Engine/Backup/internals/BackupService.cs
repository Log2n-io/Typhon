using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Internals;

/// <summary><see cref="IBackupService"/> over the engine's <see cref="BackupCoordinator"/>, publishing into a <see cref="FileSystemBackupStore"/>.</summary>
internal sealed class BackupService(DatabaseEngine engine) : IBackupService
{
    public async Task<BackupPointSummary> CreateAsync(string storePath, BackupRequestKind kind = BackupRequestKind.Auto, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storePath);
        var requested = kind switch
        {
            // The coordinator's admission (BKP-06) turns any request but Full into an incremental only when the chain head allows it.
            BackupRequestKind.Auto => BackupKind.Incremental,
            BackupRequestKind.Full => BackupKind.Full,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Expected Auto or Full."),
        };

        var coordinator = engine.BackupCoordinator
                          ?? throw new BackupAbortedException(BackupAbortReason.EngineStopping, "the engine is not open.");
        var point = await coordinator.CaptureAsync(new FileSystemBackupStore(storePath), requested, ct).ConfigureAwait(false);
        return BackupOperations.Summarize(StoreView.Open(storePath).Find(point.BackupId).Original);
    }
}
