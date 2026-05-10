using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;

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
    public void Initialize(long lastSegmentId = 0, long firstLSN = 1)
    {
        if (_initialized)
        {
            ThrowHelper.ThrowInvalidOp("WalManager is already initialized.");
        }

        SegmentManager = new WalSegmentManager(_fileIO, _options.WalDirectory, _options.SegmentSize, _options.PreAllocateSegments, _options.UseFUA);
        SegmentManager.Initialize(lastSegmentId, firstLSN);
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

    // ═══════════════════════════════════════════════════════════════
    // FPI Search (on-the-fly repair)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Scans all WAL segments for the most recent Full-Page Image (FPI) matching the given file page index. Returns the page data (8192 bytes) if found,
    /// or null if no FPI exists. Handles both compressed (LZ4) and uncompressed FPI payloads.
    /// Used by <see cref="PagedMMF.TryRepairPageFromFpi"/> for on-the-fly repair during normal operation.
    /// </summary>
    internal byte[] SearchFpiForPage(int filePageIndex)
    {
        if (SegmentManager == null)
        {
            return null;
        }

        var segmentPaths = SegmentManager.GetAllSegmentPaths();
        if (segmentPaths.Count == 0)
        {
            return null;
        }

        byte[] bestPageData = null;
        long bestLSN = -1;

        using var reader = new WalSegmentReader(_fileIO);

        foreach (var path in segmentPaths)
        {
            if (!reader.OpenSegment(path))
            {
                continue;
            }

            while (reader.TryReadNext(out var chunkHeader, out var body))
            {
                if ((WalChunkType)chunkHeader.ChunkType != WalChunkType.FullPageImage)
                {
                    continue;
                }

                // FPI body: [LSN (8B)] [FpiMetadata (16B)] [page data]
                if (body.Length < sizeof(long) + FpiMetadata.SizeInBytes)
                {
                    continue;
                }

                var lsn = MemoryMarshal.Read<long>(body);
                var meta = MemoryMarshal.Read<FpiMetadata>(body.Slice(sizeof(long)));

                if (meta.FilePageIndex != filePageIndex)
                {
                    continue;
                }

                if (lsn <= bestLSN)
                {
                    continue;
                }

                var pagePayload = body.Slice(sizeof(long) + FpiMetadata.SizeInBytes);

                if (meta.CompressionAlgo != FpiCompression.AlgoNone)
                {
                    // Compressed FPI — decompress the page payload
                    if (meta.CompressionAlgo != FpiCompression.AlgoLZ4)
                    {
                        continue; // Unknown algorithm — try older FPI
                    }

                    var decompressed = new byte[meta.UncompressedSize];
                    var decompressedSize = FpiCompression.Decompress(pagePayload, decompressed);
                    if (decompressedSize != meta.UncompressedSize)
                    {
                        continue; // Decompression failure — try older FPI
                    }

                    bestLSN = lsn;
                    bestPageData = decompressed;
                }
                else
                {
                    // Uncompressed FPI — validate and extract
                    if (pagePayload.Length < PagedMMF.PageSize)
                    {
                        continue;
                    }

                    bestLSN = lsn;
                    bestPageData = pagePayload.Slice(0, PagedMMF.PageSize).ToArray();
                }
            }
        }

        return bestPageData;
    }

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
