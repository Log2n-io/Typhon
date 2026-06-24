using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Top-level orchestrator for the Write-Ahead Log subsystem. Owns the <see cref="WalCommitBuffer"/>, <see cref="WalWriter"/>, and
/// <see cref="WalSegmentManager"/> as a single cohesive unit.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: <see cref="Initialize"/> creates the WAL directory and opens the first segment, then <see cref="Start"/> launches the writer thread.
/// <see cref="Dispose"/> stops the writer and releases all resources.
/// </para>
/// <para>
/// The manager delegates producer-facing APIs (<see cref="DurableLsn"/>, <see cref="WaitForDurable"/>) to the underlying <see cref="WalWriter"/>.
/// The <see cref="CommitBuffer"/> is exposed for transaction threads to claim and publish WAL records.
/// </para>
/// </remarks>
[PublicAPI]
internal sealed class WalManager : ResourceNode
{
    private readonly WalWriterOptions _options;
    private readonly IMemoryAllocator _allocator;
    private readonly IWalFileIO _fileIO;

    private WalWriter _writer;

    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Creates a new WAL manager. Call <see cref="Initialize"/> then <see cref="Start"/> to activate.
    /// </summary>
    /// <param name="options">Writer and segment configuration.</param>
    /// <param name="allocator">Memory allocator for buffer and staging allocations.</param>
    /// <param name="fileIO">Platform I/O abstraction.</param>
    /// <param name="parent">Parent resource node (typically <c>registry.Durability</c>).</param>
    /// <param name="commitBufferCapacity">Capacity of each commit buffer half in bytes. Default: 2 MB.</param>
    public WalManager(WalWriterOptions options, IMemoryAllocator allocator, IWalFileIO fileIO, IResource parent, int commitBufferCapacity = 2 * 1024 * 1024)
        : base("WalManager", ResourceType.WAL, parent)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(fileIO);

        _options = options;
        _allocator = allocator;
        _fileIO = fileIO;

        CommitBuffer = new WalCommitBuffer(allocator, this, commitBufferCapacity);
    }

    // ═══════════════════════════════════════════════════════════════
    // Public properties
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The commit buffer for producer threads to claim and publish WAL records.</summary>
    public WalCommitBuffer CommitBuffer { get; private set; }

    /// <summary>The segment manager for WAL file lifecycle operations. Used by <see cref="CheckpointManager"/> for segment reclamation.</summary>
    internal WalSegmentManager SegmentManager { get; private set; }

    /// <summary>The highest LSN durably written to stable media.</summary>
    public long DurableLsn => _writer?.DurableLsn ?? 0;

    /// <summary>Highest LSN claimed so far (mirrors <see cref="DurabilityLog.LastAppendedLsn"/>). Used by the checkpoint
    /// barrier (CK-02) to flush the WAL through everything appended before capturing dirty pages.</summary>
    public long LastAppendedLsn => (CommitBuffer?.NextLsn ?? 1) - 1;

    /// <summary>Whether the WAL writer thread is running.</summary>
    public bool IsRunning => _writer?.IsRunning ?? false;

    /// <summary>Whether a fatal I/O error has occurred.</summary>
    public bool HasFatalError => _writer?.HasFatalError ?? false;

    /// <summary>Optional logger, propagated to the WAL writer thread for diagnostics.</summary>
    internal ILogger Logger
    {
        set => _writer?.Logger = value;
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Initializes the WAL subsystem: creates directories, opens the first segment, and prepares the writer. Must be called before <see cref="Start"/>.
    /// </summary>
    /// <param name="lastSegmentId">Last known segment ID for continuity (0 for fresh start).</param>
    /// <param name="firstLSN">First LSN for the initial segment.</param>
    /// <param name="checkpointLsn">Checkpoint frontier passed to the segment manager's reopen reconcile — segments with
    /// records below it are reclaimed, segments with records ≥ it are retained for recovery. See WR-01.</param>
    public void Initialize(long lastSegmentId = 0, long firstLSN = 1, long checkpointLsn = 0)
    {
        if (_initialized)
        {
            ThrowHelper.ThrowInvalidOp("WalManager is already initialized.");
        }

        SegmentManager = new WalSegmentManager(_fileIO, _options.WalDirectory, _options.SegmentSize, _options.PreAllocateSegments, _options.UseFUA);
        SegmentManager.Initialize(lastSegmentId, firstLSN, checkpointLsn);
        // Continue the LSN sequence from the active segment's first-record LSN. The commit buffer's counter is constructed at 1; on a reopen (firstLSN > 1) it must be
        // advanced to match, or record LSNs restart at 1 below the segment header AND below a prior session's CheckpointLSN — the latter makes recovery drop the whole
        // post-reopen window (LOG-08). Monotonic + pre-Start, so a fresh writer (firstLSN == 1) is a no-op.
        CommitBuffer.SeedNextLsn(firstLSN);
        _writer = new WalWriter(CommitBuffer, SegmentManager, _fileIO, _options, _allocator, this);
        _initialized = true;
    }

    /// <summary>
    /// Starts the WAL writer thread. <see cref="Initialize"/> must be called first.
    /// </summary>
    public void Start()
    {
        if (!_initialized)
        {
            ThrowHelper.ThrowInvalidOp("WalManager must be initialized before starting.");
        }

        _writer.Start();
    }

    /// <summary>
    /// Blocks the caller until the specified LSN has been durably written.
    /// Delegates to <see cref="WalWriter.WaitForDurable"/>.
    /// </summary>
    public void WaitForDurable(long lsn, ref WaitContext ctx) => _writer.WaitForDurable(lsn, ref ctx);

    /// <summary>
    /// Requests an explicit flush of buffered WAL data.
    /// Used by <see cref="DurabilityMode.Deferred"/> callers.
    /// </summary>
    public void RequestFlush() => _writer?.RequestFlush();

    /// <summary>Seeds the durable watermark to a frontier already durable on disk (crash-recovery seal — see <see cref="WalWriter.SeedDurableLsn"/>).</summary>
    public void SeedDurableLsn(long lsn) => _writer?.SeedDurableLsn(lsn);

    // ═══════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _writer?.Dispose();
            _writer = null;

            SegmentManager?.Dispose();
            SegmentManager = null;

            CommitBuffer?.Dispose();
            CommitBuffer = null;
        }

        base.Dispose(disposing);
        _disposed = true;
    }
}
