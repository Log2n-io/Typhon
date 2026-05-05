using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using K4os.Compression.LZ4;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler.Exporters;

/// <summary>
/// <see cref="IProfilerExporter"/> that streams typed-event record batches over TCP to a single connected client (typically the browser-based
/// profiler viewer).
/// </summary>
/// <remarks>
/// <para>
/// <b>Framing:</b> <see cref="LiveStreamProtocol"/> envelopes. On connect, the exporter sends an <see cref="LiveFrameType.Init"/> frame whose
/// payload is the v3 file header + system / archetype / component-type tables (identical binary format to the file writer). Subsequent batches
/// are sent as <see cref="LiveFrameType.Block"/> frames: <see cref="TraceBlockEncoder.BlockHeaderSize"/> + LZ4-compressed record bytes.
/// </para>
/// <para>
/// <b>Single-client model:</b> listens on one port, accepts one client at a time, rejects additional connections.
/// </para>
/// <para>
/// <b>Non-blocking sends:</b> after the initial Init frame is delivered in blocking mode, the socket is switched to non-blocking. Subsequent
/// sends treat <see cref="SocketError.WouldBlock"/> as a drop — the exporter thread never blocks on a slow client.
/// </para>
/// </remarks>
public sealed class TcpExporter : ResourceNode, IProfilerExporter
{
    private readonly int _port;
    private readonly int _liveConnectTimeoutMs;
    private readonly ManualResetEventSlim _firstClientConnected = new(initialState: false);
    private TcpListener _listener;
    private Thread _acceptThread;
    private bool _shutdown;
    private Socket _client;
    private long _droppedFrames;

    private byte[] _compressedBuffer;
    private byte[] _frameBuffer;
    private ProfilerSessionMetadata _metadata;

    private bool _disposed;
    private bool _shutdownFrameSent;

    /// <summary>
    /// Stopwatch timestamp of the last catch-up ThreadInfo block sent from <see cref="ProcessBatch"/>. Drives a 1 Hz
    /// re-emit of all currently-claimed slots' ThreadInfo records, backstopping any single-batch losses (WouldBlock,
    /// pre-_client_set window). Read/written only from the consumer thread that calls ProcessBatch — no lock needed.
    /// Initial value 0 forces the first ProcessBatch after each connect to fire a catch-up immediately.
    /// </summary>
    private long _lastCatchupTicks;

    /// <summary>Stopwatch ticks per catch-up interval (1 second).</summary>
    private static readonly long CatchupIntervalTicks = Stopwatch.Frequency;

    /// <summary>
    /// Construct a TcpExporter listening on <paramref name="port"/>. Pass <paramref name="liveConnectTimeoutMs"/> &gt; 0
    /// to make <see cref="Initialize"/> block until the first client connects (or the timeout elapses) — gives the
    /// host an attach-the-viewer-before-startup window. <c>0</c> (default) preserves the original async behavior:
    /// Initialize returns immediately, clients connect when they connect.
    /// </summary>
    public TcpExporter(int port, IResource parent, int liveConnectTimeoutMs = 0)
        : base("TcpExporter", ResourceType.Service, parent ?? throw new ArgumentNullException(nameof(parent)))
    {
        if (liveConnectTimeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(liveConnectTimeoutMs), "must be ≥ 0 (0 = don't wait)");
        }
        _port = port;
        _liveConnectTimeoutMs = liveConnectTimeoutMs;
        // Match <see cref="FileExporter"/>'s capacity — 64 gives the socket-send path ~16 MB of slack, enough to absorb a single// gcChurn-class burst
        // without drop-newest firing. Previously 4, which was too tight for any workload with multi-tick-spanning// I/O pressure. See FileExporter ctor for
        // why not 256.
        Queue = new ExporterQueue(64);
    }

    /// <summary>True if the first client has connected since startup. Stays true after the client disconnects (we only signal once).</summary>
    public bool HasClientEverConnected => _firstClientConnected.IsSet;

    /// <summary>The configured live-connect wait timeout in milliseconds (0 = no wait).</summary>
    public int LiveConnectTimeoutMs => _liveConnectTimeoutMs;

    /// <inheritdoc />
    public ExporterQueue Queue { get; }

    /// <summary>Number of frames dropped because the socket was not write-ready or partially sent.</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <inheritdoc />
    public void Initialize(ProfilerSessionMetadata metadata)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

        _compressedBuffer = new byte[LZ4Codec.MaximumOutputSize(TraceRecordBatchPool.MaxPayloadBytes)];
        _frameBuffer = new byte[LiveStreamProtocol.FrameHeaderSize + TraceBlockEncoder.BlockHeaderSize + _compressedBuffer.Length];

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start(1);

        _acceptThread = new Thread(AcceptLoop)
        {
            Name = "TyphonProfilerTcpExporterAccept",
            IsBackground = true
        };
        _acceptThread.Start();

        // Synchronous "wait for first client" gate. The listener is up, the accept thread is running — both
        // necessary for a client to actually connect during this wait. Block on `_firstClientConnected` (Set
        // by AcceptLoop after `_client = socket`). The wait is a no-op when the timeout is 0.
        if (_liveConnectTimeoutMs > 0)
        {
            // We don't reach Stopwatch.Elapsed here for logging because the host owns logging — the
            // counterpart `HasClientEverConnected` after Initialize lets the host observe the outcome
            // (timeout vs. connected) and decide what to print.
            _firstClientConnected.Wait(_liveConnectTimeoutMs);
        }
    }

    /// <inheritdoc />
    public void ProcessBatch(TraceRecordBatch batch)
    {
        var client = _client;
        if (client == null || batch.PayloadBytes == 0)
        {
            return;
        }

        // Periodic catch-up backstop. The producer's one-shot ThreadInfo emit at AssignClaim time can be lost if its
        // batch hits a WouldBlock send or lands during the pre-_client_set window — the workbench then never learns
        // that slot's name and falls back to "Slot N". Re-emitting catch-up here at most once per second walks the
        // registry's OwnerThreadName cache (authoritative once AssignClaim completes) and re-ships ThreadInfo for
        // every active slot. Workbench's upsertThreadInfo is idempotent, so re-sends are no-ops once received.
        // Cost: ~600 bytes/sec while connected; runs on the consumer thread (same as the batch send below) so no
        // socket lock needed. Initial _lastCatchupTicks=0 forces an immediate fire on the first batch after connect,
        // backstopping the connect-time catch-up that ran in AcceptLoop before _client_set.
        var now = Stopwatch.GetTimestamp();
        if (now - _lastCatchupTicks > CatchupIntervalTicks)
        {
            var (catchupFrame, catchupLen) = BuildCatchupThreadInfoFrame();
            if (catchupFrame != null)
            {
                TrySendAll(client, catchupFrame, 0, catchupLen);
            }
            _lastCatchupTicks = now;
        }

        // Frame layout: [5B envelope][12B block header][LZ4-compressed records]
        var fb = _frameBuffer.AsSpan();
        var blockHeader = fb.Slice(LiveStreamProtocol.FrameHeaderSize, TraceBlockEncoder.BlockHeaderSize);
        var compressedSlot = fb[(LiveStreamProtocol.FrameHeaderSize + TraceBlockEncoder.BlockHeaderSize)..];

        var compressedSize = TraceBlockEncoder.EncodeBlock(
            batch.Payload.AsSpan(0, batch.PayloadBytes),
            batch.Count,
            compressedSlot,
            blockHeader);

        var payloadSize = TraceBlockEncoder.BlockHeaderSize + compressedSize;
        LiveStreamProtocol.WriteFrameHeader(fb, LiveFrameType.Block, payloadSize);

        TrySendAll(client, _frameBuffer, 0, LiveStreamProtocol.FrameHeaderSize + payloadSize);
    }

    /// <inheritdoc />
    public void Flush()
    {
        SendShutdownFrame();

        _shutdown = true;
        try { _listener?.Stop(); }
        catch
        {
            // ignored
        }

        _acceptThread?.Join(500);
    }

    /// <summary>
    /// Send the <see cref="LiveFrameType.Shutdown"/> envelope synchronously, switching the socket back to blocking mode for this one send. The
    /// hot-path uses non-blocking <see cref="TrySendAll"/>, which silently drops on <see cref="SocketError.WouldBlock"/> — fine for Block frames
    /// (the consumer's bounded queue absorbs back-pressure) but disastrous for the Shutdown signal. If the workbench misses Shutdown, it sees
    /// the socket close as "connection lost" and enters the reconnect loop forever. Idempotent — second call no-ops.
    /// </summary>
    private void SendShutdownFrame()
    {
        if (_shutdownFrameSent)
        {
            return;
        }
        _shutdownFrameSent = true;

        var client = _client;
        if (client == null)
        {
            return;
        }

        var shutdownFrame = new byte[LiveStreamProtocol.FrameHeaderSize];
        LiveStreamProtocol.WriteFrameHeader(shutdownFrame, LiveFrameType.Shutdown, 0);
        try
        {
            client.Blocking = true;
            client.SendTimeout = 500;
            client.Send(shutdownFrame, 0, shutdownFrame.Length, SocketFlags.None);
        }
        catch
        {
            // Best-effort — peer may already be gone or the socket may be in a bad state.
        }
    }

    /// <inheritdoc />
    void IDisposable.Dispose() => Dispose(true);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _shutdown = true;
            // Belt-and-suspenders: if Flush wasn't called (e.g. owner skipped Stop and went straight to Dispose),
            // still try to deliver the Shutdown envelope before tearing down the socket.
            SendShutdownFrame();
            var client = Interlocked.Exchange(ref _client, null);
            if (client != null)
            {
                try { client.Shutdown(SocketShutdown.Both); }
                catch
                {
                    // ignored
                }

                try { client.Close(); }
                catch
                {
                    // ignored
                }
            }
            try { _listener?.Stop(); }
            catch
            {
                // ignored
            }

            _acceptThread?.Join(2000);
            try { Queue?.Dispose(); }
            catch
            {
                // ignored
            }
            try { _firstClientConnected.Dispose(); }
            catch
            {
                // ignored
            }
        }
        base.Dispose(disposing);
    }

    private void TrySendAll(Socket client, byte[] buffer, int offset, int length)
    {
        try
        {
            var sent = client.Send(buffer, offset, length, SocketFlags.None, out var error);
            if (error == SocketError.Success && sent == length)
            {
                return;
            }

            if (error == SocketError.WouldBlock)
            {
                Interlocked.Increment(ref _droppedFrames);
                return;
            }
            Interlocked.Increment(ref _droppedFrames);
            DisposeClient(client);
        }
        catch (SocketException)
        {
            DisposeClient(client);
        }
        catch (ObjectDisposedException)
        {
            _client = null;
        }
    }

    private void DisposeClient(Socket expected)
    {
        if (Interlocked.CompareExchange(ref _client, null, expected) == expected)
        {
            try { expected.Close(); }
            catch
            {
                // ignored
            }
        }
    }

    private void AcceptLoop()
    {
        while (!_shutdown)
        {
            Socket socket;
            try
            {
                socket = _listener.AcceptSocket();
            }
            catch (SocketException) when (_shutdown) { break; }
            catch (ObjectDisposedException) { break; }

            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            if (_client != null)
            {
                try { socket.Close(); }
                catch
                {
                    // ignored
                }

                continue;
            }

            try
            {
                var initPayload = BuildInitPayload();
                var frameBuffer = new byte[LiveStreamProtocol.FrameHeaderSize + initPayload.Length];
                LiveStreamProtocol.WriteFrameHeader(frameBuffer.AsSpan(), LiveFrameType.Init, initPayload.Length);
                initPayload.CopyTo(frameBuffer.AsSpan(LiveStreamProtocol.FrameHeaderSize));

                socket.SendTimeout = 5000;
                socket.Send(frameBuffer);

                // #302 Phase 4: send the FileTable + SourceLocationManifest frames once during the init handshake.
                // No-op when the generated SourceLocations table is empty (zero attributed sites).
                // Sent BEFORE the catch-up block so the client has the manifest by the time spans start arriving.
                var (fileTableFrame, slManifestFrame) = BuildSourceLocationFrames();
                if (fileTableFrame != null)
                {
                    socket.Send(fileTableFrame);
                }
                if (slManifestFrame != null)
                {
                    socket.Send(slManifestFrame);
                }

                // Before switching to non-blocking (and before exposing _client to ProcessBatch), send a catch-up Block frame
                // carrying a synthetic ThreadInfo record for every currently-claimed slot. Without this, mid-session live
                // connections never see the one-shot ThreadInfo records emitted when each worker claimed its slot — those
                // were silently dropped by ProcessBatch while _client was null — and the viewer has no slot→name mapping,
                // which leaves lanes unlabeled and the UI unable to lay out span tracks. ProcessBatch fires another catch-up
                // every ~1 s as a backstop for any slots claimed during the window between this scan and `_client = socket`,
                // and for any later batch drops that wipe out a slot's one-shot ThreadInfo record.
                var (catchupFrame, catchupLen) = BuildCatchupThreadInfoFrame();
                if (catchupFrame != null)
                {
                    socket.Send(catchupFrame, 0, catchupLen, SocketFlags.None);
                }

                socket.Blocking = false;
                _client = socket;

                // Signal any synchronous-wait gate set up at Initialize time. Done AFTER `_client = socket` so a
                // host that observes HasClientEverConnected and starts emitting can be sure ProcessBatch will
                // see a non-null client. ManualResetEventSlim.Set is idempotent — second connect doesn't re-block.
                _firstClientConnected.Set();
            }
            catch (SocketException)
            {
                try { socket.Close(); }
                catch
                {
                    // ignored
                }
            }
        }
    }

    /// <summary>
    /// #302 Phase 4: build the optional FileTable + SourceLocationManifest frames sent during the init handshake.
    /// Returns (null, null) when the generated <c>SourceLocations</c> table is empty (zero attributed sites).
    /// Each frame's payload mirrors the corresponding <c>.typhon-trace</c> trailer section MINUS the magic constants (the LiveFrameType byte already identifies
    /// the section). See claude/design/observability/10-profiler-source-attribution.md §4.7.
    /// </summary>
    private static (byte[] FileTableFrame, byte[] ManifestFrame) BuildSourceLocationFrames()
    {
        // Merged manifest: compile-time call sites + runtime-resolved system entries from
        // RuntimeSourceLocationManifest (populated by DagScheduler's constructor).
        var (files, entries) = RuntimeSourceLocationManifest.BuildMerged();
        if (files.Length == 0 || entries.Length == 0)
        {
            return (null, null);
        }

        // FileTable frame payload: [u32 entryCount][per entry: u16 fileId, u16 pathLen, UTF-8 bytes]
        byte[] fileTableFrame;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false))
        {
            bw.Write((uint)files.Length);
            for (ushort i = 0; i < files.Length; i++)
            {
                bw.Write(i);
                var bytes = Encoding.UTF8.GetBytes(files[i] ?? string.Empty);
                var len = (ushort)Math.Min(bytes.Length, ushort.MaxValue);
                bw.Write(len);
                bw.Write(bytes, 0, len);
            }
            bw.Flush();
            var payload = ms.ToArray();
            fileTableFrame = new byte[LiveStreamProtocol.FrameHeaderSize + payload.Length];
            LiveStreamProtocol.WriteFrameHeader(fileTableFrame.AsSpan(), LiveFrameType.FileTable, payload.Length);
            payload.CopyTo(fileTableFrame.AsSpan(LiveStreamProtocol.FrameHeaderSize));
        }

        // SourceLocationManifest frame payload: [u32 entryCount][per entry: u16 id, u16 fileId, u32 line, u8 kind, u8 methodLen, UTF-8 method bytes]
        byte[] manifestFrame;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false))
        {
            bw.Write((uint)entries.Length);
            foreach (var e in entries)
            {
                bw.Write(e.Id);
                bw.Write(e.FileId);
                bw.Write(e.Line);
                bw.Write(e.Kind);
                var bytes = Encoding.UTF8.GetBytes(e.Method ?? string.Empty);
                var len = (byte)Math.Min(bytes.Length, 255);
                bw.Write(len);
                bw.Write(bytes, 0, len);
            }
            bw.Flush();
            var payload = ms.ToArray();
            manifestFrame = new byte[LiveStreamProtocol.FrameHeaderSize + payload.Length];
            LiveStreamProtocol.WriteFrameHeader(manifestFrame.AsSpan(), LiveFrameType.SourceLocationManifest, payload.Length);
            payload.CopyTo(manifestFrame.AsSpan(LiveStreamProtocol.FrameHeaderSize));
        }

        return (fileTableFrame, manifestFrame);
    }

    /// <summary>
    /// Build the INIT frame payload — identical layout to the first four sections of a <c>.typhon-trace</c> file: header + system defs
    /// + archetype table + component type table.
    /// </summary>
    private byte[] BuildInitPayload()
    {
        using var ms = new MemoryStream();

        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = _metadata.StopwatchFrequency,
            BaseTickRate = _metadata.BaseTickRate,
            WorkerCount = (byte)_metadata.WorkerCount,
            SystemCount = (ushort)_metadata.Systems.Length,
            ArchetypeCount = (ushort)_metadata.Archetypes.Length,
            ComponentTypeCount = (ushort)_metadata.ComponentTypes.Length,
            CreatedUtcTicks = _metadata.StartedUtc.Ticks,
            SamplingSessionStartQpc = _metadata.SamplingSessionStartQpc,
        };
        ms.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in header, 1)));

        using var bw = new BinaryWriter(ms, Encoding.UTF8, true);

        // System definitions
        bw.Write((ushort)_metadata.Systems.Length);
        foreach (var sys in _metadata.Systems)
        {
            bw.Write(sys.Index);
            WriteShortString(bw, sys.Name);
            bw.Write(sys.Type);
            bw.Write(sys.Priority);
            bw.Write(sys.IsParallel);
            bw.Write(sys.TierFilter);

            bw.Write((byte)sys.Predecessors.Length);
            foreach (var pred in sys.Predecessors)
            {
                bw.Write(pred);
            }

            bw.Write((byte)sys.Successors.Length);
            foreach (var succ in sys.Successors)
            {
                bw.Write(succ);
            }
        }

        // Archetype table
        bw.Write((ushort)_metadata.Archetypes.Length);
        foreach (var a in _metadata.Archetypes)
        {
            bw.Write(a.ArchetypeId);
            WriteShortString(bw, a.Name);
        }

        // Component type table
        bw.Write((ushort)_metadata.ComponentTypes.Length);
        foreach (var c in _metadata.ComponentTypes)
        {
            bw.Write(c.ComponentTypeId);
            WriteShortString(bw, c.Name);
        }

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Encode a Block frame containing a synthetic ThreadInfo record for every currently-claimed slot, reading
    /// <see cref="ThreadSlot.OwnerManagedThreadId"/> + <see cref="ThreadSlot.OwnerThreadName"/> from the registry.
    /// Returns <c>(null, 0)</c> if no slots qualify (registry empty, or every active slot is mid-flight in
    /// <c>AssignClaim</c> with <c>OwnerManagedThreadId == 0</c>).
    /// </summary>
    /// <remarks>
    /// Called from two paths: (1) <see cref="AcceptLoop"/> on client accept (sent synchronously while the socket is
    /// still blocking) — covers slots active at connect time; (2) <see cref="ProcessBatch"/> on a 1 Hz cadence (sent
    /// non-blocking via <see cref="TrySendAll"/>) — backstops slots whose producer-side ThreadInfo emit was lost in
    /// a dropped batch (WouldBlock or pre-_client_set window). Workbench's upsert is idempotent, so re-sends are
    /// harmless once received.
    /// </remarks>
    private static (byte[] Frame, int Length) BuildCatchupThreadInfoFrame()
    {
        var scanLimit = ThreadSlotRegistry.HighWaterMark;
        if (scanLimit == 0)
        {
            return (null, 0);
        }

        // Allocate generously — 256 max slots × 273 bytes (12+4+2+255 name cap) = ~70 KB worst case. Heap-allocated
        // (vs. stackalloc) because this runs at most 1 Hz and keeps the stack frame small.
        var rawScratch = new byte[ThreadSlotRegistry.MaxSlots * 273];
        var writePos = 0;
        var recordCount = 0;
        var timestamp = Stopwatch.GetTimestamp();

        for (var i = 0; i < scanLimit; i++)
        {
            var state = ThreadSlotRegistry.GetSlotState(i);
            if (state != (int)SlotState.Active && state != (int)SlotState.Retiring)
            {
                continue;
            }

            var slot = ThreadSlotRegistry.GetSlot(i);
            var managedThreadId = slot.OwnerManagedThreadId;
            if (managedThreadId == 0)
            {
                continue; // AssignClaim still mid-flight — producer will emit its own ThreadInfo
            }

            var name = slot.OwnerThreadName;
            var kind = slot.OwnerThreadKind;
            var nameBytes = name != null ? Encoding.UTF8.GetBytes(name) : Array.Empty<byte>();
            if (nameBytes.Length > 255)
            {
                nameBytes = nameBytes.AsSpan(0, 255).ToArray(); // defensive cap; pathological for a thread name
            }

            var recordSize = ThreadInfoEventCodec.ComputeSize(nameBytes.Length);
            if (writePos + recordSize > rawScratch.Length)
            {
                break;
            }

            ThreadInfoEventCodec.WriteThreadInfo(rawScratch.AsSpan(writePos), (byte)i, timestamp, managedThreadId, nameBytes, kind, out var bytesWritten);
            writePos += bytesWritten;
            recordCount++;
        }

        if (recordCount == 0)
        {
            return (null, 0);
        }

        // Same framing as ProcessBatch: [frame header][block header][LZ4 compressed records].
        var maxCompressed = LZ4Codec.MaximumOutputSize(writePos);
        var frame = new byte[LiveStreamProtocol.FrameHeaderSize + TraceBlockEncoder.BlockHeaderSize + maxCompressed];
        var blockHeader = frame.AsSpan(LiveStreamProtocol.FrameHeaderSize, TraceBlockEncoder.BlockHeaderSize);
        var compressedSlot = frame.AsSpan(LiveStreamProtocol.FrameHeaderSize + TraceBlockEncoder.BlockHeaderSize);

        var compressedSize = TraceBlockEncoder.EncodeBlock(rawScratch.AsSpan(0, writePos), recordCount, compressedSlot, blockHeader);
        var payloadSize = TraceBlockEncoder.BlockHeaderSize + compressedSize;
        LiveStreamProtocol.WriteFrameHeader(frame.AsSpan(), LiveFrameType.Block, payloadSize);

        return (frame, LiveStreamProtocol.FrameHeaderSize + payloadSize);
    }

    private static void WriteShortString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var len = (byte)Math.Min(bytes.Length, 255);
        bw.Write(len);
        bw.Write(bytes, 0, len);
    }
}
