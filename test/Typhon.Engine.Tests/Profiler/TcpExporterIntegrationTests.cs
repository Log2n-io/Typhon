using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine.Profiler;
using Typhon.Engine.Profiler.Exporters;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Loopback integration test for the v3 TCP exporter. Starts a <see cref="TcpExporter"/> on an ephemeral port, connects a raw <see cref="TcpClient"/>,
/// consumes the INIT frame + at least one Block frame, and asserts the framed content round-trips cleanly through the block encoder + typed codecs.
/// </summary>
/// <remarks>
/// <b>Why loopback matters:</b> proves the full producer → ring → consumer → exporter thread → socket → framing path end-to-end. The INIT frame
/// is parsed by wrapping its bytes in a <see cref="MemoryStream"/> and feeding it to <see cref="TraceFileReader"/> — the same parser the profiler
/// server uses for live sessions, so a pass here validates the server's plumbing by proxy.
/// </remarks>
[TestFixture]
[NonParallelizable] // shares static TyphonProfiler state with other fixtures running in parallel.
public class TcpExporterIntegrationTests
{
    private const int DiscoveryPort = 0; // ask OS for a free port

    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = $"TcpExporterIT-{Guid.NewGuid():N}" });
    }

    [TearDown]
    public void TearDown()
    {
        try { TyphonProfiler.Stop(); } catch { }
        TyphonProfiler.ResetForTests();
    }

    [Test]
    public void Loopback_InitAndBlockFrames_RoundTrip()
    {
        // ── Find a free port so the test doesn't collide with other runs ──────────────────────────
        int port;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, DiscoveryPort))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var metadata = BuildMetadata();
        var tcpExporter = new TcpExporter(port, _registry.Profiler);
        TyphonProfiler.AttachExporter(tcpExporter);

        TyphonProfiler.Start(_registry.Profiler, metadata);

        try
        {
            // Connect as a raw TCP client, reading the INIT + one Block frame.
            using var client = new TcpClient();
            // The exporter's accept loop runs on its own thread; give it a brief chance to start listening.
            ConnectWithRetry(client, "127.0.0.1", port, timeoutMs: 2000);
            using var stream = client.GetStream();
            stream.ReadTimeout = 5000;

            // ── Frame 1: INIT ──
            var (initType, initPayload) = ReadFrame(stream);
            Assert.That(initType, Is.EqualTo(LiveFrameType.Init), "First frame must be INIT");

            // The INIT payload is byte-identical to the first four sections of a v3 trace file.
            // Reuse TraceFileReader by wrapping the bytes in a MemoryStream.
            using (var ms = new MemoryStream(initPayload, writable: false))
            using (var reader = new TraceFileReader(ms))
            {
                var header = reader.ReadHeader();
                Assert.That(header.Version, Is.EqualTo(TraceFileHeader.CurrentVersion));
                Assert.That(header.TimestampFrequency, Is.EqualTo(metadata.StopwatchFrequency));
                reader.ReadSystemDefinitions();
                reader.ReadArchetypes();
                reader.ReadComponentTypes();
                reader.ReadPhases();
            }

            // ── Emit records on a dedicated fresh thread so the slot buffer starts clean. NUnit reuses the test thread across tests
            // and `TelemetryConfig.ProfilerActive == true` means prior tests' engine workloads have silently filled this thread's
            // slot buffer with stale spans; emitting from a fresh thread gives us a clean slot without competing with that backlog.
            var emitThread = new Thread(() =>
            {
                using (var e = TyphonEvent.BeginBTreeInsert()) { }
                using (var e = TyphonEvent.BeginBTreeDelete()) { }

                {
                    var e = TyphonEvent.BeginClusterMigration(archetypeId: 7, migrationCount: 3, componentCount: 9);
                    e.Dispose();
                }
            })
            {
                IsBackground = true,
                Name = "TcpExporterIntegrationTests.Emit",
            };
            emitThread.Start();
            emitThread.Join();

            // The consumer thread runs on a 1 ms cadence, so three back-to-back emits may land in three separate blocks depending on
            // when each drain cycle wakes up relative to the producer calls. Read Block frames until we've seen everything we care about
            // (or we've read enough frames to conclude something is wrong) — aggregating kind counts across all frames.
            var kindCounts = new Dictionary<TraceEventKind, int>();
            var decodedClusterMigration = false;
            var framesRead = 0;

            while (framesRead < 22
                   && (!decodedClusterMigration
                       || kindCounts.GetValueOrDefault(TraceEventKind.BTreeInsert, 0) == 0
                       || kindCounts.GetValueOrDefault(TraceEventKind.BTreeDelete, 0) == 0))
            {
                var (frameType, framePayload) = ReadFrame(stream);
                framesRead++;
                // #302 Phase 4: FileTable + SourceLocationManifest frames are sent during the init handshake,
                // before the first Block frame. Skip past them — they're optional metadata, not record blocks.
                if (frameType == LiveFrameType.FileTable || frameType == LiveFrameType.SourceLocationManifest)
                {
                    continue;
                }
                Assert.That(frameType, Is.EqualTo(LiveFrameType.Block), $"Frame {framesRead} after INIT must be a Block (or FileTable/SourceLocationManifest)");

                var (uncompressedBytes, compressedBytes, recordCount) = TraceBlockEncoder.ReadBlockHeader(framePayload);
                Assert.That(recordCount, Is.GreaterThan(0), $"Block {framesRead} should carry at least one record");
                Assert.That(framePayload.Length, Is.GreaterThanOrEqualTo(TraceBlockEncoder.BlockHeaderSize + compressedBytes));

                var raw = new byte[uncompressedBytes];
                TraceBlockEncoder.DecodeBlock(
                    new ReadOnlySpan<byte>(framePayload, TraceBlockEncoder.BlockHeaderSize, compressedBytes),
                    uncompressedBytes,
                    raw);

                WalkRecords(raw, kindCounts, ref decodedClusterMigration);
            }

            Assert.Multiple(() =>
            {
                Assert.That(kindCounts.GetValueOrDefault(TraceEventKind.BTreeInsert, 0), Is.GreaterThanOrEqualTo(1),
                    "BTreeInsert record should arrive across the stream");
                Assert.That(kindCounts.GetValueOrDefault(TraceEventKind.BTreeDelete, 0), Is.GreaterThanOrEqualTo(1),
                    "BTreeDelete record should arrive across the stream");
                Assert.That(decodedClusterMigration, Is.True,
                    "ClusterMigration(archetypeId=7, migrationCount=3) should decode cleanly");
            });
        }
        finally
        {
            TyphonProfiler.Stop();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // --live-wait gate (synchronous "block until first viewer attaches")
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void LiveConnectTimeout_Zero_DoesNotBlock_DefaultBehavior()
    {
        int port;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var exporter = new TcpExporter(port, _registry.Profiler /* default 0 timeout */);
        TyphonProfiler.AttachExporter(exporter);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TyphonProfiler.Start(_registry.Profiler, BuildMetadata());
        sw.Stop();
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(500), "Initialize must not block when timeout = 0");
        Assert.That(exporter.HasClientEverConnected, Is.False);
    }

    [Test]
    public void LiveConnectTimeout_BlocksUntilTimeoutWhenNoClientConnects()
    {
        int port;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var exporter = new TcpExporter(port, _registry.Profiler, liveConnectTimeoutMs: 100);
        TyphonProfiler.AttachExporter(exporter);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TyphonProfiler.Start(_registry.Profiler, BuildMetadata());
        sw.Stop();
        // Should block ~100 ms (allow some slack for thread scheduling) and ultimately time out without a client.
        Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(80).And.LessThan(2000),
            $"Initialize should block ~100ms; actual: {sw.ElapsedMilliseconds}ms");
        Assert.That(exporter.HasClientEverConnected, Is.False, "no client connected → flag must stay false");
    }

    [Test]
    public void LiveConnectTimeout_UnblocksWhenClientConnectsBeforeTimeout()
    {
        int port;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var exporter = new TcpExporter(port, _registry.Profiler, liveConnectTimeoutMs: 2000);
        TyphonProfiler.AttachExporter(exporter);

        // Schedule a client to connect after a short delay; verify Initialize unblocks shortly after.
        var connectTask = System.Threading.Tasks.Task.Run(() =>
        {
            Thread.Sleep(50);
            var client = new TcpClient();
            ConnectWithRetry(client, "127.0.0.1", port, timeoutMs: 2000);
            return client;
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        TyphonProfiler.Start(_registry.Profiler, BuildMetadata());
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1500),
            $"Initialize should unblock within ~50-200ms after client connects; actual: {sw.ElapsedMilliseconds}ms");
        Assert.That(exporter.HasClientEverConnected, Is.True, "first-client signal must be set");

        // Cleanup the client we spawned for the test.
        var client = connectTask.Result;
        try { client.Close(); } catch { }
    }

    [Test]
    public void TcpExporter_RejectsNegativeLiveConnectTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TcpExporter(0, _registry.Profiler, liveConnectTimeoutMs: -5));
    }

    private static ProfilerSessionMetadata BuildMetadata()
    {
        return new ProfilerSessionMetadata(
            systems: Array.Empty<SystemDefinitionRecord>(),
            archetypes: Array.Empty<ArchetypeRecord>(),
            componentTypes: Array.Empty<ComponentTypeRecord>(),
            workerCount: 0,
            baseTickRate: 60.0f,
            startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(),
            stopwatchFrequency: System.Diagnostics.Stopwatch.Frequency,
            startedUtc: DateTime.UtcNow,
            samplingSessionStartQpc: 0);
    }

    private static void ConnectWithRetry(TcpClient client, string host, int port, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                client.Connect(host, port);
                return;
            }
            catch (SocketException)
            {
                if (Environment.TickCount64 >= deadline) throw;
                Thread.Sleep(25);
            }
        }
    }

    private static (LiveFrameType Type, byte[] Payload) ReadFrame(NetworkStream stream)
    {
        var header = new byte[LiveStreamProtocol.FrameHeaderSize];
        ReadExactly(stream, header);
        var (type, length) = LiveStreamProtocol.ReadFrameHeader(header);
        var payload = length > 0 ? new byte[length] : Array.Empty<byte>();
        if (length > 0) ReadExactly(stream, payload);
        return (type, payload);
    }

    private static void ReadExactly(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new EndOfStreamException("Socket closed mid-frame");
            read += n;
        }
    }

    /// <summary>Walk the raw record bytes and populate the tally + the cluster-migration flag.</summary>
    private static void WalkRecords(ReadOnlySpan<byte> records, Dictionary<TraceEventKind, int> kindCounts, ref bool decodedClusterMigration)
    {
        var pos = 0;
        while (pos + TraceRecordHeader.CommonHeaderSize <= records.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size < TraceRecordHeader.CommonHeaderSize || pos + size > records.Length) break;

            var record = records.Slice(pos, size);
            var kind = (TraceEventKind)record[2];
            kindCounts[kind] = kindCounts.GetValueOrDefault(kind, 0) + 1;

            if (kind == TraceEventKind.ClusterMigration)
            {
                var data = ClusterMigrationEventCodec.Decode(record);
                if (data.ArchetypeId == 7 && data.MigrationCount == 3)
                {
                    decodedClusterMigration = true;
                }
            }

            pos += size;
        }
    }
}
