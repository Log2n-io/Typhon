using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Round-trip and ordering coverage for <see cref="CacheSectionId.EntityLifecycle"/> — the trace-side spawn/destroy index behind the Workbench
/// entity lens (#620, design §4.4).
/// </summary>
/// <remarks>
/// The section stores **runs**, not one row per entity, because a batch spawn reserves its keys contiguously: <c>(FirstEntityKey, Count, RoutingId)</c>
/// reconstructs every id exactly. These tests pin that reconstruction and the <c>(TickNumber, FirstEntityKey)</c> ordering the cohort endpoint
/// binary-searches, since a silent ordering break would return a plausible-looking but wrong slice of a tick range.
/// </remarks>
[TestFixture]
internal sealed class EntityLifecycleCacheTests
{
    private static EntityLifecycleRun Spawn(uint tick, long firstKey, uint count, ushort routingId = 3, ushort archetypeId = 10) => new()
    {
        TickNumber = tick,
        ArchetypeId = archetypeId,
        RoutingId = routingId,
        FirstEntityKey = firstKey,
        Count = count,
        Kind = (byte)EntityLifecycleKind.Spawn,
    };

    private static EntityLifecycleRun Destroy(uint tick, long key, ushort routingId = 3) => new()
    {
        TickNumber = tick,
        // Destroys genuinely do not know the catalog id — the wire event carries only the entity id. The sentinel says so instead of guessing.
        ArchetypeId = EntityLifecycleRun.UnknownArchetypeId,
        RoutingId = routingId,
        FirstEntityKey = key,
        Count = 1,
        Kind = (byte)EntityLifecycleKind.Destroy,
    };

    [Test]
    public void Section_RoundTripsEveryField()
    {
        var written = new[]
        {
            Spawn(tick: 0, firstKey: 1, count: 200_000),   // a pre-tick bulk load — one row for 200K entities
            Spawn(tick: 4_102, firstKey: 500_000, count: 1_240),
            Destroy(tick: 4_110, key: 500_007),
        };

        var read = RoundTrip(written);

        Assert.That(read, Has.Count.EqualTo(3));
        for (var i = 0; i < written.Length; i++)
        {
            Assert.That(read[i].TickNumber, Is.EqualTo(written[i].TickNumber));
            Assert.That(read[i].ArchetypeId, Is.EqualTo(written[i].ArchetypeId));
            Assert.That(read[i].RoutingId, Is.EqualTo(written[i].RoutingId));
            Assert.That(read[i].FirstEntityKey, Is.EqualTo(written[i].FirstEntityKey));
            Assert.That(read[i].Count, Is.EqualTo(written[i].Count));
            Assert.That(read[i].Kind, Is.EqualTo(written[i].Kind));
        }
    }

    [Test]
    public void ARunReconstructsItsEntityIdsExactly()
    {
        // The load-bearing property. A raw id is (key << 16) | routingId, so a run's members are 65,536 apart in raw value and adjacent in key.
        var run = RoundTrip([Spawn(tick: 7, firstKey: 1_000, count: 4, routingId: 3)])[0];

        var ids = Enumerable.Range(0, (int)run.Count)
            .Select(n => new EntityId(run.FirstEntityKey + n, run.RoutingId).RawValue)
            .ToArray();

        Assert.That(ids, Is.EqualTo(new long[]
        {
            (1_000L << 16) | 3,
            (1_001L << 16) | 3,
            (1_002L << 16) | 3,
            (1_003L << 16) | 3,
        }));
    }

    [Test]
    public void AnEmptySectionIsWrittenAndReadsBackEmpty()
    {
        // Written even when empty, so "this capture recorded no spawns" is distinguishable from "this cache predates the section".
        Assert.That(RoundTrip([]), Is.Empty);
    }

    [Test]
    public void OneRowCarriesAWholeBulkLoad_NotOneRowPerEntity()
    {
        // The size argument, made a test so nobody "simplifies" the encoding into per-entity rows: 200K entities, one 24-byte row.
        var read = RoundTrip([Spawn(tick: 0, firstKey: 1, count: 200_000)]);

        Assert.That(read, Has.Count.EqualTo(1));
        Assert.That(read[0].Count, Is.EqualTo(200_000u));
        Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<EntityLifecycleRun>(), Is.EqualTo(24));
    }

    [Test]
    public void PreTickRunsSurvive_BecauseThatIsWhereSetupSpawnsLand()
    {
        // Everything spawned before the first tick is recorded at tick 0. Dropping those as "not in a tick" would empty the feature for any world
        // populated at startup — which is most of them.
        var read = RoundTrip([Spawn(tick: 0, firstKey: 1, count: 500), Spawn(tick: 1, firstKey: 501, count: 2)]);

        Assert.That(read.Where(r => r.TickNumber == 0).Sum(r => r.Count), Is.EqualTo(500));
    }

    [Test]
    public void SpawnAndDestroyAreDistinguishable_WhichIsWhatMakesACohortShrink()
    {
        var read = RoundTrip([Spawn(tick: 1, firstKey: 1, count: 3), Destroy(tick: 2, key: 2)]);

        Assert.That(read[0].Kind, Is.EqualTo((byte)EntityLifecycleKind.Spawn));
        Assert.That(read[1].Kind, Is.EqualTo((byte)EntityLifecycleKind.Destroy));
        Assert.That(read[1].ArchetypeId, Is.EqualTo(EntityLifecycleRun.UnknownArchetypeId),
            "a destroy must not claim a catalog archetype id it never received");
    }

    private static List<EntityLifecycleRun> RoundTrip(EntityLifecycleRun[] runs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"typhon-elc-{Guid.NewGuid():N}.typhon-trace-cache");
        try
        {
            using (var sink = FileCacheSink.Create(path))
            {
                var headerTemplate = new CacheHeader
                {
                    Magic = CacheHeader.MagicValue,
                    Version = CacheHeader.CurrentVersion,
                    ChunkerVersion = TraceFileCacheConstants.CurrentChunkerVersion,
                };
                CacheHeader.SetIdentifier(ref headerTemplate, new byte[32]);

                sink.WriteTrailer(
                    tickSummaries: Array.Empty<TickSummary>(),
                    globalMetrics: new GlobalMetricsFixed(),
                    systemAggregates: Array.Empty<SystemAggregateDuration>(),
                    chunkManifest: Array.Empty<ChunkManifestEntry>(),
                    spanNames: new Dictionary<int, string>(),
                    sourceMetadataBytes: default,
                    headerTemplate: headerTemplate,
                    systemTickSummaries: Array.Empty<SystemTickSummary>(),
                    queueTickSummaries: Array.Empty<QueueTickSummary>(),
                    postTickSummaries: Array.Empty<PostTickSummary>(),
                    queueIdToName: new Dictionary<ushort, string>(),
                    systemArchetypeTouches: Array.Empty<SystemArchetypeTouchSummary>(),
                    entityLifecycleRuns: runs);
            }

            using var reader = new TraceFileCacheReader(File.OpenRead(path));
            return reader.EntityLifecycleRuns.ToList();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
