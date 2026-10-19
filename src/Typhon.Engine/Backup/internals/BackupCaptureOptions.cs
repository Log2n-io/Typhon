using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Tuning of online backup capture. Internal until the public backup surface (#1414) exposes what operators may set; the defaults are the design's
/// (02 §3-§4).
/// </summary>
internal sealed class BackupCaptureOptions
{
    /// <summary>Copy-before-write slots, 8 KiB each: pre-images writers can park while the backup thread is busy with the store. Default 256 (2 MiB).</summary>
    public int CbwBufferPages
    {
        get => _cbwBufferPages;
        set => _cbwBufferPages = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(CbwBufferPages), value, "at least one slot");
    }

    private int _cbwBufferPages = 256;

    /// <summary>
    /// The most a data-file writer waits in copy-before-write, per write; past it the capture aborts and the write proceeds (BKP-12). Default 50 ms.
    /// </summary>
    public TimeSpan CbwStallTimeout { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the freeze may wait, in all, for the data-file writes in flight before the capture fails (BKP-12). Default 2 s.</summary>
    public TimeSpan FreezeAcquireTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The forced checkpoint before the freeze, best effort: a short tail and few copy-before-write reads, never correctness. Default 30 s.</summary>
    public TimeSpan CheckpointTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the WAL segment switch before the freeze may take; best effort. Default 1 s.</summary>
    public TimeSpan SegmentSwitchTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long the tail copy waits for the WAL to be durable through the freeze's published LSN. Default 10 s.</summary>
    public TimeSpan WalFlushTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Pages the scanner reads in one I/O. Default 32 (256 KiB).</summary>
    public int ScanRunPages
    {
        get => _scanRunPages;
        set => _scanRunPages = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(ScanRunPages), value, "at least one page");
    }

    private int _scanRunPages = 32;

    /// <summary>How long removing the copy-before-write hook may hold writers back, in all (BKP-12). Default 2 s.</summary>
    public TimeSpan UninstallTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Pages the freeze sizes its bitmaps beyond the file's tracked length (which lags an async batch until its fsync); a freeze that finds the file
    /// longer retries with the length it saw plus this margin. Default 8 192 (64 MiB).
    /// </summary>
    public int FreezeGrowthMarginPages { get; set; } = 8192;

    /// <summary>How long a closing engine lets a store call in flight finish, on top of the gate budgets, before it gives up on the backup thread.</summary>
    public TimeSpan StoreStopGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a closing engine waits for the backup thread once it has cancelled it. Every wait a capture makes observes the cancellation but a store
    /// call and the gate's bounded acquires, so this is their budgets plus <see cref="StoreStopGrace"/>.
    /// </summary>
    public TimeSpan StopTimeout => FreezeAcquireTimeout + UninstallTimeout + StoreStopGrace;

    /// <summary>Whether the capture forces a checkpoint before its freeze. Tests turn it off to freeze an engine with dirty pages. Default on.</summary>
    public bool CheckpointBeforeFreeze { get; set; } = true;
}
