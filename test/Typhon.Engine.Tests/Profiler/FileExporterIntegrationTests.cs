using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// End-to-end integration test for the v3 file exporter pipeline. Starts the profiler with a real <see cref="FileExporter"/>, emits a mix of
/// typed span events from the test thread, stops the profiler, then opens the written <c>.typhon-trace</c> file with <see cref="TraceFileReader"/>
/// and walks records via each kind's codec to assert round-trip fidelity.
/// </summary>
/// <remarks>
/// <b>Relies on:</b> <c>typhon.telemetry.json</c> in the test project root sets <c>ProfilerEnabled: true</c>, which turns
/// <see cref="Typhon.Engine.Observability.TelemetryConfig.ProfilerActive"/> on — required for producer-side emissions to actually land.
/// Tests run serially within <see cref="TyphonProfiler"/>'s lifecycle lock, so attaching/starting/stopping doesn't race with other fixtures.
/// </remarks>
[TestFixture]
public class FileExporterIntegrationTests
{
    private string _tempPath;
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = $"FileExporterIT-{Guid.NewGuid():N}" });
        _tempPath = Path.Combine(Path.GetTempPath(), $"typhon-it-{Guid.NewGuid():N}.typhon-trace");
    }

    [TearDown]
    public void TearDown()
    {
        // Belt-and-braces in case a prior Stop() failed
        try { TyphonProfiler.Stop(); } catch { }
        TyphonProfiler.ResetForTests();
        // Restore the default deny-list state so we don't poison sibling tests. After the 2026-04-30 re-tier, PageCacheDiskWrite is no
        // longer in the deny-list — its shipped default is unsuppressed (gated solely by Storage:PageCache:Enabled in JSON).
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheDiskWrite);
        if (_tempPath != null && File.Exists(_tempPath))
        {
            try { File.Delete(_tempPath); } catch { }
        }
    }

    [Test]
    public void StartEmitStop_RoundTripsTypedEvents()
    {
        // ── Arrange: metadata + exporter ──────────────────────────────────────────────────────────
        var metadata = BuildMetadata();
        var fileExporter = new FileExporter(_tempPath, _registry.Profiler);
        TyphonProfiler.AttachExporter(fileExporter);

        // ── Act: start, emit a mix of events on a FRESH thread, stop (flushes + closes file) ─────
        // PageCacheDiskWrite is reachable from JSON post-2026-04-30 re-tier (only PageCacheFetch stays default-suppressed). Calling
        // UnsuppressKind here is a no-op against the shipped default but keeps the test's intent self-documenting.
        TyphonEvent.UnsuppressKind(TraceEventKind.PageCacheDiskWrite);
        TyphonProfiler.Start(_registry.Profiler, metadata);

        // Important: run the emissions on a dedicated thread so the slot buffer starts clean. Using the NUnit test thread is unreliable
        // because `TelemetryConfig.ProfilerActive == true` in the test config means every prior test that touched engine code
        // (BTree/Transaction/PagedMMF spans) has been silently accumulating records in this thread's slot — without an exporter
        // attached those records pile up until the ring overflows. A fresh OS thread claims a fresh slot and sees a clean buffer.
        var emitThread = new Thread(() =>
        {
            // Tick + phase boundaries so the file has instant records too.
            TyphonEvent.SetCurrentTickNumber(1);
            TyphonEvent.EmitTickStart(System.Diagnostics.Stopwatch.GetTimestamp());
            TyphonEvent.EmitPhaseStart(Typhon.Profiler.TickPhase.SystemDispatch, System.Diagnostics.Stopwatch.GetTimestamp());

            // Scheduler chunk span.
            TyphonEvent.EmitSchedulerChunk(
                systemIdx: 5, chunkIdx: 0, totalChunks: 1,
                startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(),
                endTimestamp: System.Diagnostics.Stopwatch.GetTimestamp() + 100,
                entitiesProcessed: 42);

            // BTree span — no payload.
            using (var e = TyphonEvent.BeginBTreeInsert()) { }

            // Transaction commit with ComponentCount + ConflictDetected set.
            {
                var e = TyphonEvent.BeginTransactionCommit(tsn: 777);
                try
                {
                    e.ComponentCount = 3;
                    e.ConflictDetected = false;
                }
                finally
                {
                    e.Dispose();
                }
            }

            // ECS spawn with EntityId + Tsn set.
            {
                var e = TyphonEvent.BeginEcsSpawn(archetypeId: 9);
                try
                {
                    e.Tsn = 777;
                    e.EntityId = 0xDEADBEEFCAFEBABEUL;
                }
                finally
                {
                    e.Dispose();
                }
            }

            // PageCache DiskWrite with PageCount set.
            {
                var e = TyphonEvent.BeginPageCacheDiskWrite(filePageIndex: 17);
                try
                {
                    e.PageCount = 4;
                }
                finally
                {
                    e.Dispose();
                }
            }

            // Cluster migration.
            {
                var e = TyphonEvent.BeginClusterMigration(archetypeId: 3, migrationCount: 128, componentCount: 384);
                e.Dispose();
            }

            TyphonEvent.EmitPhaseEnd(Typhon.Profiler.TickPhase.SystemDispatch, System.Diagnostics.Stopwatch.GetTimestamp());
            TyphonEvent.EmitTickEnd(System.Diagnostics.Stopwatch.GetTimestamp(), overloadLevel: 0, tickMultiplier: 1);
        })
        {
            IsBackground = true,
            Name = "FileExporterIntegrationTests.Emit",
        };
        emitThread.Start();
        emitThread.Join();

        // Give the consumer thread several 1 ms cadence cycles to drain + fan out.
        Thread.Sleep(100);

        TyphonProfiler.Stop();

        // ── Assert: reopen file and walk records ──────────────────────────────────────────────────
        Assert.That(File.Exists(_tempPath), Is.True, "FileExporter should have produced a .typhon-trace file");

        using var stream = File.OpenRead(_tempPath);
        using var reader = new TraceFileReader(stream);

        var header = reader.ReadHeader();
        Assert.That(header.Version, Is.EqualTo(TraceFileHeader.CurrentVersion));
        Assert.That(header.TimestampFrequency, Is.EqualTo(metadata.StopwatchFrequency));

        reader.ReadSystemDefinitions();
        reader.ReadArchetypes();
        reader.ReadComponentTypes();
        reader.ReadPhases();
        reader.ReadStaticStructures();

        // Walk all blocks + all records; tally what we saw.
        var kindCounts = new Dictionary<TraceEventKind, int>();
        var decodedBTreeInsert = false;
        var decodedCommit = false;
        var decodedSpawn = false;
        var decodedDiskWrite = false;
        var decodedClusterMigration = false;

        while (reader.ReadNextBlock(out var records, out var recordCount))
        {
            WalkRecords(records.Span, kindCounts,
                ref decodedBTreeInsert, ref decodedCommit, ref decodedSpawn, ref decodedDiskWrite, ref decodedClusterMigration);
        }

        // We should have seen at least one of each emitted kind.
        Assert.Multiple(() =>
        {
            Assert.That(kindCounts.GetValueOrDefault(TraceEventKind.TickStart, 0), Is.GreaterThan(0), "TickStart emitted");
            Assert.That(kindCounts.GetValueOrDefault(TraceEventKind.TickEnd, 0), Is.GreaterThan(0), "TickEnd emitted");
            Assert.That(kindCounts.GetValueOrDefault(TraceEventKind.PhaseStart, 0), Is.GreaterThan(0), "PhaseStart emitted");
            Assert.That(kindCounts.GetValueOrDefault(TraceEventKind.PhaseEnd, 0), Is.GreaterThan(0), "PhaseEnd emitted");
            Assert.That(kindCounts.GetValueOrDefault(TraceEventKind.SchedulerChunk, 0), Is.GreaterThan(0), "SchedulerChunk emitted");
            Assert.That(decodedBTreeInsert, Is.True, "BTreeInsert decoded with correct kind byte");
            Assert.That(decodedCommit, Is.True, "TransactionCommit decoded with ComponentCount + ConflictDetected");
            Assert.That(decodedSpawn, Is.True, "EcsSpawn decoded with EntityId + Tsn");
            Assert.That(decodedDiskWrite, Is.True, "PageCacheDiskWrite decoded with PageCount=4");
            Assert.That(decodedClusterMigration, Is.True, "ClusterMigration decoded with MigrationCount=128");
        });
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

    /// <summary>Walk a block's raw record bytes, dispatch on kind, and populate the tally + per-kind flags.</summary>
    private static void WalkRecords(ReadOnlySpan<byte> records, Dictionary<TraceEventKind, int> kindCounts,
        ref bool decodedBTreeInsert, ref bool decodedCommit, ref bool decodedSpawn,
        ref bool decodedDiskWrite, ref bool decodedClusterMigration)
    {
        var pos = 0;
        while (pos + TraceRecordHeader.CommonHeaderSize <= records.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size < TraceRecordHeader.CommonHeaderSize || pos + size > records.Length) break;

            var record = records.Slice(pos, size);
            var kind = (TraceEventKind)record[2];
            kindCounts[kind] = kindCounts.GetValueOrDefault(kind, 0) + 1;

            switch (kind)
            {
                case TraceEventKind.BTreeInsert:
                    decodedBTreeInsert = true;
                    break;

                case TraceEventKind.TransactionCommit:
                {
                    var data = TransactionEventCodec.Decode(record);
                    if (data.Tsn == 777 && data.HasComponentCount && data.ComponentCount == 3 && data.HasConflictDetected && !data.ConflictDetected)
                    {
                        decodedCommit = true;
                    }
                    break;
                }

                case TraceEventKind.EcsSpawn:
                {
                    var data = EcsSpawnEventCodec.Decode(record);
                    if (data.ArchetypeId == 9 && data.HasEntityId && data.EntityId == 0xDEADBEEFCAFEBABEUL && data.HasTsn && data.Tsn == 777)
                    {
                        decodedSpawn = true;
                    }
                    break;
                }

                case TraceEventKind.PageCacheDiskWrite:
                {
                    var data = PageCacheEventCodec.Decode(record);
                    if (data.FilePageIndex == 17 && data.HasPageCount && data.PageCount == 4)
                    {
                        decodedDiskWrite = true;
                    }
                    break;
                }

                case TraceEventKind.ClusterMigration:
                {
                    var data = ClusterMigrationEventCodec.Decode(record);
                    if (data.ArchetypeId == 3 && data.MigrationCount == 128)
                    {
                        decodedClusterMigration = true;
                    }
                    break;
                }
            }

            pos += size;
        }
    }
}
