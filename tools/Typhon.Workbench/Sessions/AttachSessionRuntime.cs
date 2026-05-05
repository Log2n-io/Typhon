using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Live-attach session runtime. Owns the TCP connection to a running Typhon engine's profiler exporter, drives an
/// <see cref="IncrementalCacheBuilder"/> from incoming Block frames, and fans deltas out to SSE subscribers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architectural note (#289).</b> Phase 3 of the live-replay unification: this runtime no longer decodes records into
/// <c>LiveTickBatch</c> structures. The engine's wire bytes are decompressed and fed straight into the same
/// <see cref="IncrementalCacheBuilder"/> the replay pipeline uses, producing a chunk manifest and tick summaries identical
/// in shape to a sealed <c>.typhon-trace-cache</c> file. Chunk bytes go to a <see cref="LiveCacheTempFile"/> and become
/// addressable via <see cref="ReadChunkCompressedAsync"/>; ticks and chunks fan out as growth deltas over SSE.
/// </para>
/// <para>
/// <b>Connect semantics.</b> <see cref="StartAsync"/> attempts the first TCP connect with 3 × 2 s upfront retry. On total
/// failure it throws <see cref="WorkbenchException"/> with HTTP 503. Once the first connect succeeds the runtime lives, and
/// the background read loop silently reconnects on later <see cref="SocketException"/> / <see cref="IOException"/>.
/// </para>
/// <para>
/// <b>Reconnect Init compatibility.</b> If the engine reconnects with a fresh Init frame whose system / archetype /
/// component-type signature differs from the original, we mark the session unrecoverable and emit a <c>shutdown</c> SSE
/// frame. The client then reconnects to a fresh sessionId.
/// </para>
/// <para>
/// <b>Disposal.</b> Cancels the read loop, closes the socket, disposes the builder + temp file, completes every subscriber
/// channel. Safe to call multiple times.
/// </para>
/// </remarks>
public sealed partial class AttachSessionRuntime : IDisposable, IChunkProvider
{
    private const int DefaultPort = 9100;
    private const int ConnectRetryCount = 3;
    private const int ConnectRetryDelayMs = 2000;
    private const int ReconnectDelayMs = 2000;
    private const int MaxFrameBytes = 8 * 1024 * 1024;

    /// <summary>Force-flush the in-progress chunk every N ms so partial chunks become visible to clients.</summary>
    private const int FlushChunkTimerMs = 200;

    /// <summary>If no records arrived in this window AND a tick is open, finalize the trailing tick using event timestamps.</summary>
    private const int TrailingTickTimerMs = 250;

    /// <summary>Coalesce GlobalMetricsUpdated SSE deltas to at most one per N ms.</summary>
    private const int GlobalMetricsTimerMs = 1000;

    /// <summary>Per-subscriber bounded delta channel: SSE clients buffer up to this many deltas before being kicked.</summary>
    private const int SubscriberBufferSize = 1000;

    /// <summary>Max time to wait when the buffer is full before disconnecting a slow subscriber.</summary>
    private const int SlowSubscriberTimeoutMs = 1000;

    private readonly Guid _sessionId;
    private readonly string _endpointAddress;
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<ProfilerMetadataDto> _metadataTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, Channel<LiveStreamEventDto>> _subscribers = new();
    private readonly object _builderLock = new();

    /// <summary>Header DTO captured at first Init — used to detect cross-reconnect signature mismatches.</summary>
    private ProfilerHeaderDto _initialHeader;
    private SystemDefinitionDto[] _initialSystems = [];
    private ArchetypeDto[] _initialArchetypes = [];
    private ComponentTypeDto[] _initialComponentTypes = [];
    private string _initialSignature;

    /// <summary>
    /// Verbatim bytes of the first Init frame's payload (TraceFileHeader + system / archetype / component-type tables in
    /// <c>TraceFileWriter</c> wire format). Captured on first Init so the live save-as-replay flow can write them verbatim into a
    /// <see cref="CacheSectionId.SourceMetadata"/> section, producing a self-contained <c>.typhon-replay</c> file that opens with no
    /// source <c>.typhon-trace</c>.
    /// </summary>
    private byte[] _initialMetadataBytes;

    private LiveCacheTempFile _tempFile;
    private IncrementalCacheBuilder _builder;
    private long _timestampFrequency;
    private byte[] _rawBlockBuffer = [];
    private long _lastBlockReceivedTicks;

    /// <summary>
    /// Slot → ThreadInfo (name + managed thread id). Populated by walking each Block's records for
    /// <see cref="TraceEventKind.ThreadInfo"/>. Surfaced on the metadata snapshot and via <c>threadInfoAdded</c>
    /// SSE deltas so the client knows slot names without having to fetch any chunk.
    /// </summary>
    private readonly Dictionary<byte, ThreadInfoDto> _threadInfos = new();

    /// <summary>Mutable list views — owning lock is <see cref="_builderLock"/>. Snapshots fan out via <see cref="Metadata"/>.</summary>
    private readonly List<TickSummaryDto> _tickSummaries = new(capacity: 4096);
    private readonly List<ChunkManifestEntryDto> _chunkManifest = new(capacity: 256);
    private GlobalMetricsDto _globalMetrics = ZeroMetrics;

    /// <summary>Cached metadata snapshot — invalidated on every state change, lazily rebuilt on access.</summary>
    private volatile ProfilerMetadataDto _metadataSnapshot;

    /// <summary>
    /// Compile-time source-location manifest received from the engine in the init handshake (#302, Phase 4).
    /// Parsed once per Init and made available via <see cref="SourceLocationManifest"/>. Empty when the
    /// engine ran without an intercepted call site (e.g. test-only configurations).
    /// </summary>
    private SourceLocationManifestDto _sourceLocationManifest = SourceLocationManifestDto.Empty;

    private Timer _flushChunkTimer;
    private Timer _trailingTickTimer;
    private Timer _globalMetricsTimer;

    private volatile string _connectionStatus = "connecting";
    private volatile bool _unrecoverable;
    private bool _disposed;

    private static readonly GlobalMetricsDto ZeroMetrics = new(0, 0, 0, 0, 0, 0, 0, []);

    /// <summary>Engine endpoint as provided by the client (e.g. <c>"localhost:9100"</c>).</summary>
    public string EndpointAddress => _endpointAddress;

    /// <summary>Metadata snapshot. <c>null</c> until the first Init arrives. Grows over the session's lifetime.</summary>
    public ProfilerMetadataDto Metadata
    {
        get
        {
            var snap = _metadataSnapshot;
            if (snap != null)
            {
                return snap;
            }
            return BuildSnapshotLocked();
        }
    }

    /// <summary>Resolves with the metadata DTO once the first Init frame arrives. Faults if the runtime is disposed before that.</summary>
    public Task<ProfilerMetadataDto> MetadataReady => _metadataTcs.Task;

    /// <summary>
    /// Compile-time source-location manifest from the engine's init handshake (#302).
    /// Returns an empty manifest until the FileTable + SourceLocationManifest frames have arrived.
    /// </summary>
    public SourceLocationManifestDto SourceLocationManifest => _sourceLocationManifest;

    /// <summary>Number of finalized ticks currently in the metadata snapshot.</summary>
    public long TickCount
    {
        get
        {
            lock (_builderLock)
            {
                return _tickSummaries.Count;
            }
        }
    }

    /// <summary>Current connection status — <c>"connected"</c>, <c>"reconnecting"</c>, or <c>"disconnected"</c>.</summary>
    public string ConnectionStatus => _connectionStatus;

    /// <summary>True while the TCP socket is currently held open.</summary>
    public bool IsConnected => _connectionStatus == "connected";

    /// <summary>Set when an Init mismatch on reconnect made the session unrecoverable.</summary>
    public bool IsUnrecoverable => _unrecoverable;

    /// <inheritdoc />
    public bool IsReady => _builder != null;

    /// <inheritdoc />
    public long TimestampFrequency => _timestampFrequency;

    /// <summary>Fires once per tick the builder finalizes — SSE handlers forward as <c>tickSummaryAdded</c> deltas.</summary>
    public event Action<TickSummaryDto> TickSummaryAdded;

    /// <summary>Fires once per chunk the builder flushes — SSE handlers forward as <c>chunkAdded</c> deltas.</summary>
    public event Action<ChunkManifestEntryDto> ChunkAdded;

    /// <summary>Fires ~1 Hz with the latest global metrics — SSE handlers forward as <c>globalMetricsUpdated</c>.</summary>
    public event Action<GlobalMetricsDto> GlobalMetricsUpdated;

    /// <summary>Fires once per discovered (slot, name) — SSE handlers forward as <c>threadInfoAdded</c>.</summary>
    public event Action<ThreadInfoDto> ThreadInfoAdded;

    /// <summary>Fires once per Init frame (so once on first connect, once per reconnect with fresh metadata).</summary>
    public event Action<ProfilerMetadataDto> MetadataReceived;

    /// <summary>Fires when the connection state changes. SSE handlers emit heartbeat frames when this fires.</summary>
    public event Action<string> ConnectionStateChanged;

    /// <summary>Fires when the engine sends a Shutdown frame — terminal.</summary>
    public event Action<string> ShutdownReceived;

    private AttachSessionRuntime(Guid sessionId, string endpointAddress, string host, int port, ILogger logger)
    {
        _sessionId = sessionId;
        _endpointAddress = endpointAddress;
        _host = host;
        _port = port;
        _logger = logger;
    }

    /// <summary>
    /// Starts a new attach-session runtime. Performs 3 × 2 s upfront TCP connect retry before returning.
    /// </summary>
    public static async Task<AttachSessionRuntime> StartAsync(string endpointAddress, ILogger logger, CancellationToken ct)
        => await StartAsync(Guid.NewGuid(), endpointAddress, logger, ct).ConfigureAwait(false);

    /// <summary>
    /// Overload that accepts an explicit session id — used by <c>SessionsController</c> so the temp file path matches the
    /// public <c>sessionId</c> from <see cref="SessionManager"/>.
    /// </summary>
    public static async Task<AttachSessionRuntime> StartAsync(Guid sessionId, string endpointAddress, ILogger logger, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointAddress);
        var (host, port) = ParseEndpoint(endpointAddress);
        var ipv4 = await ResolveIPv4Async(host, ct);

        TcpClient tcp = null;
        Exception lastError = null;
        for (var attempt = 0; attempt < ConnectRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(ipv4, port, ct);
                tcp.NoDelay = true;
                break;
            }
            catch (SocketException ex)
            {
                lastError = ex;
                try { tcp.Dispose(); } catch { }
                tcp = null;
                if (attempt < ConnectRetryCount - 1)
                {
                    await Task.Delay(ConnectRetryDelayMs, ct);
                }
            }
            catch (OperationCanceledException)
            {
                try { tcp.Dispose(); } catch { }
                throw;
            }
        }

        if (tcp == null)
        {
            throw new WorkbenchException(
                StatusCodes.Status503ServiceUnavailable,
                "attach_connect_failed",
                $"Failed to connect to {host}:{port} after {ConnectRetryCount} attempts. {lastError?.Message}",
                lastError);
        }

        var runtime = new AttachSessionRuntime(sessionId, endpointAddress, host, port, logger);
        runtime.LogStarting(host, port);
        runtime.SetConnectionStatus("connected");
        runtime.LogConnected(host, port);

        // Periodic timers — fire only after StartAsync returns to avoid racing with construction.
        runtime._flushChunkTimer = new Timer(runtime.OnFlushChunkTimer, null, FlushChunkTimerMs, FlushChunkTimerMs);
        runtime._trailingTickTimer = new Timer(runtime.OnTrailingTickTimer, null, TrailingTickTimerMs, TrailingTickTimerMs);
        runtime._globalMetricsTimer = new Timer(runtime.OnGlobalMetricsTimer, null, GlobalMetricsTimerMs, GlobalMetricsTimerMs);

        _ = Task.Run(() => runtime.ReadLoopAsync(tcp))
            .ContinueWith(
                t => runtime.LogReadLoopFaulted(t.Exception!),
                default,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        return runtime;
    }

    /// <summary>Creates a subscriber channel and returns its reader. Use <see cref="Unsubscribe"/> on teardown.</summary>
    public (Guid Id, ChannelReader<LiveStreamEventDto> Reader) Subscribe()
    {
        // Wait mode + 1000-delta buffer per subscriber. If a slow subscriber fills the buffer, the writer (BroadcastDelta)
        // waits up to SlowSubscriberTimeoutMs and then drops the channel; the subscriber then reconnects fresh.
        var channel = Channel.CreateBounded<LiveStreamEventDto>(
            new BoundedChannelOptions(SubscriberBufferSize) { FullMode = BoundedChannelFullMode.Wait });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        LogSubscriberConnected(id, _subscribers.Count);
        return (id, channel.Reader);
    }

    /// <summary>
    /// User-initiated disconnect. Cancels the read loop and any in-flight reconnect attempts so the runtime settles on
    /// <c>"disconnected"</c>. Does NOT dispose the runtime — the metadata snapshot stays accessible.
    /// </summary>
    public void RequestDisconnect()
    {
        if (_disposed) return;
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Snapshot of the (slot → ThreadInfo) mapping accumulated so far. SSE handlers replay this on connect so a
    /// late-attaching subscriber sees thread names without having to wait for the engine to re-emit them.
    /// </summary>
    public IReadOnlyList<ThreadInfoDto> GetThreadInfosSnapshot()
    {
        lock (_threadInfos)
        {
            var arr = new ThreadInfoDto[_threadInfos.Count];
            var i = 0;
            foreach (var kv in _threadInfos)
            {
                arr[i++] = kv.Value;
            }
            return arr;
        }
    }

    /// <summary>Removes a subscriber channel and completes it. No-op if the subscriber is already gone.</summary>
    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var ch))
        {
            ch.Writer.TryComplete();
            LogSubscriberDisconnected(id, _subscribers.Count);
        }
    }

    /// <inheritdoc />
    public ValueTask<ChunkManifestEntry> GetChunkManifestEntryAsync(int chunkIdx)
    {
        ThrowIfDisposed();
        if (_builder == null)
        {
            throw new InvalidOperationException("Runtime not ready — Init has not been received yet.");
        }
        lock (_builderLock)
        {
            if ((uint)chunkIdx >= (uint)_builder.ChunkManifest.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkIdx),
                    $"Chunk index {chunkIdx} out of range (manifest has {_builder.ChunkManifest.Count} entries).");
            }
            return ValueTask.FromResult(_builder.ChunkManifest[chunkIdx]);
        }
    }

    /// <inheritdoc />
    public ValueTask<(byte[] Bytes, int Length)> ReadChunkCompressedAsync(int chunkIdx)
    {
        ThrowIfDisposed();
        if (_builder == null || _tempFile == null)
        {
            throw new InvalidOperationException("Runtime not ready — Init has not been received yet.");
        }
        ChunkManifestEntry entry;
        lock (_builderLock)
        {
            if ((uint)chunkIdx >= (uint)_builder.ChunkManifest.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkIdx));
            }
            entry = _builder.ChunkManifest[chunkIdx];
        }
        var bytes = ArrayPool<byte>.Shared.Rent((int)entry.CacheByteLength);
        // Open a fresh reader stream — multiple chunk requests can run concurrently with the writer (FileShare.ReadWrite).
        using (var reader = _tempFile.OpenReader())
        {
            reader.Position = entry.CacheByteOffset;
            reader.ReadExactly(bytes.AsSpan(0, (int)entry.CacheByteLength));
        }
        return ValueTask.FromResult((bytes, (int)entry.CacheByteLength));
    }

    /// <summary>
    /// Snapshot the current live session into a self-contained <c>.typhon-replay</c> file at <paramref name="outputPath"/>. Returns the
    /// number of bytes written. The file embeds the source metadata tables (header + systems + archetypes + component types) so it opens
    /// with no companion <c>.typhon-trace</c> required.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The save runs under <see cref="_builderLock"/> for the duration of the chunk re-feed + trailer write. This serializes the save with
    /// live record processing — record decoding pauses for the duration. Acceptable for an interactive user-initiated save against a
    /// typically-modest live session (a few MB to ~100 MB of cache, sub-second to a few seconds of disk I/O). For very large sessions a
    /// future optimization is a snapshot-then-write pattern (capture the manifest under lock, stream chunks lock-free, brief lock for the
    /// trailer write); not built today since save events are rare and the back-pressure cost is small.
    /// </para>
    /// <para>
    /// The <see cref="ChunkManifestEntry.CacheByteOffset"/> values stored in the live temp file are absolute offsets within that file.
    /// When re-feeding into the new <see cref="FileCacheSink"/>, the sink computes fresh offsets within its own FoldedChunkData section.
    /// We deliberately don't go through <see cref="IncrementalCacheBuilder.WriteTrailerTo"/> here — the builder's live manifest still
    /// references temp-file offsets and must remain unchanged so live chunk-fetches keep working. Instead we assemble a fresh relocated
    /// manifest as chunks are appended and call <see cref="FileCacheSink.WriteTrailer"/> directly.
    /// </para>
    /// </remarks>
    public async Task<long> SaveSessionAsync(string outputPath, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (_builder == null || _tempFile == null || _initialMetadataBytes == null)
        {
            throw new InvalidOperationException("Save not available — Init has not been received yet.");
        }

        // Resolve + validate target dir up front so we fail fast before holding any lock.
        var fullPath = Path.GetFullPath(outputPath);
        var parentDir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
        {
            throw new DirectoryNotFoundException($"Parent directory does not exist: {parentDir}");
        }

        return await Task.Run(() => SaveSessionCore(fullPath, ct), ct).ConfigureAwait(false);
    }

    private long SaveSessionCore(string outputPath, CancellationToken ct)
    {
        using var sink = FileCacheSink.Create(outputPath);

        using var reader = _tempFile.OpenReader();
        lock (_builderLock)
        {
            ct.ThrowIfCancellationRequested();
            _builder.FinalizePendingState();

            // Build the relocated manifest as we re-feed chunks. The new sink's offsets differ from the temp file's; we deliberately
            // do NOT mutate the builder's live manifest (live chunk-fetches must keep working). We construct a fresh array here and
            // pass it to FileCacheSink.WriteTrailer below.
            var liveManifest = _builder.ChunkManifest;
            var relocatedManifest = new ChunkManifestEntry[liveManifest.Count];

            // Size scratch buffers to the actual largest chunk in the manifest. Pre-tick events (engine setup — ThreadInfo,
            // ~200K EcsSpawn for AntHill, etc.) buffer up to PreTickBufferCap = 16 MB and get flushed into chunk[0] at the first
            // TickStart, so that chunk can be substantially larger than IntraTickByteCap. Renting at IntraTickByteCap (2 MB) blew
            // up with ArgumentOutOfRangeException on AsSpan when chunk[0] exceeded the rented array. Walking the manifest first
            // costs ~1 µs and lets the rent always fit.
            var maxCompLen = 1;
            var maxUncompLen = 1;
            for (var i = 0; i < liveManifest.Count; i++)
            {
                var e = liveManifest[i];
                if (e.CacheByteLength > maxCompLen) maxCompLen = (int)e.CacheByteLength;
                if (e.UncompressedBytes > maxUncompLen) maxUncompLen = (int)e.UncompressedBytes;
            }

            var compressedScratch = ArrayPool<byte>.Shared.Rent(maxCompLen);
            var uncompressedScratch = ArrayPool<byte>.Shared.Rent(maxUncompLen);
            try
            {
                for (var i = 0; i < liveManifest.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = liveManifest[i];

                    var compLen = (int)entry.CacheByteLength;
                    var uncompLen = (int)entry.UncompressedBytes;

                    reader.Position = entry.CacheByteOffset;
                    reader.ReadExactly(compressedScratch.AsSpan(0, compLen));
                    var decoded = K4os.Compression.LZ4.LZ4Codec.Decode(
                        compressedScratch.AsSpan(0, compLen),
                        uncompressedScratch.AsSpan(0, uncompLen));
                    if (decoded != uncompLen)
                    {
                        throw new InvalidDataException(
                            $"LZ4 decode size mismatch for chunk [{entry.FromTick}, {entry.ToTick}): expected {uncompLen}, got {decoded}.");
                    }

                    var (newOffset, newCompLen, newUncompLen) = sink.AppendChunk(uncompressedScratch.AsSpan(0, uncompLen));
                    relocatedManifest[i] = new ChunkManifestEntry
                    {
                        FromTick = entry.FromTick,
                        ToTick = entry.ToTick,
                        CacheByteOffset = newOffset,
                        CacheByteLength = newCompLen,
                        EventCount = entry.EventCount,
                        UncompressedBytes = newUncompLen,
                        Flags = entry.Flags,
                    };
                }

                // Snapshot trailer inputs from the builder. Held under lock so the snapshot is internally consistent.
                var tickSummaries = new TickSummary[_builder.TickSummaries.Count];
                for (var i = 0; i < tickSummaries.Length; i++) tickSummaries[i] = _builder.TickSummaries[i];

                var systemAggregates = _builder.GetSystemAggregatesSnapshot();
                var globalMetrics = _builder.CurrentGlobalMetrics;

                // Span names: copy under lock — the builder's dictionary may be mutated by future Block decodes.
                var spanNames = new Dictionary<int, string>(_builder.SpanNames);

                // Build the cache header. Identifier slot carries the sessionId (32 bytes after zero-padding the 16-byte GUID), plainly
                // documented in CacheHeader.SourceFingerprint as "arbitrary identifier when IsSelfContained". The flag tells the loader
                // to project metadata from the SourceMetadata section instead of opening a parent .typhon-trace.
                var cacheHeader = new CacheHeader
                {
                    Flags = CacheHeaderFlags.IsSelfContained,
                    SourceVersion = (ushort)_initialHeader.Version,
                    ChunkerVersion = TraceFileCacheConstants.CurrentChunkerVersion,
                    CreatedUtcTicks = DateTime.UtcNow.Ticks,
                };
                Span<byte> identifier = stackalloc byte[32];
                _sessionId.TryWriteBytes(identifier);
                CacheHeader.SetIdentifier(ref cacheHeader, identifier);

                sink.WriteTrailer(
                    tickSummaries,
                    globalMetrics,
                    systemAggregates,
                    relocatedManifest,
                    spanNames,
                    _initialMetadataBytes,
                    cacheHeader);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressedScratch);
                ArrayPool<byte>.Shared.Return(uncompressedScratch);
            }
        }

        return new FileInfo(outputPath).Length;
    }

    private async Task ReadLoopAsync(TcpClient initialSocket)
    {
        var ct = _cts.Token;
        var socket = initialSocket;
        var streamEnd = StreamEndReason.ConnectionLost;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using (socket)
                {
                    streamEnd = await ProcessStreamAsync(socket.GetStream(), ct);
                }
            }
            catch (SocketException) when (!ct.IsCancellationRequested)
            {
                streamEnd = StreamEndReason.ConnectionLost;
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                streamEnd = StreamEndReason.ConnectionLost;
                LogConnectionLost();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                streamEnd = StreamEndReason.ConnectionLost;
                LogUnexpectedError(ex);
            }

            socket = null;
            if (ct.IsCancellationRequested) break;

            if (streamEnd == StreamEndReason.Shutdown || streamEnd == StreamEndReason.Unrecoverable)
            {
                break;
            }

            SetConnectionStatus("reconnecting");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReconnectDelayMs, ct);
                    var ipv4 = await ResolveIPv4Async(_host, ct);
                    var next = new TcpClient();
                    await next.ConnectAsync(ipv4, _port, ct);
                    next.NoDelay = true;
                    socket = next;
                    SetConnectionStatus("connected");
                    LogConnected(_host, _port);
                    break;
                }
                catch (SocketException) { }
                catch (IOException) { }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }

        SetConnectionStatus("disconnected");
    }

    private enum StreamEndReason
    {
        ConnectionLost,
        Shutdown,
        Unrecoverable,
    }

    private async Task<StreamEndReason> ProcessStreamAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBuf = new byte[LiveStreamProtocol.FrameHeaderSize];

        while (!ct.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(headerBuf, ct);
            var (type, length) = LiveStreamProtocol.ReadFrameHeader(headerBuf);

            if (length < 0 || length > MaxFrameBytes)
            {
                LogMalformedFrame(type.ToString(), $"invalid length {length}");
                return StreamEndReason.ConnectionLost;
            }

            var payload = length > 0 ? ArrayPool<byte>.Shared.Rent(length) : [];
            try
            {
                if (length > 0)
                {
                    await stream.ReadExactlyAsync(payload.AsMemory(0, length), ct);
                }

                switch (type)
                {
                    case LiveFrameType.Init:
                        if (!HandleInit(payload, length))
                        {
                            return StreamEndReason.Unrecoverable;
                        }
                        break;

                    case LiveFrameType.Block:
                        HandleBlock(payload, length);
                        break;

                    case LiveFrameType.FileTable:
                        // #302 Phase 4: parse the engine's compile-time FileTable into the pending manifest.
                        // Combined with the SourceLocationManifest frame that follows (or has already
                        // arrived), it lets the client resolve span siteIds.
                        HandleFileTable(payload, length);
                        break;

                    case LiveFrameType.SourceLocationManifest:
                        HandleSourceLocationManifest(payload, length);
                        break;

                    case LiveFrameType.Shutdown:
                        LogShutdownReceived();
                        ShutdownReceived?.Invoke("engine_shutdown");
                        BroadcastDelta(new LiveStreamEventDto(Kind: "shutdown", Status: "engine_shutdown"));
                        return StreamEndReason.Shutdown;

                    default:
                        LogUnknownFrame((byte)type);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogMalformedFrame(type.ToString(), ex.Message);
            }
            finally
            {
                if (length > 0)
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }

        return StreamEndReason.ConnectionLost;
    }

    private bool HandleInit(byte[] payload, int length)
    {
        // Parse exactly as before: the Init payload is the first four sections of a .typhon-trace file (header + systems +
        // archetypes + componentTypes). Wrap in a non-owning MemoryStream slice so TraceFileReader can drive its parser.
        using var ms = new MemoryStream(payload, index: 0, count: length, writable: false);
        using var reader = new TraceFileReader(ms);
        reader.ReadHeader();
        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();

        var headerDto = ProjectHeader(reader);
        var systems = ProjectSystems(reader);
        var archetypes = ProjectArchetypes(reader);
        var componentTypes = ProjectComponentTypes(reader);

        var newSignature = ComputeInitSignature(headerDto, systems, archetypes, componentTypes);

        if (_builder == null)
        {
            // First connect — initialize everything.
            _initialHeader = headerDto;
            _initialSystems = systems;
            _initialArchetypes = archetypes;
            _initialComponentTypes = componentTypes;
            _initialSignature = newSignature;
            _timestampFrequency = headerDto.TimestampFrequency;
            // Capture the raw Init payload bytes — needed verbatim for the SourceMetadata section if the user later saves the live session
            // as a self-contained .typhon-replay. The byte format here matches what TraceFileWriter emits for header + tables.
            _initialMetadataBytes = new byte[length];
            Array.Copy(payload, 0, _initialMetadataBytes, 0, length);

            _tempFile = LiveCacheTempFile.Create(_sessionId);
            var profilerHeader = new ProfilerHeader { Version = (ushort)headerDto.Version, TimestampFrequency = headerDto.TimestampFrequency };
            // Use the sessionId as the fingerprint (no source file to hash). The 32-byte fingerprint slot in the cache header isn't
            // surfaced to the client for live sessions; we just need a valid 32-byte value.
            Span<byte> fingerprint = stackalloc byte[32];
            _sessionId.TryWriteBytes(fingerprint);
            _builder = new IncrementalCacheBuilder(_tempFile.Sink, ownsSink: false, profilerHeader, fingerprint, new Dictionary<int, string>());
            _builder.TickFinalized += OnBuilderTickFinalized;
            _builder.ChunkFlushed += OnBuilderChunkFlushed;

            var snapshot = BuildSnapshotLocked();
            _metadataTcs.TrySetResult(snapshot);
            MetadataReceived?.Invoke(snapshot);
            BroadcastDelta(new LiveStreamEventDto(Kind: "metadata", Metadata: snapshot));
            LogInitReceived(reader.Systems.Count, reader.Header.WorkerCount, reader.Header.BaseTickRate);
            return true;
        }

        // Reconnect — match signatures.
        if (newSignature != _initialSignature)
        {
            _unrecoverable = true;
            LogInitMismatchUnrecoverable();
            ShutdownReceived?.Invoke("init_mismatch");
            BroadcastDelta(new LiveStreamEventDto(Kind: "shutdown", Status: "init_mismatch"));
            return false;
        }
        // Same signature → continue feeding from where we left off. The builder's internal tick counter survives the
        // reconnect, so subsequent TickStart records resume the same tickNumber sequence.
        LogInitReceived(reader.Systems.Count, reader.Header.WorkerCount, reader.Header.BaseTickRate);
        return true;
    }

    /// <summary>
    /// Parse the engine's FileTable frame payload (#302, Phase 4). Wire layout matches the file-format
    /// trailer's FileTable section: u32 entryCount, then per-entry u16 fileId, u16 pathLen, UTF-8 bytes.
    /// Updates the pending manifest's <c>Files</c>; the partner SourceLocationManifest frame fills entries.
    /// </summary>
    private void HandleFileTable(byte[] payload, int length)
    {
        try
        {
            using var ms = new MemoryStream(payload, 0, length, writable: false);
            using var br = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            var count = br.ReadUInt32();
            var files = new SourceLocationFileDto[count];
            for (uint i = 0; i < count; i++)
            {
                var fileId = br.ReadUInt16();
                var pathLen = br.ReadUInt16();
                var pathBytes = br.ReadBytes(pathLen);
                files[i] = new SourceLocationFileDto(fileId, System.Text.Encoding.UTF8.GetString(pathBytes));
            }
            _sourceLocationManifest = _sourceLocationManifest with { Files = files };
        }
        catch (Exception ex)
        {
            LogMalformedFrame(LiveFrameType.FileTable.ToString(), ex.Message);
        }
    }

    /// <summary>
    /// Parse the engine's SourceLocationManifest frame payload (#302, Phase 4). Wire layout: u32 entryCount,
    /// then per-entry u16 id, u16 fileId, u32 line, u8 kind, u8 methodLen, UTF-8 method-name bytes.
    /// Combined with the partner FileTable frame, gives the client a full siteId → (file, line, method, kind)
    /// lookup.
    /// </summary>
    private void HandleSourceLocationManifest(byte[] payload, int length)
    {
        try
        {
            using var ms = new MemoryStream(payload, 0, length, writable: false);
            using var br = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            var count = br.ReadUInt32();
            var entries = new SourceLocationEntryDto[count];
            for (uint i = 0; i < count; i++)
            {
                var id = br.ReadUInt16();
                var fileId = br.ReadUInt16();
                var line = br.ReadUInt32();
                var kind = br.ReadByte();
                var methodLen = br.ReadByte();
                var methodBytes = br.ReadBytes(methodLen);
                entries[i] = new SourceLocationEntryDto(
                    id, fileId, line, kind, System.Text.Encoding.UTF8.GetString(methodBytes));
            }
            _sourceLocationManifest = _sourceLocationManifest with { Entries = entries };
        }
        catch (Exception ex)
        {
            LogMalformedFrame(LiveFrameType.SourceLocationManifest.ToString(), ex.Message);
        }
    }

    private void HandleBlock(byte[] payload, int length)
    {
        if (_builder == null)
        {
            // Block before Init — engine bug or mid-session reconnect race. Drop silently.
            return;
        }

        if (length < TraceBlockEncoder.BlockHeaderSize)
        {
            LogMalformedFrame("Block", "payload shorter than block header");
            return;
        }

        var (uncompressedBytes, compressedBytes, _) = TraceBlockEncoder.ReadBlockHeader(payload);
        if (uncompressedBytes < 0 || compressedBytes < 0
            || length < TraceBlockEncoder.BlockHeaderSize + compressedBytes)
        {
            LogMalformedFrame("Block", $"inconsistent block header (u={uncompressedBytes}, c={compressedBytes})");
            return;
        }

        if (_rawBlockBuffer.Length < uncompressedBytes)
        {
            _rawBlockBuffer = new byte[uncompressedBytes];
        }

        try
        {
            TraceBlockEncoder.DecodeBlock(
                payload.AsSpan(TraceBlockEncoder.BlockHeaderSize, compressedBytes),
                uncompressedBytes,
                _rawBlockBuffer);
        }
        catch (InvalidDataException ex)
        {
            LogDecompressionMismatch(uncompressedBytes, 0);
            LogMalformedFrame("Block", ex.Message);
            return;
        }

        Volatile.Write(ref _lastBlockReceivedTicks, DateTime.UtcNow.Ticks);

        // Walk for ThreadInfo records BEFORE feeding the builder. The builder treats records as opaque bytes; we
        // need the slot→name mapping surfaced to the client via SSE delta + metadata snapshot regardless of which
        // chunk a record happens to land in (otherwise late-arriving worker ThreadInfo records get buried in
        // unloaded chunks).
        ExtractThreadInfos(_rawBlockBuffer.AsSpan(0, uncompressedBytes));

        lock (_builderLock)
        {
            _builder.FeedRawRecords(_rawBlockBuffer.AsSpan(0, uncompressedBytes));
        }
    }

    /// <summary>
    /// Walk a raw record buffer for <see cref="TraceEventKind.ThreadInfo"/> records and populate
    /// <see cref="_threadInfos"/>. Mirrors the relevant parts of <see cref="RecordDecoder.DecodeThreadInfo"/>.
    /// Wire format: u16 size, u8 kind (=ThreadInfo), u8 threadSlot, i64 timestamp, then payload —
    /// i32 managedThreadId, u16 nameByteCount, UTF-8 name bytes, u8 ThreadKind (Main=0/Worker=1/Pool=2/Other=3).
    /// </summary>
    private void ExtractThreadInfos(ReadOnlySpan<byte> records)
    {
        const int CommonHeaderSize = 12;
        var pos = 0;
        while (pos + CommonHeaderSize <= records.Length)
        {
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size < CommonHeaderSize || pos + size > records.Length)
            {
                break;
            }
            var kind = (TraceEventKind)records[pos + 2];
            if (kind != TraceEventKind.ThreadInfo)
            {
                pos += size;
                continue;
            }
            var threadSlot = records[pos + 3];
            var payloadOffset = pos + CommonHeaderSize;
            if (payloadOffset + 6 > pos + size)
            {
                pos += size;
                continue;
            }
            var managedThreadId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(records[payloadOffset..]);
            var nameByteCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(records[(payloadOffset + 4)..]);
            string name = null;
            if (nameByteCount > 0 && nameByteCount <= 4096 && payloadOffset + 6 + nameByteCount <= pos + size)
            {
                try { name = System.Text.Encoding.UTF8.GetString(records.Slice(payloadOffset + 6, nameByteCount)); }
                catch (System.Text.DecoderFallbackException) { name = null; }
            }
            // Trailing u8 ThreadKind (cache/wire v4+). Older records omit it; default to Other (=3).
            byte threadKind = 3;
            var kindOffset = payloadOffset + 6 + nameByteCount;
            if (kindOffset < pos + size)
            {
                threadKind = records[kindOffset];
            }
            ThreadInfoDto dto;
            bool added;
            lock (_threadInfos)
            {
                if (_threadInfos.TryGetValue(threadSlot, out var existing)
                    && existing.Name == name
                    && existing.ManagedThreadId == managedThreadId
                    && existing.Kind == threadKind)
                {
                    pos += size;
                    continue;
                }
                dto = new ThreadInfoDto(threadSlot, name ?? string.Empty, managedThreadId, threadKind);
                _threadInfos[threadSlot] = dto;
                added = true;
            }
            if (added)
            {
                _metadataSnapshot = null;
                ThreadInfoAdded?.Invoke(dto);
                BroadcastDelta(new LiveStreamEventDto(Kind: "threadInfoAdded", ThreadInfo: dto));
            }
            pos += size;
        }
    }

    private void OnBuilderTickFinalized(TickSummary summary)
    {
        // Builder fires synchronously inside FeedRawRecords (which we already hold _builderLock for). Project to DTO and
        // append; SSE delta fanout happens outside the lock to avoid back-pressure on the read loop.
        var dto = new TickSummaryDto(
            TickNumber: summary.TickNumber,
            StartUs: summary.StartUs,
            DurationUs: summary.DurationUs,
            EventCount: summary.EventCount,
            MaxSystemDurationUs: summary.MaxSystemDurationUs,
            ActiveSystemsBitmask: summary.ActiveSystemsBitmask.ToString(),
            OverloadLevel: summary.OverloadLevel,
            TickMultiplier: summary.TickMultiplier,
            MetronomeWaitUs: summary.MetronomeWaitUs,
            MetronomeIntentClass: summary.MetronomeIntentClass,
            ConsecutiveOverrun: summary.ConsecutiveOverrun,
            ConsecutiveUnderrun: summary.ConsecutiveUnderrun);
        _tickSummaries.Add(dto);
        _metadataSnapshot = null;
        TickSummaryAdded?.Invoke(dto);
        BroadcastDelta(new LiveStreamEventDto(Kind: "tickSummaryAdded", TickSummary: dto));
    }

    private void OnBuilderChunkFlushed(ChunkManifestEntry entry)
    {
        var isContinuation = (entry.Flags & TraceFileCacheConstants.FlagIsContinuation) != 0;
        var dto = new ChunkManifestEntryDto(
            FromTick: entry.FromTick,
            ToTick: entry.ToTick,
            EventCount: entry.EventCount,
            IsContinuation: isContinuation);
        _chunkManifest.Add(dto);
        _metadataSnapshot = null;
        ChunkAdded?.Invoke(dto);
        BroadcastDelta(new LiveStreamEventDto(Kind: "chunkAdded", ChunkEntry: dto));
    }

    private void OnFlushChunkTimer(object _)
    {
        if (_disposed || _builder == null) return;
        try
        {
            lock (_builderLock)
            {
                _builder.FlushCurrentChunk();
            }
        }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private void OnTrailingTickTimer(object _)
    {
        if (_disposed || _builder == null) return;
        // Only finalize the trailing tick if no records have arrived in the last window. Otherwise we'd race the builder's
        // own tick-on-TickStart finalization and double-count.
        var last = Volatile.Read(ref _lastBlockReceivedTicks);
        if (last == 0) return;
        var elapsedMs = (DateTime.UtcNow.Ticks - last) / TimeSpan.TicksPerMillisecond;
        if (elapsedMs < TrailingTickTimerMs) return;
        try
        {
            lock (_builderLock)
            {
                _builder.FlushTrailingTick();
            }
        }
        catch (ObjectDisposedException) { }
    }

    private void OnGlobalMetricsTimer(object _)
    {
        if (_disposed || _builder == null) return;
        try
        {
            GlobalMetricsDto metrics;
            lock (_builderLock)
            {
                metrics = ProjectGlobalMetrics(_builder.CurrentGlobalMetrics);
                _globalMetrics = metrics;
                _metadataSnapshot = null;
            }
            GlobalMetricsUpdated?.Invoke(metrics);
            BroadcastDelta(new LiveStreamEventDto(Kind: "globalMetricsUpdated", GlobalMetrics: metrics));
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Build a fresh snapshot from the current state. Called rarely (HTTP /metadata, SSE connect, after invalidation).
    /// Holds <see cref="_builderLock"/> briefly to copy the lists into arrays.
    /// </summary>
    private ProfilerMetadataDto BuildSnapshotLocked()
    {
        TickSummaryDto[] tickSummariesArr;
        ChunkManifestEntryDto[] chunkManifestArr;
        GlobalMetricsDto metrics;
        lock (_builderLock)
        {
            tickSummariesArr = _tickSummaries.ToArray();
            chunkManifestArr = _chunkManifest.ToArray();
            metrics = _globalMetrics;
        }
        var snap = new ProfilerMetadataDto(
            Fingerprint: string.Empty,
            Header: _initialHeader ?? new ProfilerHeaderDto(0, 0, 0, 0, 0, 0, 0, 0, 0),
            Systems: _initialSystems,
            Archetypes: _initialArchetypes,
            ComponentTypes: _initialComponentTypes,
            SpanNames: new Dictionary<int, string>(),
            GlobalMetrics: metrics,
            TickSummaries: tickSummariesArr,
            ChunkManifest: chunkManifestArr,
            GcSuspensions: []);
        _metadataSnapshot = snap;
        return snap;
    }

    private void BroadcastDelta(LiveStreamEventDto delta)
    {
        if (_subscribers.Count == 0) return;
        foreach (var (id, channel) in _subscribers)
        {
            // Fast path: TryWrite. If the buffer is full, kick off a bounded async write that disconnects the slow client
            // after SlowSubscriberTimeoutMs. Doing this synchronously here would back-pressure the TCP read loop.
            if (channel.Writer.TryWrite(delta))
            {
                continue;
            }
            _ = WriteWithTimeoutAsync(id, channel, delta);
        }
    }

    private async Task WriteWithTimeoutAsync(Guid id, Channel<LiveStreamEventDto> channel, LiveStreamEventDto delta)
    {
        try
        {
            using var cts = new CancellationTokenSource(SlowSubscriberTimeoutMs);
            await channel.Writer.WriteAsync(delta, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogSlowSubscriberKicked(id);
            channel.Writer.TryComplete();
            _subscribers.TryRemove(id, out _);
        }
        catch (ChannelClosedException)
        {
            // Subscriber already gone — nothing to do.
        }
    }

    private void SetConnectionStatus(string status)
    {
        if (_connectionStatus == status) return;
        _connectionStatus = status;
        ConnectionStateChanged?.Invoke(status);
        BroadcastDelta(new LiveStreamEventDto(Kind: "heartbeat", Status: status));
    }

    private static string ComputeInitSignature(ProfilerHeaderDto header, SystemDefinitionDto[] systems, ArchetypeDto[] archetypes, ComponentTypeDto[] componentTypes)
    {
        // Cheap stable signature: SHA-256 of a deterministic textual rendering. Collisions across genuinely-different engines
        // would require both (a) the same primitive header and (b) the exact same definition tables — fine for our use.
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(header.Version);
        bw.Write(header.TimestampFrequency);
        bw.Write(header.BaseTickRate);
        bw.Write(header.WorkerCount);
        bw.Write(header.SystemCount);
        bw.Write(header.ArchetypeCount);
        bw.Write(header.ComponentTypeCount);
        foreach (var s in systems)
        {
            bw.Write(s.Index);
            bw.Write(s.Name);
            bw.Write(s.Type);
            bw.Write(s.Priority);
            bw.Write(s.IsParallel);
            bw.Write(s.TierFilter);
            foreach (var p in s.Predecessors) bw.Write(p);
            foreach (var p in s.Successors) bw.Write(p);
        }
        foreach (var a in archetypes)
        {
            bw.Write(a.ArchetypeId);
            bw.Write(a.Name);
        }
        foreach (var c in componentTypes)
        {
            bw.Write(c.ComponentTypeId);
            bw.Write(c.Name);
        }
        bw.Flush();
        var hash = sha.ComputeHash(ms.ToArray());
        return Convert.ToHexString(hash);
    }

    private static GlobalMetricsDto ProjectGlobalMetrics(GlobalMetricsFixed metrics)
        => new(
            GlobalStartUs: metrics.GlobalStartUs,
            GlobalEndUs: metrics.GlobalEndUs,
            MaxTickDurationUs: metrics.MaxTickDurationUs,
            MaxSystemDurationUs: metrics.MaxSystemDurationUs,
            P95TickDurationUs: metrics.P95TickDurationUs,
            TotalEvents: metrics.TotalEvents,
            TotalTicks: metrics.TotalTicks,
            SystemAggregates: []);

    private static ProfilerHeaderDto ProjectHeader(TraceFileReader reader)
    {
        var h = reader.Header;
        return new ProfilerHeaderDto(
            Version: h.Version,
            TimestampFrequency: h.TimestampFrequency,
            BaseTickRate: h.BaseTickRate,
            WorkerCount: h.WorkerCount,
            SystemCount: h.SystemCount,
            ArchetypeCount: h.ArchetypeCount,
            ComponentTypeCount: h.ComponentTypeCount,
            CreatedUtcTicks: h.CreatedUtcTicks,
            SamplingSessionStartQpc: h.SamplingSessionStartQpc);
    }

    private static SystemDefinitionDto[] ProjectSystems(TraceFileReader reader)
    {
        var arr = new SystemDefinitionDto[reader.Systems.Count];
        for (var i = 0; i < reader.Systems.Count; i++)
        {
            var sr = reader.Systems[i];
            arr[i] = new SystemDefinitionDto(
                Index: sr.Index,
                Name: sr.Name,
                Type: sr.Type,
                Priority: sr.Priority,
                IsParallel: sr.IsParallel,
                TierFilter: sr.TierFilter,
                Predecessors: sr.Predecessors,
                Successors: sr.Successors);
        }
        return arr;
    }

    private static ArchetypeDto[] ProjectArchetypes(TraceFileReader reader)
    {
        var arr = new ArchetypeDto[reader.Archetypes.Count];
        for (var i = 0; i < reader.Archetypes.Count; i++)
        {
            arr[i] = new ArchetypeDto(reader.Archetypes[i].ArchetypeId, reader.Archetypes[i].Name);
        }
        return arr;
    }

    private static ComponentTypeDto[] ProjectComponentTypes(TraceFileReader reader)
    {
        var arr = new ComponentTypeDto[reader.ComponentTypes.Count];
        for (var i = 0; i < reader.ComponentTypes.Count; i++)
        {
            arr[i] = new ComponentTypeDto(reader.ComponentTypes[i].ComponentTypeId, reader.ComponentTypes[i].Name);
        }
        return arr;
    }

    private static async Task<IPAddress> ResolveIPv4Async(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork)
        {
            return literal;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
        }
        catch (SocketException ex)
        {
            throw new WorkbenchException(
                StatusCodes.Status503ServiceUnavailable,
                "attach_dns_failed",
                $"Failed to resolve '{host}' to an IPv4 address: {ex.Message}",
                ex);
        }

        if (addresses.Length == 0)
        {
            throw new WorkbenchException(
                StatusCodes.Status503ServiceUnavailable,
                "attach_dns_failed",
                $"Host '{host}' resolved to zero IPv4 addresses. The Workbench connects to the engine over IPv4 only.");
        }

        return addresses[0];
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        var idx = trimmed.LastIndexOf(':');
        if (idx < 0) return (trimmed, DefaultPort);
        var host = trimmed[..idx];
        var portStr = trimmed[(idx + 1)..];
        if (!int.TryParse(portStr, out var port) || port < 1 || port > 65535)
        {
            throw new WorkbenchException(
                StatusCodes.Status400BadRequest,
                "invalid_endpoint",
                $"Invalid port '{portStr}' in endpoint '{endpoint}'. Expected host:port (1..65535).");
        }
        return (host, port);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AttachSessionRuntime));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _flushChunkTimer?.Dispose(); } catch { }
        try { _trailingTickTimer?.Dispose(); } catch { }
        try { _globalMetricsTimer?.Dispose(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        try
        {
            lock (_builderLock)
            {
                _builder?.Dispose();
            }
        }
        catch { }
        try { _tempFile?.Dispose(); } catch { }
        if (!_metadataTcs.Task.IsCompleted)
        {
            _metadataTcs.TrySetException(new ObjectDisposedException(nameof(AttachSessionRuntime)));
        }
        foreach (var kv in _subscribers)
        {
            try { kv.Value.Writer.TryComplete(); } catch { }
        }
        _subscribers.Clear();
    }
}
