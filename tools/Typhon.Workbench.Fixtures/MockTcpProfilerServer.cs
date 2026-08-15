using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Profiler;

namespace Typhon.Workbench.Fixtures;

/// <summary>
/// Test / dev-fixture TCP profiler server. Listens on a dynamic port, accepts a single client,
/// sends an <see cref="LiveFrameType.Init"/> frame immediately on connect, and emits periodic
/// <see cref="LiveFrameType.Block"/> frames until stopped.
///
/// Every payload matches the real <see cref="Typhon.Profiler"/> wire format exactly (via
/// <see cref="LiveStreamProtocol.WriteFrameHeader"/> + a hand-assembled minimal
/// <see cref="TraceFileHeader"/> + empty metadata tables) so the Workbench's real
/// typed-DTO pipeline (<see cref="TraceEventDecoder"/>) processes the frames without any test-only code paths on the client side.
///
/// Usage (NUnit):
/// <code>
/// await using var server = new MockTcpProfilerServer();
/// server.Start();
/// var runtime = await AttachSessionRuntime.StartAsync(
///     $"localhost:{server.Port}", SessionManager, logger);
/// </code>
///
/// Usage (Playwright via DEBUG-only <c>POST /api/fixtures/mock-profiler</c>): the Workbench holds a
/// dictionary keyed by port and disposes each server on application shutdown.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two modes.</b> The default <i>timer</i> mode preserves the original behaviour — after Init it emits
/// <see cref="MaxBlocks"/> minimal tick blocks at <see cref="BlockInterval"/>. Setting <see cref="Scripted"/> before
/// <see cref="Start"/> suppresses that loop entirely and hands control to the test, which pushes exactly the records it
/// wants via <see cref="SendBlockAsync"/> and observes the effect deterministically. Filter tests need scripted mode:
/// asserting "tick N carries detail and tick N+1 does not" is untestable against a timer.
/// </para>
/// <para>
/// <b>Engine lifecycle simulation.</b> <see cref="SendShutdownAsync"/> models a clean quit (the engine's Shutdown
/// envelope), <see cref="DropClientAsync"/> models a crash (socket closed with no warning). Both leave the listener up,
/// so the Workbench's reconnect loop can land a fresh connection and receive a second Init — which is how restart
/// behaviour is exercised without launching a process.
/// </para>
/// </remarks>
public sealed class MockTcpProfilerServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _clientLock = new();
    private TaskCompletionSource _clientConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _acceptTask;
    private TcpClient _client;
    private bool _started;
    private int _connectionCount;

    public int Port { get; private set; }

    /// <summary>How often to emit Block frames after Init. Default 100 ms. Ignored when <see cref="Scripted"/> is set.</summary>
    public TimeSpan BlockInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Maximum blocks to emit before idling. Defaults to 10 so tests don't run indefinitely. Ignored when <see cref="Scripted"/> is set.</summary>
    public int MaxBlocks { get; set; } = 10;

    /// <summary>
    /// When true, no automatic Block emission happens — the test drives the stream via <see cref="SendBlockAsync"/>.
    /// Must be set before <see cref="Start"/>.
    /// </summary>
    public bool Scripted { get; set; }

    /// <summary>
    /// Value written into the Init header's <c>CreatedUtcTicks</c>. Changing it between connections models a genuine
    /// engine <b>restart</b>; leaving it constant models a transient socket drop within one engine process. The
    /// Workbench distinguishes the two on exactly this field, so a fixture that cannot vary it cannot test either.
    /// </summary>
    public long CreatedUtcTicks { get; set; }

    /// <summary>
    /// Number of client connections accepted so far. A restart scenario expects this to reach 2: the original attach,
    /// then the reconnect after <see cref="DropClientAsync"/> or <see cref="SendShutdownAsync"/>.
    /// </summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>True while a client socket is held open.</summary>
    public bool HasClient
    {
        get
        {
            lock (_clientLock)
            {
                return _client != null;
            }
        }
    }

    public MockTcpProfilerServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, port: 0);
    }

    public void Start()
    {
        if (_started) throw new InvalidOperationException("Already started.");
        _started = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Completes once a client has connected and its Init frame has been written. Scripted tests await this before
    /// pushing records, so a block can never race ahead of the handshake.
    /// </summary>
    public Task WaitForClientAsync(CancellationToken ct = default)
    {
        Task connected;
        lock (_clientLock)
        {
            connected = _clientConnected.Task;
        }
        return connected.WaitAsync(ct);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient accepted;
            try
            {
                accepted = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            accepted.NoDelay = true;
            lock (_clientLock)
            {
                _client = accepted;
            }
            Interlocked.Increment(ref _connectionCount);

            try
            {
                var stream = accepted.GetStream();

                // Send Init frame immediately on connect.
                var initPayload = BuildInitPayload(CreatedUtcTicks);
                await WriteFrameAsync(stream, LiveFrameType.Init, initPayload, _cts.Token).ConfigureAwait(false);

                lock (_clientLock)
                {
                    _clientConnected.TrySetResult();
                }

                if (Scripted)
                {
                    // The test owns the stream from here. Park until the connection is torn down or the server stops.
                    await WaitForDisconnectAsync(accepted).ConfigureAwait(false);
                    continue;
                }

                // Emit periodic Block frames. Each block carries one minimal TickStart + TickEnd record
                // pair so the client's tick counter increments observably.
                for (var i = 0; i < MaxBlocks && !_cts.IsCancellationRequested; i++)
                {
                    try
                    {
                        await Task.Delay(BlockInterval, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    var blockPayload = BuildBlockPayload(tickIndex: i + 1);
                    await WriteFrameAsync(stream, LiveFrameType.Block, blockPayload, _cts.Token).ConfigureAwait(false);
                }

                await WaitForDisconnectAsync(accepted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (ObjectDisposedException) { /* listener or client disposed */ }
            catch (SocketException) { /* client disconnected */ }
            catch (IOException) { /* client disconnected mid-send */ }
        }
    }

    /// <summary>Parks until the connection drops or the server is cancelled, so the accept loop can take the next client.</summary>
    private async Task WaitForDisconnectAsync(TcpClient client)
    {
        while (!_cts.IsCancellationRequested)
        {
            bool stillCurrent;
            lock (_clientLock)
            {
                stillCurrent = ReferenceEquals(_client, client);
            }
            if (!stillCurrent || !client.Connected)
            {
                return;
            }
            try
            {
                await Task.Delay(20, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Push one Block frame carrying <paramref name="records"/> — a packed, size-prefixed record buffer, typically built
    /// with <see cref="MockRecordFactory"/>. The caller decides where block boundaries fall, which is what makes a
    /// straddling-block test possible.
    /// </summary>
    public async Task SendBlockAsync(byte[] records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var recordCount = MockRecordFactory.CountRecords(records);
        var compressed = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(Math.Max(records.Length, 1))];
        var blockHeader = new byte[TraceBlockEncoder.BlockHeaderSize];
        var compressedSize = TraceBlockEncoder.EncodeBlock(records, recordCount, compressed, blockHeader);

        var payload = new byte[blockHeader.Length + compressedSize];
        blockHeader.CopyTo(payload, 0);
        Array.Copy(compressed, 0, payload, blockHeader.Length, compressedSize);

        await SendFrameAsync(LiveFrameType.Block, payload, ct).ConfigureAwait(false);
    }

    /// <summary>Send the clean-quit <see cref="LiveFrameType.Shutdown"/> envelope, as a real engine does on orderly teardown.</summary>
    public Task SendShutdownAsync(CancellationToken ct = default) => SendFrameAsync(LiveFrameType.Shutdown, [], ct);

    /// <summary>
    /// Close the client socket with no Shutdown envelope — the crash / kill path. The listener stays up, so the
    /// Workbench's reconnect loop can land a new connection and receive a second Init.
    /// </summary>
    public Task DropClientAsync()
    {
        TcpClient toClose;
        lock (_clientLock)
        {
            toClose = _client;
            _client = null;
            _clientConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { toClose?.Close(); } catch { /* already gone */ }
        return Task.CompletedTask;
    }

    private async Task SendFrameAsync(LiveFrameType type, byte[] payload, CancellationToken ct)
    {
        TcpClient client;
        lock (_clientLock)
        {
            client = _client;
        }
        if (client == null)
        {
            throw new InvalidOperationException("No client connected — await WaitForClientAsync() before sending.");
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(client.GetStream(), type, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task WriteFrameAsync(NetworkStream stream, LiveFrameType type, byte[] payload, CancellationToken ct)
    {
        var header = new byte[LiveStreamProtocol.FrameHeaderSize];
        LiveStreamProtocol.WriteFrameHeader(header, type, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Minimal Init payload: header + empty system / archetype / component-type tables.</summary>
    private static byte[] BuildInitPayload(long createdUtcTicks)
    {
        using var ms = new MemoryStream();
        var header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = 10_000_000,
            BaseTickRate = 1_000f,
            WorkerCount = 1,
            SystemCount = 0,
            ArchetypeCount = 0,
            ComponentTypeCount = 0,
            CreatedUtcTicks = createdUtcTicks,
            SamplingSessionStartQpc = 0,
        };
        ms.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in header, 1)));

        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((ushort)0); // system count = 0
        bw.Write((ushort)0); // archetype count = 0
        bw.Write((ushort)0); // component-type count = 0
        bw.Write((ushort)0); // tracks count = 0 (v11+ Track→DAG hierarchy)
        bw.Write((ushort)0); // dags count = 0
        // v7 static-structure tables — empty placeholders so the wire layout matches the source format
        // and AttachSessionRuntime can drive a TraceFileReader through it without throwing.
        bw.Write((ushort)0); // ComponentDefinitions count
        bw.Write((ushort)0); // ArchetypeDefinitions count
        bw.Write((ushort)0); // IndexCatalog count
        bw.Write(false);     // RuntimeConfig presence flag
        bw.Write((ushort)0); // EventQueueCatalog count
        bw.Write(0);         // ResourceGraphSnapshot count (i32)
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Minimal Block payload: <see cref="TraceBlockEncoder.BlockHeaderSize"/> + LZ4-compressed raw
    /// record bytes for a TickStart + TickEnd pair. Builds via the real encoder so the client's
    /// real decoder gets a valid block every time.
    ///
    /// Record layout must match the production <c>InstantEventCodec</c> exactly — TickStart is
    /// 12 B (header only), TickEnd is 14 B (header + <c>u8 overloadLevel</c> + <c>u8 tickMultiplier</c>).
    /// Writing an undersized TickEnd crashes the live decoder with an IndexOutOfRange on
    /// <c>payload[0]</c>, which is what made the Tier-0 attach canary time out before this fix.
    /// </summary>
    private static byte[] BuildBlockPayload(int tickIndex)
    {
        const int commonHeaderSize = 12;
        const int tickStartSize = commonHeaderSize;      // header only
        const int tickEndSize = commonHeaderSize + 2;    // header + overloadLevel + tickMultiplier
        var records = new byte[tickStartSize + tickEndSize];
        long ts = 100L * tickIndex;

        // Record 0 — TickStart (12 B)
        BinaryPrimitives.WriteUInt16LittleEndian(records.AsSpan(0, 2), tickStartSize);
        records[2] = (byte)Typhon.Profiler.TraceEventKind.TickStart;
        records[3] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(records.AsSpan(4, 8), ts);

        // Record 1 — TickEnd (14 B: common header + u8 overloadLevel + u8 tickMultiplier)
        var tickEndOffset = tickStartSize;
        BinaryPrimitives.WriteUInt16LittleEndian(records.AsSpan(tickEndOffset, 2), tickEndSize);
        records[tickEndOffset + 2] = (byte)Typhon.Profiler.TraceEventKind.TickEnd;
        records[tickEndOffset + 3] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(records.AsSpan(tickEndOffset + 4, 8), ts + 50);
        records[tickEndOffset + commonHeaderSize] = 0;     // overloadLevel
        records[tickEndOffset + commonHeaderSize + 1] = 1; // tickMultiplier

        var compressed = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(records.Length)];
        var blockHeader = new byte[TraceBlockEncoder.BlockHeaderSize];
        var compressedSize = TraceBlockEncoder.EncodeBlock(records, recordCount: 2, compressed, blockHeader);

        var payload = new byte[blockHeader.Length + compressedSize];
        blockHeader.CopyTo(payload, 0);
        Array.Copy(compressed, 0, payload, blockHeader.Length, compressedSize);
        return payload;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        lock (_clientLock)
        {
            try { _client?.Close(); } catch { /* ignore */ }
            _client = null;
        }
        try { _listener.Stop(); } catch { /* ignore */ }
        if (_acceptTask != null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch { /* already observed */ }
        }
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
