using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Internals;

/// <summary><see cref="IBackupService"/> over the engine's <see cref="BackupCoordinator"/>, publishing into a <see cref="FileSystemBackupStore"/>.</summary>
internal sealed class BackupService(DatabaseEngine engine) : IBackupService
{
    /// <summary>Test seam: the store's file system. Null in production.</summary>
    internal IStoreFileSystem StoreFileSystem { get; set; }

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
        // The pack's write buffers are the engine's: children of the coordinator, in the resource tree and the unmanaged-memory gauges.
        var writer = new PackWriterOptions { Owner = new BufferOwner(engine.MemoryAllocator, coordinator) };
        var store = new FileSystemBackupStore(storePath, StoreFileSystem, writerOptions: writer);
        var point = await coordinator.CaptureAsync(store, requested, ct).ConfigureAwait(false);

        // From what the store published, not a second read of the store: the point is durable, and nothing after it may report a failure.
        var published = store.LastPublished;
        if (published == null || published.BackupId != point.BackupId)
        {
            throw new InvalidOperationException($"The store did not record publishing point #{point.BackupId}.");
        }

        return BackupOperations.Summarize(published);
    }
}
