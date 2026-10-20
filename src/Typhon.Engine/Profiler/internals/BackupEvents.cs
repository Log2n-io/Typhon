// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>An online backup capture, freeze to publish (#1416, DL-29). Required: the requested kind. Optional: the point's id, pages, copy-before-write copies.</summary>
[TraceEvent(TraceEventKind.BackupCapture, EmitEncoder = true)]
internal ref partial struct BackupCaptureEvent
{
    [BeginParam]
    public byte RequestedKind;

    [Optional(MaskValue = 0x01)]
    private uint _backupId;

    [Optional(MaskValue = 0x02)]
    private int _pages;

    [Optional(MaskValue = 0x04)]
    private int _cbwCopies;
}

/// <summary>The capture's freeze. Optional: the image's pages.</summary>
[TraceEvent(TraceEventKind.BackupFreeze, EmitEncoder = true)]
internal ref partial struct BackupFreezeEvent
{
    [Optional(MaskValue = 0x01)]
    private int _imagePages;
}

/// <summary>The capture's WAL tail copy. Optional: the point's LSN.</summary>
[TraceEvent(TraceEventKind.BackupTail, EmitEncoder = true)]
internal ref partial struct BackupTailEvent
{
    [Optional(MaskValue = 0x01)]
    private long _pointLsn;
}

/// <summary>The capture's scan. Optional: the pages handed to the store.</summary>
[TraceEvent(TraceEventKind.BackupScan, EmitEncoder = true)]
internal ref partial struct BackupScanEvent
{
    [Optional(MaskValue = 0x01)]
    private int _pages;
}

/// <summary>A pack verified after its publish. Required: the point, the level (0 quick, 1 full).</summary>
[TraceEvent(TraceEventKind.BackupVerify, EmitEncoder = true)]
internal ref partial struct BackupVerifyEvent
{
    [BeginParam]
    public uint BackupId;

    [BeginParam]
    public byte Level;
}

/// <summary>A chain lifecycle operation (1 compaction, 2 retention). Optional: packs read or deleted, bytes written or freed.</summary>
[TraceEvent(TraceEventKind.BackupLifecycle, EmitEncoder = true)]
internal ref partial struct BackupLifecycleEvent
{
    [BeginParam]
    public byte Op;

    [Optional(MaskValue = 0x01)]
    private int _packs;

    [Optional(MaskValue = 0x02)]
    private long _bytes;
}
