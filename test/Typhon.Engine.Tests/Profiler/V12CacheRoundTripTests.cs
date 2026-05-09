using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Round-trip tests for the v12 cache sections introduced for #311 — <see cref="CacheSectionId.SystemTickSummaries"/>,
/// <see cref="CacheSectionId.QueueTickSummaries"/>, <see cref="CacheSectionId.PostTickSummaries"/>, and
/// <see cref="CacheSectionId.QueueNameTable"/>. Each test writes a small set of records, closes the writer, opens a
/// reader, and asserts the round-tripped values match.
/// </summary>
[TestFixture]
public sealed class V12CacheRoundTripTests
{
    [Test]
    public void SystemTickSummary_RoundTrip()
    {
        var rows = new[]
        {
            new SystemTickSummary
            {
                TickNumber = 1, SystemIndex = 0, SkipReasonCode = 0, Flags = 0,
                StartUs = 100, EndUs = 250, ReadyUs = 95, DurationUs = 150f,
                EntitiesProcessed = 42, WorkersTouched = 4, ChunksProcessed = 8, _reserved = 0,
                TotalCpuUs = 600,
            },
            new SystemTickSummary
            {
                TickNumber = 1, SystemIndex = 1, SkipReasonCode = 3, Flags = 0,
                StartUs = 0, EndUs = 0, ReadyUs = 0, DurationUs = 0f,
                EntitiesProcessed = 0, WorkersTouched = 0, ChunksProcessed = 0, _reserved = 0,
                TotalCpuUs = 0,
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"v12-sys-{System.Guid.NewGuid():N}.cache");
        try
        {
            WriteCache(path, systemRows: rows);
            using var reader = new TraceFileCacheReader(File.OpenRead(path));
            Assert.That(reader.SystemTickSummaries, Has.Count.EqualTo(2));
            Assert.That(reader.SystemTickSummaries[0].DurationUs, Is.EqualTo(150f));
            Assert.That(reader.SystemTickSummaries[0].EntitiesProcessed, Is.EqualTo(42));
            Assert.That(reader.SystemTickSummaries[0].WorkersTouched, Is.EqualTo(4));
            Assert.That(reader.SystemTickSummaries[0].TotalCpuUs, Is.EqualTo(600));
            Assert.That(reader.SystemTickSummaries[1].SkipReasonCode, Is.EqualTo(3));
            Assert.That(reader.SystemTickSummaries[1].TotalCpuUs, Is.EqualTo(0));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void QueueTickSummary_AndQueueNameTable_RoundTrip()
    {
        var rows = new[]
        {
            new QueueTickSummary { TickNumber = 5, QueueId = 0, _reserved = 0, PeakDepth = 100, EndOfTickDepth = 12, OverflowCount = 0, Produced = 50, Consumed = 38 },
            new QueueTickSummary { TickNumber = 5, QueueId = 1, _reserved = 0, PeakDepth = 1024, EndOfTickDepth = 0, OverflowCount = 3, Produced = 1027, Consumed = 1027 },
        };
        var names = new Dictionary<ushort, string> { [0] = "DamageQueue", [1] = "DeathQueue" };

        var path = Path.Combine(Path.GetTempPath(), $"v12-queue-{System.Guid.NewGuid():N}.cache");
        try
        {
            WriteCache(path, queueRows: rows, queueNames: names);
            using var reader = new TraceFileCacheReader(File.OpenRead(path));
            Assert.That(reader.QueueTickSummaries, Has.Count.EqualTo(2));
            Assert.That(reader.QueueTickSummaries[0].PeakDepth, Is.EqualTo(100));
            Assert.That(reader.QueueTickSummaries[1].OverflowCount, Is.EqualTo(3));
            Assert.That(reader.QueueIdToName, Has.Count.EqualTo(2));
            Assert.That(reader.QueueIdToName[0], Is.EqualTo("DamageQueue"));
            Assert.That(reader.QueueIdToName[1], Is.EqualTo("DeathQueue"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void PostTickSummary_RoundTrip()
    {
        var rows = new[]
        {
            new PostTickSummary
            {
                TickNumber = 10, _reserved = 0,
                WriteTickFenceUs = 12.5f, WalFlushUs = 100f, SubscriptionOutputUs = 5.2f,
                TierIndexRebuildUs = 0f, DormancySweepUs = 1.1f, TierBudgetUs = 0f,
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"v12-post-{System.Guid.NewGuid():N}.cache");
        try
        {
            WriteCache(path, postRows: rows);
            using var reader = new TraceFileCacheReader(File.OpenRead(path));
            Assert.That(reader.PostTickSummaries, Has.Count.EqualTo(1));
            Assert.That(reader.PostTickSummaries[0].WalFlushUs, Is.EqualTo(100f));
            Assert.That(reader.PostTickSummaries[0].WriteTickFenceUs, Is.EqualTo(12.5f));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void EmptyV12Sections_PresentInTrailer_DoNotBreakReader()
    {
        // Smoke test: explicitly write empty v12 sections + verify reader sees zero rows.
        var path = Path.Combine(Path.GetTempPath(), $"v12-empty-{System.Guid.NewGuid():N}.cache");
        try
        {
            WriteCache(path);
            using var reader = new TraceFileCacheReader(File.OpenRead(path));
            Assert.That(reader.SystemTickSummaries, Is.Empty);
            Assert.That(reader.QueueTickSummaries, Is.Empty);
            Assert.That(reader.PostTickSummaries, Is.Empty);
            Assert.That(reader.QueueIdToName, Is.Empty);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WriteCache(
        string path,
        SystemTickSummary[] systemRows = null,
        QueueTickSummary[] queueRows = null,
        PostTickSummary[] postRows = null,
        Dictionary<ushort, string> queueNames = null)
    {
        using var sink = FileCacheSink.Create(path);
        // FileCacheSink.WriteTrailer auto-opens the FoldedChunkData section when no chunks were appended; no AppendChunk needed here.
        var headerTemplate = new CacheHeader
        {
            Magic = CacheHeader.MagicValue,
            Version = CacheHeader.CurrentVersion,
            ChunkerVersion = TraceFileCacheConstants.CurrentChunkerVersion,
        };
        // Identifier doesn't matter for these tests.
        var ident = new byte[32];
        CacheHeader.SetIdentifier(ref headerTemplate, ident);

        sink.WriteTrailer(
            tickSummaries: System.Array.Empty<TickSummary>(),
            globalMetrics: new GlobalMetricsFixed(),
            systemAggregates: System.Array.Empty<SystemAggregateDuration>(),
            chunkManifest: System.Array.Empty<ChunkManifestEntry>(),
            spanNames: new Dictionary<int, string>(),
            sourceMetadataBytes: default,
            headerTemplate: headerTemplate,
            systemTickSummaries: systemRows ?? System.Array.Empty<SystemTickSummary>(),
            queueTickSummaries: queueRows ?? System.Array.Empty<QueueTickSummary>(),
            postTickSummaries: postRows ?? System.Array.Empty<PostTickSummary>(),
            queueIdToName: queueNames ?? new Dictionary<ushort, string>(),
            systemArchetypeTouches: System.Array.Empty<SystemArchetypeTouchSummary>());
    }
}
