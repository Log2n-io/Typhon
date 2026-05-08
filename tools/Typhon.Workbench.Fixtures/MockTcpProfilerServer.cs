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
/// <c>RecordDecoder</c> processes the frames without any test-only code paths on the client side.
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
public sealed class MockTcpProfilerServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task _acceptTask;
    private TcpClient _client;
    private bool _started;

    public int Port { get; private set; }

    /// <summary>How often to emit Block frames after Init. Default 100 ms.</summary>
    public TimeSpan BlockInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Maximum blocks to emit before idling. Defaults to 10 so tests don't run indefinitely.</summary>
    public int MaxBlocks { get; set; } = 10;

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

    private async Task AcceptLoopAsync()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            _client.NoDelay = true;
            var stream = _client.GetStream();

            // Send Init frame immediately on connect.
            var initPayload = BuildInitPayload();
            await WriteFrameAsync(stream, LiveFrameType.Init, initPayload, _cts.Token).ConfigureAwait(false);

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
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (ObjectDisposedException) { /* listener disposed */ }
        catch (SocketException) { /* client disconnected */ }
        catch (IOException) { /* client disconnected mid-send */ }
    }

    private static async Task WriteFrameAsync(NetworkStream stream, LiveFrameType type, byte[] payload, CancellationToken ct)
    {
        var header = new byte[LiveStreamProtocol.FrameHeaderSize];
        LiveStreamProtocol.WriteFrameHeader(header, type, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Minimal Init payload: header + empty system / archetype / component-type tables.</summary>
    private static byte[] BuildInitPayload()
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
            CreatedUtcTicks = 0,
            SamplingSessionStartQpc = 0,
        };
        ms.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in header, 1)));

        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((ushort)0); // system count = 0
        bw.Write((ushort)0); // archetype count = 0
        bw.Write((ushort)0); // component-type count = 0
        bw.Write((ushort)0); // phase count = 0 (v6+ Phases section)
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
        try { _client?.Close(); } catch { /* ignore */ }
        try { _listener.Stop(); } catch { /* ignore */ }
        if (_acceptTask != null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch { /* already observed */ }
        }
        _cts.Dispose();
    }
}
