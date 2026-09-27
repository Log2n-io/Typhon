using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
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
[Category("Sensitive")] // live emit→async-drain→TCP roundtrip; the network drain is starved under parallel CPU load
                        // (same failure mode as FileExporterIntegrationTests). Runs in the serial quiet pass.
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
                reader.ReadTracks();
                reader.ReadDags();
                reader.ReadStaticStructures();
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

    /// <summary>
    /// #WB-01 — the Init frame carries the engine's schema, where it used to carry six zero counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The six v7 static-structure sections were written as literal zeros with a comment saying so, which made every attach session anonymous: the
    /// Workbench could show a tick rate but not the name of a component, so every "what is this thing?" pivot dead-ended. This asserts the whole
    /// round trip — the exporter writes the tables through <c>TraceFileWriter</c>, the client wraps the payload in a <see cref="TraceFileReader"/>,
    /// and the records come back out.
    /// </para>
    /// <para>
    /// The field-level assertions are the point, not decoration. A count that survives a layout mistake is exactly what a section-prefix-only check
    /// would pass on: the sections are variable-size and self-delimiting, so a wrong offset inside one record is read as the *next* record's length
    /// and the error surfaces as garbage three sections later, or as an exception that names the wrong table. Reading one field back per section is
    /// what makes a layout divergence between the file writer and this frame fail here rather than in the Workbench.
    /// </para>
    /// </remarks>
    [Test]
    public void AnInitFrameCarriesTheEnginesSchema_NotSixZeroCounts()
    {
        int port;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, DiscoveryPort))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var metadata = BuildMetadataWithSchema();
        var tcpExporter = new TcpExporter(port, _registry.Profiler);
        TyphonProfiler.AttachExporter(tcpExporter);
        TyphonProfiler.Start(_registry.Profiler, metadata);

        try
        {
            using var client = new TcpClient();
            ConnectWithRetry(client, "127.0.0.1", port, timeoutMs: 2000);
            using var stream = client.GetStream();
            stream.ReadTimeout = 5000;

            var (initType, initPayload) = ReadFrame(stream);
            Assert.That(initType, Is.EqualTo(LiveFrameType.Init));
            Assert.That(tcpExporter.SchemaOmittedFromInit, Is.False, "a two-record schema fits a frame by a wide margin; the fallback must not have run");

            using var ms = new MemoryStream(initPayload, writable: false);
            using var reader = new TraceFileReader(ms);
            reader.ReadHeader();
            reader.ReadSystemDefinitions();
            reader.ReadArchetypes();
            reader.ReadComponentTypes();
            reader.ReadTracks();
            reader.ReadDags();
            reader.ReadStaticStructures();

            Assert.Multiple(() =>
            {
                Assert.That(reader.ComponentDefinitions, Has.Count.EqualTo(1), "component definitions");
                Assert.That(reader.ComponentDefinitions[0].Name, Is.EqualTo("Position"));
                Assert.That(reader.ComponentDefinitions[0].Fields, Has.Length.EqualTo(2), "a component's field table travels with it");
                Assert.That(reader.ComponentDefinitions[0].Fields[1].Name, Is.EqualTo("Z"));
                Assert.That(reader.ComponentDefinitions[0].Fields[1].Offset, Is.EqualTo(4), "a field's byte offset is what the layout panel draws");

                Assert.That(reader.ArchetypeDefinitions, Has.Count.EqualTo(1), "archetype definitions");
                Assert.That(reader.ArchetypeDefinitions[0].Name, Is.EqualTo("Creature"));
                Assert.That(reader.ArchetypeDefinitions[0].ComponentTypeIds, Is.EqualTo(new[] { 11 }));

                Assert.That(reader.IndexCatalog, Has.Count.EqualTo(1), "index catalog");
                Assert.That(reader.IndexCatalog[0].IsSpatial, Is.True);

                Assert.That(reader.RuntimeConfig, Is.Not.Null, "runtime config presence flag");
                Assert.That(reader.RuntimeConfig.BaseTickRate, Is.EqualTo(50));

                Assert.That(reader.EventQueues, Has.Count.EqualTo(1), "event-queue catalog");
                Assert.That(reader.EventQueues[0].Name, Is.EqualTo("DamageEvents"));

                Assert.That(reader.ResourceGraphNodes, Has.Count.EqualTo(2), "resource graph");
                Assert.That(reader.ResourceGraphNodes[1].ParentId, Is.EqualTo(1), "the tree is reconstructed from ParentId");
            });
        }
        finally
        {
            TyphonProfiler.Stop();
        }
    }

    /// <summary>
    /// A schema too large for one frame is dropped from the Init rather than sent, because a receiver refuses an oversize frame and treats it as a
    /// malformed stream — so the alternative to "attach without schema" is not "attach with a big schema", it is "cannot attach".
    /// </summary>
    /// <remarks>
    /// Forced by a component table large enough to exceed <see cref="LiveStreamProtocol.MaxFrameBytes"/>. The assertion is the flag plus a parse of
    /// the payload: the fallback has to leave a wire-legal frame behind, not a truncated one, which is the part a size check alone would not catch.
    /// </remarks>
    [Test]
    public void ASchemaTooLargeForOneFrameIsOmitted_AndTheFrameStaysParseable()
    {
        int port;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, DiscoveryPort))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var metadata = BuildMetadataWithOversizeSchema();
        var tcpExporter = new TcpExporter(port, _registry.Profiler);
        TyphonProfiler.AttachExporter(tcpExporter);
        TyphonProfiler.Start(_registry.Profiler, metadata);

        try
        {
            using var client = new TcpClient();
            ConnectWithRetry(client, "127.0.0.1", port, timeoutMs: 2000);
            using var stream = client.GetStream();
            stream.ReadTimeout = 5000;

            var (initType, initPayload) = ReadFrame(stream);
            Assert.That(initType, Is.EqualTo(LiveFrameType.Init));
            Assert.That(tcpExporter.SchemaOmittedFromInit, Is.True, "the oversize branch must have run");
            Assert.That(LiveStreamProtocol.FrameHeaderSize + initPayload.Length, Is.LessThanOrEqualTo(LiveStreamProtocol.MaxFrameBytes),
                "the frame a receiver would refuse is the one this branch exists to avoid sending");

            using var ms = new MemoryStream(initPayload, writable: false);
            using var reader = new TraceFileReader(ms);
            reader.ReadHeader();
            reader.ReadSystemDefinitions();
            reader.ReadArchetypes();
            reader.ReadComponentTypes();
            reader.ReadTracks();
            reader.ReadDags();
            reader.ReadStaticStructures();
            Assert.That(reader.ComponentDefinitions, Is.Empty, "empty sections, in the layout the reader expects");
            Assert.That(reader.RuntimeConfig, Is.Null, "including the presence flag, which is the one section that is not a count");
        }
        finally
        {
            TyphonProfiler.Stop();
        }
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

    /// <summary>Metadata whose six static-structure tables are populated — one of each, with the inner tables (fields, component ids) non-empty.</summary>
    private static ProfilerSessionMetadata BuildMetadataWithSchema() =>
        new(
            systems: [],
            archetypes: [],
            componentTypes: [],
            workerCount: 1,
            baseTickRate: 50.0f,
            startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(),
            stopwatchFrequency: System.Diagnostics.Stopwatch.Frequency,
            startedUtc: DateTime.UtcNow,
            componentDefinitions:
            [
                new ComponentDefinitionRecord
                {
                    ComponentTypeId = 11, Name = "Position", Revision = 3, ComponentStorageSize = 8, ComponentStorageTotalSize = 8, IndicesCount = 1,
                    SpatialField = "X",
                    Fields =
                    [
                        new FieldDefinitionRecord { FieldId = 0, Name = "X", Offset = 0, Size = 4 },
                        new FieldDefinitionRecord { FieldId = 1, Name = "Z", Offset = 4, Size = 4 },
                    ],
                },
            ],
            archetypeDefinitions:
            [
                new ArchetypeDefinitionRecord { ArchetypeId = 2, Name = "Creature", Revision = 1, ComponentCount = 1, ComponentTypeIds = [11] },
            ],
            indexCatalog: [new IndexCatalogEntry { ComponentTypeId = 11, FieldId = 0, IsSpatial = true, IsAuto = true }],
            runtimeConfig: new RuntimeConfigRecord { BaseTickRate = 50, WorkerCount = 8, TelemetryRingCapacity = 256 },
            eventQueues: [new EventQueueRecord { QueueIndex = 0, Name = "DamageEvents", Capacity = 1024, EventTypeName = "Game.Damage" }],
            resourceGraphNodes:
            [
                new ResourceGraphNodeRecord { Id = 1, Name = "Engine", ParentId = -1 },
                new ResourceGraphNodeRecord { Id = 2, Name = "PageCache", ParentId = 1 },
            ]);

    /// <summary>
    /// Metadata whose component table alone cannot fit a frame. Each record carries a 200-byte name, so 40 000 of them overshoot
    /// <see cref="LiveStreamProtocol.MaxFrameBytes"/> without needing a field table to do it.
    /// </summary>
    private static ProfilerSessionMetadata BuildMetadataWithOversizeSchema()
    {
        var name = new string('c', 200);
        var components = new ComponentDefinitionRecord[40_000];
        for (var i = 0; i < components.Length; i++)
        {
            components[i] = new ComponentDefinitionRecord { ComponentTypeId = i, Name = name };
        }

        return new ProfilerSessionMetadata(
            systems: [],
            archetypes: [],
            componentTypes: [],
            workerCount: 1,
            baseTickRate: 50.0f,
            startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(),
            stopwatchFrequency: System.Diagnostics.Stopwatch.Frequency,
            startedUtc: DateTime.UtcNow,
            componentDefinitions: components);
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
                var dto = (Typhon.Profiler.Events.ClusterMigrationEventDto)Typhon.Profiler.Events.TraceEventDecoder.Decode(record, 0, 1);
                if (dto.ArchetypeId == 7 && dto.MigrationCount == 3)
                {
                    decodedClusterMigration = true;
                }
            }

            pos += size;
        }
    }
}
